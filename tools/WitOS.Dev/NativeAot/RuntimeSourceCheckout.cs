using WitOS.Dev.Host;

namespace WitOS.Dev.NativeAot;

/// <summary>
/// Sparse Git checkout of the pinned upstream runtime under .tools/upstream; never overwrites local changes.
/// </summary>
internal static class RuntimeSourceCheckout
{
    #region Functions

    /// <summary>
    /// Creates or reuses the sparse checkout of the pinned revision and verifies its remote, revision and clean state.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="pin">Pinned upstream sources.</param>
    /// <returns>Source tree path.</returns>
    internal static async Task<string> PrepareAsync(string root, UpstreamSourceLock pin)
    {
        var source = Path.Combine(root, ".tools", "upstream", $"runtime-{pin.RuntimeVersion}");
        if (!Directory.Exists(Path.Combine(source, ".git")))
        {
            if (Directory.Exists(source) && Directory.EnumerateFileSystemEntries(source).Any())
                throw new InvalidOperationException("Runtime source directory exists without Git metadata; refusing to overwrite it.");
            Directory.CreateDirectory(source);
            await GitAsync(source, ["init"]);
            await GitAsync(source, ["remote", "add", "origin", pin.RuntimeRepository + ".git"]);
            await GitAsync(source, ["fetch", "--depth=1", "--filter=blob:none", "origin", pin.RuntimeCommit], 600);
            await GitAsync(source, ["sparse-checkout", "init", "--cone"]);
            await GitAsync(source, ["sparse-checkout", "set", "eng", "src/coreclr", "src/native"]);
            await GitAsync(source, ["checkout", "--detach", pin.RuntimeCommit], 600);
        }
        var revision = (await GitAsync(source, ["rev-parse", "HEAD"])).Trim();
        var remote = (await GitAsync(source, ["remote", "get-url", "origin"])).Trim();
        if (revision != pin.RuntimeCommit || (remote.TrimEnd('/') != pin.RuntimeRepository && remote.TrimEnd('/') != pin.RuntimeRepository + ".git"))
            throw new InvalidDataException("Existing runtime checkout differs from the pinned repository/commit; it was not changed.");
        await RequireCleanAsync(source);
        await GitAsync(source, ["sparse-checkout", "add", "eng", "src/coreclr", "src/native"], 600);
        await RequireCleanAsync(source);
        return source;
    }

    /// <summary>
    /// Throws when the checkout has local changes or untracked files.
    /// </summary>
    /// <param name="source">Runtime source tree.</param>
    internal static async Task RequireCleanAsync(string source)
    {
        if (!string.IsNullOrWhiteSpace(await GitAsync(source, ["status", "--porcelain", "--untracked-files=normal"])))
            throw new InvalidDataException("Runtime source checkout has local changes; refusing to build or overwrite them.");
    }

    /// <summary>
    /// Runs Git in the runtime checkout and throws on failure or timeout.
    /// </summary>
    /// <param name="source">Runtime source tree.</param>
    /// <param name="args">Git arguments.</param>
    /// <param name="timeout">Timeout in seconds.</param>
    /// <returns>Standard output.</returns>
    internal static async Task<string> GitAsync(string source, string[] args, int timeout = 60)
    {
        var result = await Processes.RunAsync("git", ["-c", $"safe.directory={source.Replace('\\', '/')}", .. args], source, timeout);
        if (result.TimedOut || result.ExitCode != 0)
            throw new InvalidOperationException($"Runtime Git {args[0]} failed. {result.Output}\n{result.Error}");
        return result.Output;
    }

    #endregion
}
