using System.Text.RegularExpressions;
using WitOS.Dev;

// Repository consistency checks: documentation, manifests and headers must agree.
internal static class ConsistencyTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases(string root)
    {
        yield return ("FormatManifestDescribesTree", () => FormatManifestAsync(root));
        yield return ("VersionsHaveOneSource", () => VersionsAsync(root));
        yield return ("AbiReferenceCoversEveryCall", () => AbiReferenceAsync(root));
    }

    private static Task AbiReferenceAsync(string root)
    {
        var reference = File.ReadAllText(Path.Combine(root, "@Docs/Implementation/ABI-Reference.md"));
        var calls = KernelAbi.Calls(root);
        Check(calls.Count > 0, "No WIT_CALL_* definitions were found");
        foreach (var (name, number) in calls)
        {
            Check(Regex.IsMatch(reference, $@"^\| {number} \| `{name}` \|", RegexOptions.Multiline),
                $"ABI reference lacks a row for {name} = {number}");
        }
        var rows = Regex.Matches(reference, @"^\| \d+ \| `(WIT_CALL_[A-Z0-9_]+)` \|", RegexOptions.Multiline);
        Check(rows.Count == calls.Count, $"ABI reference has {rows.Count} call rows for {calls.Count} calls");
        var next = calls.Values.Max() + 1;
        Check(reference.Contains($"Следующий свободный номер: **{next}**", StringComparison.Ordinal),
            "ABI reference names a stale next free call number");
        Check(reference.Contains($"**user ABI v{KernelAbi.UserVersion(root)}**", StringComparison.Ordinal) &&
            reference.Contains($"**boot ABI v{KernelAbi.BootVersion(root)}**", StringComparison.Ordinal),
            "ABI reference names stale versions");
        return Task.CompletedTask;
    }

    private static Task VersionsAsync(string root)
    {
        var user = KernelAbi.UserVersion(root);
        var boot = KernelAbi.BootVersion(root);
        Check(KernelAbi.Banner(root) == $"WitOS user ABI v{user}, boot ABI v{boot}", "Unexpected banner format");
        var kernel = File.ReadAllText(Path.Combine(root, "src/Kernel/kernel.c"));
        Check(!Regex.IsMatch(kernel, @"WitOS \d+\.\d+\.\d+"), "kernel.c prints a hardcoded product version");
        Check(kernel.Contains("wit_console_write_u64(WIT_ABI_VERSION);", StringComparison.Ordinal) &&
            kernel.Contains("wit_console_write_u64(WIT_BOOT_VERSION);", StringComparison.Ordinal),
            "kernel.c banner does not print the header versions");

        // README describes only the current state: every ABI version it names is current.
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        foreach (Match match in Regex.Matches(readme, @"\b(user|boot)?\s*ABI v(\d+)", RegexOptions.IgnoreCase))
        {
            var expected = match.Groups[1].Value.Equals("boot", StringComparison.OrdinalIgnoreCase) ? boot : user;
            Check(int.Parse(match.Groups[2].Value) == expected, $"README names a stale ABI version: {match.Value}");
        }
        Check(readme.Contains(KernelAbi.Banner(root), StringComparison.Ordinal), "README shows a stale kernel banner");

        // PLAN keeps history; its header must name the current versions.
        var header = string.Join("\n", File.ReadLines(Path.Combine(root, "PLAN.md")).Take(5));
        Check(header.Contains($"user ABI v{user}", StringComparison.Ordinal), "PLAN header lacks the current user ABI");
        Check(header.Contains($"boot ABI v{boot}", StringComparison.Ordinal), "PLAN header lacks the current boot ABI");
        return Task.CompletedTask;
    }

    private static async Task FormatManifestAsync(string root)
    {
        var manifest = await SourceFormat.ReadManifestAsync(root);
        var native = SourceFormat.Expand(root, manifest.Native, [".c", ".h", ".cpp"], manifest.Exclude);
        var managed = SourceFormat.Expand(root, manifest.Managed, [".cs"], manifest.Exclude);
        Check(native.All(file => !file.EndsWith(".asm", StringComparison.Ordinal)), "Assembly entered the C formatter");
        Check(managed.All(file => file.EndsWith(".cs", StringComparison.Ordinal)), "Non-C# file entered the C# formatter");
        Check(native.Concat(managed).All(file => !file.Contains("/obj/") && !file.Contains("/bin/")), "Generated output");

        var missing = false;
        try
        {
            SourceFormat.Expand(root, ["src/DoesNotExist"], [".c"], []);
        }
        catch (InvalidDataException)
        {
            missing = true;
        }
        Check(missing, "A missing manifest entry was accepted");
    }

    private static void Check(bool value, string why)
    {
        if (!value)
        {
            throw new InvalidOperationException(why);
        }
    }
}
