using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.Pe;
namespace WitOS.Dev.NativeAot;

/// <summary>
/// Audits that source-built GC objects exclude write-watch support.
/// </summary>
internal static class RuntimeGcPolicy
{
    #region Functions

    /// <summary>
    /// Audits the GC objects of the existing source build.
    /// </summary>
    /// <param name="root">Repository root.</param>
    public static Task ExistingAsync(string root)
    {
        var source = Path.Combine(root, ".tools/upstream", "runtime-" + RuntimeExperiment.ReadLock(root).RuntimeVersion);
        return RunAsync(root, source, Path.Combine(source, "artifacts/obj/coreclr/windows.x64.Release/witos"),
            Path.Combine(source, "artifacts/bin/coreclr/windows.x64.Release/witos/aotsdk/Runtime.WorkstationGC.lib"));
    }

    /// <summary>
    /// Audits that the GC objects were compiled without write-watch support.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="source">Pinned upstream source tree.</param>
    /// <param name="obj">Source-build object directory.</param>
    /// <param name="archive">Workstation GC archive.</param>
    public static async Task RunAsync(string root, string source, string obj, string archive)
    {
        var pin = RuntimeExperiment.ReadLock(root);
        var gitPrefix = new[] { "-c", "safe.directory=" + source.Replace('\\', '/') };
        var revision = await Processes.RunAsync("git", [.. gitPrefix, "rev-parse", "HEAD"], source);
        var clean = await Processes.RunAsync("git", [.. gitPrefix, "status", "--porcelain", "--untracked-files=normal"], source);
        if (revision.ExitCode != 0 || revision.TimedOut || revision.Output.Trim() != pin.RuntimeCommit || clean.ExitCode != 0 || clean.TimedOut || !string.IsNullOrWhiteSpace(clean.Output))
            throw new InvalidDataException("GC policy requires the clean pinned upstream tree.");
        var gcText = await File.ReadAllTextAsync(Path.Combine(root, "artifacts/runtime-config/source/gc.witos.cpp"));
        if (Regex.Matches(gcText, @"\bupdate_card_table_bundle\s*\(").Count != 2 ||
            !Regex.IsMatch(gcText, @"#ifndef FEATURE_MANUALLY_MANAGED_CARD_BUNDLES(?:(?!#endif).)*update_card_table_bundle\(\);(?:(?!#endif).)*#endif", RegexOptions.Singleline))
            throw new InvalidDataException("Pinned GC card-table caller no longer has the verified compile-time exclusion.");
        var all = JsonSerializer.Deserialize<JsonElement[]>(await File.ReadAllTextAsync(Path.Combine(obj, "compile_commands.json")))!;
        var commands = all.Select(c => new RuntimeGcPolicyCommand(c.GetProperty("directory").GetString()!, c.GetProperty("command").GetString()!, c.GetProperty("file").GetString()!, c.GetProperty("output").GetString()!))
            .Where(c => c.Output.Replace('\\', '/').Contains("/Runtime.WorkstationGC.dir/", StringComparison.Ordinal)).ToArray();
        var gc = commands.Single(c => c.File.Replace('\\', '/').EndsWith("/gcwks.witos.cpp", StringComparison.Ordinal));
        foreach (var flag in new[] { "FEATURE_USE_SOFTWARE_WRITE_WATCH_FOR_GC_HEAP", "FEATURE_MANUALLY_MANAGED_CARD_BUNDLES" })
            if (!Regex.IsMatch(gc.CommandLine, @"(?:^|\s)[/-]D" + flag + @"(?:\s|$)") || Regex.IsMatch(gc.CommandLine, @"[/-]U" + flag + @"\b"))
                throw new InvalidDataException("GC policy missing an unambiguous feature definition: " + flag);
        if (!gc.CommandLine.Contains("/Gy", StringComparison.Ordinal))
            throw new InvalidDataException("GC policy requires function COMDAT sections for relocation evidence.");
        var dependencies = new List<object>();
        var incoming = new List<object>();
        var inputs = new List<object>();
        foreach (var command in commands)
        {
            var path = Path.GetFullPath(command.Output, command.Directory);
            NativeObject.VerifyArchive(archive, path);
            var info = NativeObject.Inspect(path, collectReferences: true, includeDefinedReferences: true);
            inputs.Add(new { file = command.File, objectFile = path, sha256 = Hash(path) });
            foreach (var reference in info.ExternalReferences!)
            {
                // Exclude unwind/debug metadata, not data containing function pointers.
                if (reference.Section.StartsWith(".pdata", StringComparison.Ordinal) || reference.Section.StartsWith(".xdata", StringComparison.Ordinal) || reference.Section.StartsWith(".debug", StringComparison.Ordinal))
                    continue;
                if (reference.Target.Contains("update_card_table_bundle@gc_heap@WKS", StringComparison.Ordinal))
                    incoming.Add(new { file = command.File, reference });
                if (reference.Target.Contains("GetWriteWatch@GCToOSInterface", StringComparison.Ordinal) || reference.Target.Contains("ResetWriteWatch@GCToOSInterface", StringComparison.Ordinal))
                {
                    dependencies.Add(new { file = command.File, reference });
                    if (command != gc || reference.ContainingSymbol is null || !reference.ContainingSymbol.Contains("update_card_table_bundle@gc_heap@WKS", StringComparison.Ordinal))
                        throw new InvalidDataException("Unexpected OS write-watch caller: " + command.File + " / " + reference.ContainingSymbol);
                }
            }
        }
        var output = Path.Combine(root, "artifacts/runtime-gc-policy");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "policy.json"), JsonSerializer.Serialize(new
        {
            nativeSourceBuilt = true,
            guestCollectorExecuted = false,
            archiveSha256 = Hash(archive),
            gcCommand = gc.CommandLine,
            gcSourceSha256 = Hash(Path.Combine(root, "artifacts/runtime-config/source/gc.witos.cpp")),
            inputs,
            osWriteWatchReferences = dependencies,
            incomingCardTableUpdate = incoming,
            scope = "Both upstream feature definitions required. Entire actual runtime archive checked; named code/data incoming references exclude unwind/debug metadata only. OS references remain in the uncalled card-table method."
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        if (dependencies.Count != 2 || incoming.Count != 0)
            throw new InvalidDataException($"Unexpected GC write-watch boundary: {dependencies.Count} OS references, {incoming.Count} incoming card-table update references. See runtime-gc-policy/policy.json.");
        Console.WriteLine("[GC-POLICY-PASS] Software heap watch/manual card bundles: two OS references confined to an uncalled method; no incoming code/data references in the full runtime archive.");
    }

    #endregion

    #region Tools

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    #endregion
}
