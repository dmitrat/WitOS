using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Runtime;

/// <summary>
/// Goal 2 literally (plan step N4.4, Implementation Strategy §57): the .NET compatibility suite and the portable
/// application published on one host by the stock SDK without a RID (publish-apps, the same MSBuild as Visual Studio's)
/// run on witos as exactly those bytes (runtime-published), and every check of the suite must end as it ended for the
/// publish of the host that built the runtime, which <see cref="RuntimeCompatibility"/> records. CI publishes on Windows
/// and runs the upload on both ISAs.
/// </summary>
internal static class RuntimePublished
{
    #region Constants

    /// <summary>
    /// The manifest publish-apps writes beside the applications: the host, the SDK and every file with its SHA-256.
    /// </summary>
    public const string MANIFEST = "published.json";

    /// <summary>
    /// Where publish-apps publishes and runtime-published looks when told no directory.
    /// </summary>
    public const string DEFAULT_DIRECTORY = "artifacts/published-apps";

    /// <summary>
    /// The applications: the name under /app and in the published directory, the project under the repository and
    /// the program dotnet runs.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Project, string Program)> APPLICATIONS =
    [
        ("compatibility", RuntimeCompatibility.SUITE, "DotNetCompatibility.dll"), ("portable-app", RuntimeWitos.PORTABLE_APP, "PortableApp.dll")
    ];

    private const int TIMEOUT_SECONDS = 1800;

    private static readonly JsonSerializerOptions JSON = new() { WriteIndented = true };

    #endregion

    #region Functions

    /// <summary>
    /// Publishes the applications on this host with the stock SDK and no RID, each from a copy of its project apart from
    /// the repository's build properties, into <paramref name="directory"/> with the manifest (the publish-apps command).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="directory">The directory to publish into, under the repository when relative; emptied first.</param>
    /// <returns>The manifest.</returns>
    public static async Task<PublishedManifest> PublishAsync(string root, string directory)
    {
        directory = Path.GetFullPath(directory, root);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        var work = Path.Combine(root, "artifacts", "publish-apps");
        Directory.CreateDirectory(work);
        foreach (var (name, project, _) in APPLICATIONS)
        {
            var published = await RuntimeApps.PublishAsync(root, work, project, name);
            foreach (var file in Directory.EnumerateFiles(published, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(directory, name, Path.GetRelativePath(published, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }
        var sdk = (await Processes.RunAsync("dotnet", ["--version"], root)).Output.Trim();
        var manifest = await WriteManifestAsync(directory, HostName(), sdk);
        Console.WriteLine($"Published on {manifest.Host} ({manifest.Description}) with SDK {manifest.Sdk}: {manifest.Files.Count} files, " +
                          $"{manifest.Files.Sum(file => file.Bytes)} bytes under {directory}.");
        return manifest;
    }

    /// <summary>
    /// Writes the manifest of a published directory: every file below it but the manifest, with its size and SHA-256.
    /// </summary>
    /// <param name="directory">The published directory.</param>
    /// <param name="host">The host that published.</param>
    /// <param name="sdk">The SDK that published.</param>
    /// <returns>The manifest written.</returns>
    public static async Task<PublishedManifest> WriteManifestAsync(string directory, string host, string sdk)
    {
        var files = new List<PublishedFile>();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            if (relative == MANIFEST)
                continue;
            await using var stream = File.OpenRead(file);
            files.Add(new PublishedFile(relative, stream.Length, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream))));
        }
        var manifest = new PublishedManifest(host, RuntimeInformation.OSDescription, sdk, DateTime.UtcNow, files);
        await File.WriteAllTextAsync(Path.Combine(directory, MANIFEST), JsonSerializer.Serialize(manifest, JSON));
        return manifest;
    }

    /// <summary>
    /// Reads a published directory's manifest and requires the directory to hold exactly the files it names, each of
    /// the size and the SHA-256 recorded.
    /// </summary>
    /// <param name="directory">The published directory.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="InvalidDataException">The manifest is missing or the files are not the ones it names.</exception>
    public static async Task<PublishedManifest> VerifyAsync(string directory)
    {
        var path = Path.Combine(directory, MANIFEST);
        if (!File.Exists(path))
            throw new InvalidDataException($"{directory} holds no {MANIFEST}: publish-apps writes it.");
        var manifest = JsonSerializer.Deserialize<PublishedManifest>(await File.ReadAllTextAsync(path)) ??
                       throw new InvalidDataException($"{path} is empty.");
        var present = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/')).Where(file => file != MANIFEST).ToHashSet(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            if (!present.Remove(file.Path))
                throw new InvalidDataException($"{directory} lacks {file.Path}, which the manifest names.");
            await using var stream = File.OpenRead(Path.Combine(directory, file.Path));
            if (stream.Length != file.Bytes)
                throw new InvalidDataException($"{file.Path} has {stream.Length} bytes, not the {file.Bytes} the manifest records.");
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
            if (hash != file.Sha256)
                throw new InvalidDataException($"{file.Path} hashes to {hash}, not the {file.Sha256} the manifest records.");
        }
        if (present.Count > 0)
            throw new InvalidDataException($"{directory} holds {present.Order(StringComparer.Ordinal).First()}, which the manifest does not name.");
        foreach (var (name, _, program) in APPLICATIONS)
            if (!manifest.Files.Any(file => file.Path == $"{name}/{program}"))
                throw new InvalidDataException($"The manifest names no {name}/{program}.");
        return manifest;
    }

    /// <summary>
    /// Runs the published applications on witos (the runtime-published command): the package holds the bytes of
    /// <paramref name="directory"/> under /app, after the manifest verified them, and the suite's checks must end as
    /// the record of runtime-compatibility's run on this runtime says.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="directory">The published directory; this host's own publish when null.</param>
    /// <returns>When the applications ran and every check ended as recorded.</returns>
    /// <exception cref="InvalidOperationException">No record of runtime-compatibility's run, or a check ended otherwise
    /// than it did there.</exception>
    public static async Task RunAsync(string root, KernelArchitecture architecture, string? directory)
    {
        if (directory is null)
            await PublishAsync(root, DEFAULT_DIRECTORY);
        directory = Path.GetFullPath(directory ?? DEFAULT_DIRECTORY, root);
        var manifest = await VerifyAsync(directory);
        Console.WriteLine($"{manifest.Files.Count} files published on {manifest.Host} with SDK {manifest.Sdk}: exactly the bytes the manifest records.");
        var record = await RuntimeCompatibility.ReadRecordAsync(root, architecture) ??
                     throw new InvalidOperationException(
                         $"No record of the suite's run on witos-{architecture.Name} ({RuntimeCompatibility.RecordPath(root, architecture)}): runtime-compatibility writes it.");
        var output = Path.Combine(root, "artifacts", architecture.Name, KernelImageBuilder.RUNTIME_PUBLISHED_SCENARIO);
        Directory.CreateDirectory(output);
        var package = await BuildPackageAsync(root, output, architecture, directory);
        var log = RuntimeApps.SerialLog(root, architecture, KernelImageBuilder.RUNTIME_PUBLISHED_SCENARIO);
        if (File.Exists(log))
            File.Delete(log);
        await RuntimeApps.BootAsync(root, architecture, KernelImageBuilder.RUNTIME_PUBLISHED_SCENARIO, RequiredLines(architecture), TIMEOUT_SECONDS,
            package: package, terminal: RuntimeCompatibility.Script());
        var checks = RuntimeCompatibility.ReadChecks(await File.ReadAllLinesAsync(log));
        var differences = Compare(record.Checks, checks);
        var report = new PublishedReport(architecture.Name, manifest.Host, manifest.Sdk, manifest.Files.Count, record.PublishedOn, checks.Count,
            differences);
        var path = Path.Combine(root, "artifacts", "runtime-witos", $"compatibility-published-{architecture.Name}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JSON));
        if (differences.Count > 0)
            throw new InvalidOperationException(
                $"The suite published on {manifest.Host} ended otherwise than the one published on {record.PublishedOn} on witos-{architecture.Name}:\n" +
                string.Join("\n", differences));
        Console.WriteLine($"Goal 2 on witos-{architecture.Name}: the applications published on {manifest.Host} ran unchanged, " +
                          $"each of the suite's {checks.Count} checks ending as for the publish on {record.PublishedOn}.");
    }

    /// <summary>
    /// The differences between two runs of the suite: a check one run has and the other lacks, or one whose verdicts differ.
    /// </summary>
    /// <param name="recorded">The recorded run's verdict of each check.</param>
    /// <param name="observed">This run's.</param>
    /// <returns>A line per difference; none when the runs agree.</returns>
    public static IReadOnlyList<string> Compare(IReadOnlyDictionary<string, string> recorded, IReadOnlyDictionary<string, string> observed)
    {
        var differences = new List<string>();
        foreach (var (id, verdict) in recorded.OrderBy(check => check.Key, StringComparer.Ordinal))
        {
            if (!observed.TryGetValue(id, out var seen))
                differences.Add($"{id}: {verdict} recorded, not run");
            else if (seen != verdict)
                differences.Add($"{id}: {verdict} recorded, {seen} now");
        }
        differences.AddRange(observed.Keys.Except(recorded.Keys).Order(StringComparer.Ordinal)
            .Select(id => $"{id}: {observed[id]} now, not recorded"));
        return differences;
    }

    /// <summary>
    /// The package of the runtime-published scenario: the device services, the .NET install and the published
    /// applications under /app, each run by /bin/init; this host's own publish when the scenario is built alone.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="directory">The published directory; this host's own publish when null.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> BuildPackageAsync(string root, string output,
        KernelArchitecture architecture, string? directory = null)
    {
        if (directory is null)
            await PublishAsync(root, DEFAULT_DIRECTORY);
        directory = Path.GetFullPath(directory ?? DEFAULT_DIRECTORY, root);
        return await RuntimeApps.PackageAsync(root, output, architecture,
            [.. APPLICATIONS.Select(application => (application.Name, Path.Combine(directory, application.Name)))],
            [.. APPLICATIONS.Select(application => $"/dotnet/dotnet /app/{application.Name}/{application.Program}")]);
    }

    /// <summary>
    /// The lines the scenario requires: the suite's, and the portable application's checks and exit.
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The required lines.</returns>
    public static string[] RequiredLines(KernelArchitecture architecture) =>
    [
        .. RuntimeCompatibility.RequiredLines(architecture), KernelArchitecture.PortableAppPassedLine,
        "[APPS-INIT] /dotnet/dotnet /app/portable-app/PortableApp.dll exited with 0"
    ];

    /// <summary>
    /// This host's name in a manifest and a record.
    /// </summary>
    /// <returns>Windows, Linux, macOS or the runtime's description.</returns>
    public static string HostName() =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "macOS" : RuntimeInformation.OSDescription;

    #endregion

    #region Nested Types

    /// <summary>
    /// A published file: its path under the published directory with '/', its size and its SHA-256 in lower-case hex.
    /// </summary>
    public sealed record PublishedFile(string Path, long Bytes, string Sha256);

    /// <summary>
    /// The manifest of a publish: the host that published (Windows, Linux), the runtime's description of it, the SDK's
    /// version, when, and every file.
    /// </summary>
    public sealed record PublishedManifest(string Host, string Description, string Sdk, DateTime PublishedUtc, IReadOnlyList<PublishedFile> Files);

    /// <summary>
    /// The report of a run of the published applications on witos.
    /// </summary>
    public sealed record PublishedReport(string Architecture, string PublishedOn, string Sdk, int Files, string ComparedWithPublishOn, int Checks,
        IReadOnlyList<string> Differences);

    #endregion
}
