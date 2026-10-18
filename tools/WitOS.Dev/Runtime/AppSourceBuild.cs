using WitOS.Dev.Host;

namespace WitOS.Dev.Runtime;

/// <summary>
/// A base application built from its source (ADR 0027, plan step C4.2): the repository at the commit the catalog pins,
/// fetched once with its submodules under .tools/apps-src, and the project published by the stock SDK as a portable
/// application — no RID, no apphost (the layer writes its own launcher), the catalog's MSBuild properties beside — into
/// .tools/apps, once for the commit and the properties. A release that publishes only binaries of Linux's RIDs runs on
/// WitOS this way.
/// </summary>
internal static class AppSourceBuild
{
    #region Constants

    private const int FETCH_SECONDS = 1800;

    private const int PUBLISH_SECONDS = 3600;

    #endregion

    #region Functions

    /// <summary>
    /// The application's files, by their paths in its directory.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="entry">The catalog entry, of the source kind.</param>
    /// <returns>Paths in the application's directory and the host's files.</returns>
    /// <exception cref="InvalidDataException">The checkout is another commit, or the publish holds no command's assembly.</exception>
    /// <exception cref="InvalidOperationException">The publish failed.</exception>
    public static async Task<IReadOnlyList<(string Path, string Source)>> FilesAsync(string root, AppEntry entry)
    {
        var source = entry.Source;
        var commit = source.Commit!;
        var checkout = Path.Combine(root, ".tools", "apps-src", $"{entry.Id}-{commit[..12]}");
        if (!Directory.Exists(Path.Combine(checkout, ".git")))
        {
            if (Directory.Exists(checkout))
                Directory.Delete(checkout, recursive: true);
            Directory.CreateDirectory(checkout);
            await Git.RunAsync(checkout, ["init", "-q"]);
            await Git.RunAsync(checkout, ["remote", "add", "origin", source.Repository!]);
            await Git.RunAsync(checkout, ["fetch", "-q", "--depth=1", "origin", commit], FETCH_SECONDS);
            await Git.RunAsync(checkout, ["checkout", "-q", "--detach", commit], FETCH_SECONDS);
            await Git.RunAsync(checkout, ["submodule", "update", "-q", "--init", "--recursive", "--depth=1"], FETCH_SECONDS);
        }
        if ((await Git.RunAsync(checkout, ["rev-parse", "HEAD"])).Trim() != commit)
            throw new InvalidDataException($"The checkout of {entry.Id} at {checkout} is not the pinned commit {commit}; it was not changed.");
        var properties = (source.Properties ?? []).Select(property => "-p:" + property).ToArray();
        var output = Path.Combine(root, ".tools", "apps", $"{entry.Id}-{entry.Version}");
        var stamp = output + ".built-" + commit[..12] + "-" +
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', properties))))[..12];
        if (!File.Exists(stamp))
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
            var publish = await Processes.RunAsync("dotnet",
                ["publish", source.Project!, "-c", "Release", "-o", output, "-p:UseAppHost=false", "-p:NuGetAudit=false", .. properties, "-nologo"],
                checkout, PUBLISH_SECONDS);
            if (publish.TimedOut || publish.ExitCode != 0)
                throw new InvalidOperationException($"The publish of {entry.Id} failed (exit {publish.ExitCode}): {publish.Output} {publish.Error}");
            await File.WriteAllTextAsync(stamp, commit + "\n");
        }
        foreach (var command in entry.Commands)
        {
            if (!File.Exists(Path.Combine(output, command.Assembly)))
                throw new InvalidDataException($"The publish of {entry.Id} holds no {command.Assembly} for {command.Name}.");
        }
        return [.. Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => (Path.GetRelativePath(output, path).Replace('\\', '/'), path))];
    }

    #endregion
}
