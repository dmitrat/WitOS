using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WitOS.Dev;

internal static class RuntimeExperiment
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const string ExperimentPath = "experiments/NativeAotProbe";
    private static readonly string[] ProbeCases =
    [
        "NativeAotIdentity", "GcRootsAndFinalizer", "GcCompositeRoots", "GcRootsAcrossUnwind", "ExceptionsAndFinally",
        "ThreadsTlsMonitorAndGc", "WaitSignalResetAndTimeout", "TasksCancellationAndClock"
    ];

    internal sealed record SourceFile(string Path, string Sha256);
    internal sealed record SourceLock(int SchemaVersion, string RuntimeVersion, string RuntimeRepository,
        string RuntimeTag, string RuntimeCommit, string PackageRepository, string PackageCommit,
        string SourceManifestPath, string SourceManifestSha256, SourceFile[] Sources);

    internal static SourceLock ReadLock(string root)
    {
        var data = JsonSerializer.Deserialize<SourceLock>(
            File.ReadAllText(Path.Combine(root, ExperimentPath, "upstream.lock.json")), Json)
            ?? throw new InvalidDataException("Runtime source lock is empty.");
        if (data.SchemaVersion != 1 || data.RuntimeVersion != "10.0.8" ||
            data.RuntimeRepository != "https://github.com/dotnet/runtime" ||
            data.PackageRepository != "https://github.com/dotnet/dotnet" ||
            !Regex.IsMatch(data.RuntimeCommit, "^[0-9a-f]{40}$") ||
            !Regex.IsMatch(data.PackageCommit, "^[0-9a-f]{40}$") ||
            data.Sources.Length == 0)
            throw new InvalidDataException("Invalid or unsupported runtime source lock.");
        return data;
    }

    public static async Task AuditAsync(string root)
    {
        var pin = ReadLock(root);
        var cache = Path.Combine(root, ".tools", "runtime-audit");
        Directory.CreateDirectory(cache);
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var verified = new List<object>();
        foreach (var source in pin.Sources)
        {
            var file = await FetchAsync(client, cache, "runtime", pin.RuntimeCommit, source.Path, source.Sha256);
            verified.Add(new { source.Path, source.Sha256, bytes = new FileInfo(file).Length });
        }
        var manifestPath = await FetchAsync(client, cache, "dotnet", pin.PackageCommit,
            pin.SourceManifestPath, pin.SourceManifestSha256);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var runtime = manifest.RootElement.GetProperty("repositories").EnumerateArray()
            .Single(repository => repository.GetProperty("path").GetString() == "runtime");
        if (runtime.GetProperty("remoteUri").GetString() != pin.RuntimeRepository ||
            runtime.GetProperty("commitSha").GetString() != pin.RuntimeCommit)
            throw new InvalidDataException("Package provenance does not match the pinned runtime source.");

        var output = Path.Combine(root, "artifacts", "runtime-probe");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "source-audit.json"), JsonSerializer.Serialize(new
        {
            pin.RuntimeVersion, pin.RuntimeCommit, pin.PackageCommit,
            provenanceVerified = true,
            scope = "Selected source files only; not an exhaustive runtime dependency closure.",
            verifiedSources = verified
        }, Json));
        Console.WriteLine($"Verified {verified.Count} source files at {pin.RuntimeCommit}.");
        Console.WriteLine($"Package VMR {pin.PackageCommit} maps to the same runtime commit.");
    }

    internal static async Task<string> FetchAsync(HttpClient client, string cache, string repository, string revision, string path, string expectedHash)
    {
        if (!Regex.IsMatch(expectedHash, "^[0-9a-f]{64}$") || Path.IsPathRooted(path) ||
            path.Contains('\\') || path.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Invalid source path or checksum.");
        var directory = Path.GetFullPath(Path.Combine(cache, repository, revision));
        var destination = Path.GetFullPath(Path.Combine(directory, path));
        if (!destination.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source destination escaped the cache.");
        if (!File.Exists(destination))
        {
            var url = $"https://raw.githubusercontent.com/dotnet/{repository}/{revision}/{path}";
            var data = await client.GetByteArrayAsync(url);
            VerifyHash(data, expectedHash, path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(destination, data);
        }
        VerifyHash(await File.ReadAllBytesAsync(destination), expectedHash, path);
        return destination;
    }

    private static void VerifyHash(byte[] data, string expected, string label)
    {
        var actual = Convert.ToHexString(SHA256.HashData(data));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SHA-256 mismatch: {label}.");
    }

    internal static string[] VerifyPublishedPackages(string root, SourceLock pin)
    {
        var packageCache = Path.Combine(root, ".tools", "nuget");
        string[] verifiedPackages = ["runtime.win-x64.microsoft.dotnet.ilcompiler", "microsoft.netcore.app.runtime.nativeaot.win-x64"];
        foreach (var packageId in verifiedPackages)
        {
            var package = Path.Combine(packageCache, packageId, pin.RuntimeVersion, packageId + ".nuspec");
            var provenance = XDocument.Load(package).Descendants().Single(element => element.Name.LocalName == "repository");
            if ((string?)provenance.Attribute("url") != pin.PackageRepository ||
                (string?)provenance.Attribute("commit") != pin.PackageCommit)
                throw new InvalidDataException($"Published package {packageId} provenance differs from the source lock.");
        }

        return verifiedPackages;
    }

    public static async Task ProbeAsync(string root)
    {
        var pin = ReadLock(root);
        var output = Path.Combine(root, "artifacts", "runtime-probe");
        var publish = Path.Combine(output, "publish");
        var packageCache = Path.Combine(root, ".tools", "nuget");
        Directory.CreateDirectory(output);
        Console.WriteLine("Publishing a HOSTED Windows NativeAOT probe; this is not a WitOS guest build.");
        var build = await Processes.RunAsync("dotnet",
        [
            "publish", Path.Combine(root, ExperimentPath, "NativeAotProbe.csproj"),
            "--configuration", "Release", "--runtime", "win-x64", "--output", publish,
            "--packages", packageCache, "--configfile", Path.Combine(root, ExperimentPath, "NuGet.Config"),
            "-p:RestoreLockedMode=true"
        ], root, 600, Toolchain.NativeAotEnvironment());
        await File.WriteAllTextAsync(Path.Combine(output, "publish.log"), build.Output + build.Error);
        if (build.TimedOut || build.ExitCode != 0)
            throw new InvalidOperationException($"NativeAOT publish failed (exit={build.ExitCode}, timeout={build.TimedOut}).\n{build.Output}\n{build.Error}");

        var verifiedPackages = VerifyPublishedPackages(root, pin);

        var executable = Path.Combine(publish, "NativeAotProbe.exe");
        var pe = NativeImports.Inspect(executable);
        if (pe.HasClrHeader || pe.DirectImports.Length == 0 || pe.Subsystem != "WindowsCui")
            throw new InvalidDataException("The probe must be a native Windows console executable, with explicit OS imports.");

        var result = await Processes.RunAsync(executable, [], publish, 60);
        await File.WriteAllTextAsync(Path.Combine(output, "probe.stdout.log"), result.Output);
        await File.WriteAllTextAsync(Path.Combine(output, "probe.stderr.log"), result.Error);
        Console.Write(result.Output);
        if (result.TimedOut || result.ExitCode != 0 || !result.Output.Contains("[PROBE-SUCCESS]", StringComparison.Ordinal) ||
            result.Output.Contains("[PROBE-FAIL]", StringComparison.Ordinal) ||
            ProbeCases.Any(test => !result.Output.Contains($"[PROBE-PASS] {test}", StringComparison.Ordinal)))
            throw new InvalidOperationException($"NativeAOT probe failed (exit={result.ExitCode}, timeout={result.TimedOut}). {result.Error}");

        var bytes = await File.ReadAllBytesAsync(executable);
        var report = new
        {
            hostOnly = true,
            guestRuntimePorted = false,
            pin.RuntimeVersion, pin.RuntimeCommit, pin.PackageCommit,
            verifiedPackages,
            rid = "win-x64",
            profile = new { invariantGlobalization = true, serverGc = false, concurrentGc = false, windowsThreadPool = false },
            executableSize = bytes.Length,
            executableSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            passedCases = ProbeCases,
            directImportLibraryCount = pe.DirectImports.Length,
            directImportSymbolCount = pe.DirectImports.Sum(value => value.Symbols.Length),
            limitation = "PE IAT imports only. Dynamic GetProcAddress/PInvoke, forwarded exports, DLL dependencies and platform instructions are not a complete syscall list.",
            image = pe
        };
        await File.WriteAllTextAsync(Path.Combine(output, "probe-report.json"), JsonSerializer.Serialize(report, Json));

        var text = new StringBuilder();
        text.AppendLine("# Hosted NativeAOT probe");
        text.AppendLine();
        text.AppendLine("This Windows experiment does not run inside WitOS. It uses published, unmodified NativeAOT packages.");
        text.AppendLine();
        text.AppendLine($"Runtime: {pin.RuntimeVersion}; source: {pin.RuntimeCommit}");
        text.AppendLine($"Native image: {bytes.Length:N0} bytes; CLR header absent.");
        text.AppendLine($"Direct imports: {report.directImportSymbolCount} symbols from {report.directImportLibraryCount} libraries.");
        text.AppendLine();
        foreach (var library in pe.DirectImports)
            text.AppendLine($"- {library.Library}: {library.Symbols.Length} symbols");
        text.AppendLine();
        text.AppendLine(report.limitation);
        await File.WriteAllTextAsync(Path.Combine(output, "probe-report.md"), text.ToString());
        Console.WriteLine($"Native PE verified: {bytes.Length} bytes, {report.directImportSymbolCount} direct symbols in {report.directImportLibraryCount} libraries.");
        Console.WriteLine($"Reports: {output}");
    }
}
