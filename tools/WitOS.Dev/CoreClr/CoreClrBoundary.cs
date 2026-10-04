using System.Text;
using System.Text.Json;

namespace WitOS.Dev.CoreClr;

/// <summary>
/// Complete native PE import inventory, not dynamic reachability or an OS implementation.
/// </summary>
internal static class CoreClrBoundary
{
    #region Functions

    /// <summary>
    /// Writes the platform-import boundary of the CoreCLR reference images, grouped by porting area.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="images">Import inventories of the reference images.</param>
    internal static async Task WriteAsync(string root, string output, JsonElement[] images)
    {
        var entries = images.SelectMany(image => new[] { "directImports", "delayImports" }.SelectMany(kind =>
            image.GetProperty(kind).EnumerateArray().SelectMany(import => import.GetProperty("symbols").EnumerateArray().Select(symbol =>
            {
                var library = import.GetProperty("library").GetString()!;
                var name = symbol.GetString()!;
                return new
                {
                    image = Path.GetFileName(image.GetProperty("file").GetString()),
                    library,
                    name,
                    kind,
                    group = Group(library, name),
                    guestVerified = false
                };
            })))).ToArray();
        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(Path.Combine(output, "platform-boundary.json"), JsonSerializer.Serialize(new
        {
            scope = "All direct/delay imports of the actual Windows reference images; ordinal identities retained. Name-based groups are navigation, not proof of semantics or guest support. Dynamic lookups, inline TEB/ISA and BCL native dependencies require additional source/runtime audits.",
            guestExecuted = false,
            entries
        }, options));
        var text = new StringBuilder("# CoreCLR/JIT native boundary\n\nWINDOWS REFERENCE ONLY. Every listed entry remains subject to actual WitOS implementation/validation. Corerun is a reference harness, not the future guest host.\n\n");
        foreach (var group in entries.GroupBy(e => e.group).OrderBy(g => g.Key))
        {
            text.AppendLine($"## {group.Key}\n\n| Image | Library | Import | Binding |\n| --- | --- | --- | --- |");
            foreach (var entry in group.OrderBy(e => e.image).ThenBy(e => e.library).ThenBy(e => e.name))
                text.AppendLine($"| {entry.image} | {entry.library} | `{entry.name}` | {entry.kind} |");
            text.AppendLine();
        }
        await File.WriteAllTextAsync(Path.Combine(output, "platform-boundary.md"), text.ToString());
    }

    #endregion

    #region Tools

    private static string Group(string library, string name)
    {
        if (library.Contains("-crt-", StringComparison.OrdinalIgnoreCase))
            return "crt";
        if (library.StartsWith("ole", StringComparison.OrdinalIgnoreCase) || library.Contains("winrt", StringComparison.OrdinalIgnoreCase))
            return "windows-interop";
        if (library.Equals("ADVAPI32.dll", StringComparison.OrdinalIgnoreCase))
            return "security-registry-diagnostics";
        if (library.Equals("VERSION.dll", StringComparison.OrdinalIgnoreCase) || library.Equals("USER32.dll", StringComparison.OrdinalIgnoreCase))
            return "windows-resource-metadata";
        if (name.Contains("FunctionTable", StringComparison.Ordinal) || name.StartsWith("Rtl", StringComparison.Ordinal) || name.Contains("Context", StringComparison.Ordinal) || name.Contains("Exception", StringComparison.Ordinal))
            return "code-context-unwind";
        if (name.Contains("Virtual", StringComparison.Ordinal) || name.Contains("Map", StringComparison.Ordinal) || name.Contains("Memory", StringComparison.Ordinal) || name.Contains("Heap", StringComparison.Ordinal) || name.Contains("Cache", StringComparison.Ordinal))
            return "memory-code-publication";
        if (name.Contains("File", StringComparison.Ordinal) || name.Contains("Pipe", StringComparison.Ordinal) || name.Contains("Library", StringComparison.Ordinal) || name.Contains("Module", StringComparison.Ordinal) || name == "GetProcAddress" || name.Contains("Path", StringComparison.Ordinal))
            return "files-modules-binding";
        if (name.Contains("Thread", StringComparison.Ordinal) || name.Contains("Wait", StringComparison.Ordinal) || name.Contains("Event", StringComparison.Ordinal) || name.Contains("Semaphore", StringComparison.Ordinal) || name.Contains("CriticalSection", StringComparison.Ordinal) || name.Contains("SRW", StringComparison.Ordinal) || name.Contains("ConditionVariable", StringComparison.Ordinal) || name.StartsWith("Tls", StringComparison.Ordinal) || name.StartsWith("Fls", StringComparison.Ordinal) || name.StartsWith("Sleep", StringComparison.Ordinal))
            return "threads-tls-synchronization";
        return "other-kernel-platform"; // Explicitly inventoried, not implicitly supported.
    }

    #endregion
}
