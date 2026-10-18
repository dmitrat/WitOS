using System.Text;
using System.Text.Json;
using WitOS.Dev.Host;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The public compatibility matrix (Implementation Strategy §145, plan step N4.5): rendered from the suite's own list
/// of checks, the differences expectations.json records for each platform, and build/compatibility-matrix.json for
/// what lies beyond the suite, into @Docs/Implementation/Compatibility-Matrix.md; a host test requires the document to
/// be current, so the matrix never says more than the gates prove.
/// </summary>
internal static class CompatibilityMatrix
{
    #region Constants

    /// <summary>
    /// The document the command writes.
    /// </summary>
    public const string DOCUMENT = "@Docs/Implementation/Compatibility-Matrix.md";

    /// <summary>
    /// What the matrix says beyond the suite's areas.
    /// </summary>
    public const string BEYOND = "build/compatibility-matrix.json";

    /// <summary>
    /// The platforms expectations.json records, in the matrix's order, with their column headings.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, string Heading)> PLATFORMS =
        [("windows", "Windows"), ("linux", "Linux"), ("witos", "WitOS (x64, ARM64, riscv64)")];

    private static readonly JsonSerializerOptions JSON = new() { PropertyNameCaseInsensitive = true };

    #endregion

    #region Functions

    /// <summary>
    /// Renders the matrix and writes the document (the compatibility-matrix command).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>When the document is written.</returns>
    public static async Task WriteAsync(string root)
    {
        var text = await GenerateAsync(root);
        var path = Path.Combine(root, DOCUMENT);
        await File.WriteAllTextAsync(path, text);
        Console.WriteLine($"Wrote {DOCUMENT}: {text.Count(c => c == '\n')} lines.");
    }

    /// <summary>
    /// Renders the matrix from the built suite, expectations.json and the list beyond the suite.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The document's text.</returns>
    public static async Task<string> GenerateAsync(string root)
    {
        var checks = await ListChecksAsync(root);
        var expectations = await ReadExpectationsAsync(root);
        var beyond = await ReadBeyondAsync(root);
        return Render(checks, expectations, beyond);
    }

    /// <summary>
    /// The suite's own list of its checks, from the suite the solution built (dotnet DotNetCompatibility.dll --list).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Each check's id, Area.Name, in the suite's order.</returns>
    /// <exception cref="FileNotFoundException">The suite is not built for this configuration.</exception>
    public static async Task<IReadOnlyList<string>> ListChecksAsync(string root)
    {
        var suite = SuitePath(root);
        if (!File.Exists(suite))
            throw new FileNotFoundException($"The suite is not built: build WitOS.slnx with the tool's configuration ({suite}).", suite);
        var result = await Processes.RunAsync("dotnet", [suite, "--list"], Path.GetDirectoryName(suite)!);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{suite} --list exited with {result.ExitCode}:\n{result.Output}\n{result.Error}");
        return [.. result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(line => line.Contains('.'))];
    }

    /// <summary>
    /// The built suite's path for the tool's own configuration.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The path of DotNetCompatibility.dll.</returns>
    public static string SuitePath(string root)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        return Path.Combine(root, RuntimeCompatibility.SUITE, "bin", configuration, "net10.0", "DotNetCompatibility.dll");
    }

    /// <summary>
    /// The differences expectations.json records: by platform, each check's id and the reason.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The expectations.</returns>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> ReadExpectationsAsync(string root)
    {
        var path = Path.Combine(root, RuntimeCompatibility.SUITE, "expectations.json");
        var read = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(await File.ReadAllTextAsync(path)) ??
                   throw new InvalidDataException($"{path} is empty.");
        return read.ToDictionary(platform => platform.Key, platform => (IReadOnlyDictionary<string, string>)platform.Value);
    }

    /// <summary>
    /// Reads what the matrix says beyond the suite.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The capabilities and the hardware.</returns>
    /// <exception cref="InvalidDataException">A status is not one the matrix knows, or an entry lacks its note.</exception>
    public static async Task<Beyond> ReadBeyondAsync(string root)
    {
        var path = Path.Combine(root, BEYOND);
        var beyond = JsonSerializer.Deserialize<Beyond>(await File.ReadAllTextAsync(path), JSON) ?? throw new InvalidDataException($"{path} is empty.");
        foreach (var capability in beyond.Capabilities)
        {
            if (capability.Status is not ("Supported" or "Partial" or "Absent" or "Planned"))
                throw new InvalidDataException($"{capability.Name}: the status {capability.Status} is none of Supported, Partial, Absent, Planned.");
            if (string.IsNullOrWhiteSpace(capability.Note) || string.IsNullOrWhiteSpace(capability.Step))
                throw new InvalidDataException($"{capability.Name} lacks its step or its note.");
        }
        foreach (var hardware in beyond.Hardware)
            if (hardware.Status is not ("Primary" or "Early" or "Future") || string.IsNullOrWhiteSpace(hardware.Note))
                throw new InvalidDataException($"{hardware.Platform}: the status {hardware.Status} is none of Primary, Early, Future, or the note is missing.");
        return beyond;
    }

    /// <summary>
    /// Renders the document.
    /// </summary>
    /// <param name="checks">The suite's checks, Area.Name each.</param>
    /// <param name="expectations">The recorded differences by platform.</param>
    /// <param name="beyond">What lies beyond the suite.</param>
    /// <returns>The document's text, with '\n' line ends.</returns>
    public static string Render(IReadOnlyList<string> checks, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> expectations,
        Beyond beyond)
    {
        var text = new StringBuilder();
        text.Append("# Compatibility matrix\n\n");
        text.Append("The public matrix of Implementation Strategy §145 (plan step N4.5). This file is written by `compatibility-matrix` from\n");
        text.Append("the compatibility suite's own list of checks (`dotnet DotNetCompatibility.dll --list`), the differences\n");
        text.Append("`tests/DotNetCompatibility/expectations.json` records for each platform and `build/compatibility-matrix.json`; a host\n");
        text.Append("test requires it to be current, so it says no more than the gates prove: the suite must end with no unexpected result on\n");
        text.Append("Windows, Linux and WitOS on its three ISAs (N1, N4.4, V3), and every recorded difference has its reason in\n");
        text.Append("[Compatibility-Differences.md](Compatibility-Differences.md).\n\n");

        text.Append("## The areas of the compatibility suite\n\n");
        text.Append("*Supported*: every check of the area passes on the platform. *Partial*: the checks named are recorded differences, each\n");
        text.Append("failing explicitly with the reason recorded. The Terminal area runs where the suite's input is a terminal (the runner\n");
        text.Append("types in the guest) and reports its checks not applicable elsewhere.\n\n");
        text.Append("| Area | Checks |");
        foreach (var (_, heading) in PLATFORMS)
            text.Append(' ').Append(heading).Append(" |");
        text.Append("\n| --- | --- |");
        foreach (var _ in PLATFORMS)
            text.Append(" --- |");
        text.Append('\n');
        var areas = checks.Select(check => check[..check.IndexOf('.')]).Distinct().ToList();
        foreach (var area in areas)
        {
            var own = checks.Where(check => check.StartsWith(area + ".", StringComparison.Ordinal)).ToList();
            text.Append("| ").Append(area).Append(" | ").Append(own.Count).Append(" |");
            foreach (var (key, _) in PLATFORMS)
            {
                var recorded = expectations.TryGetValue(key, out var platform)
                    ? own.Where(platform.ContainsKey).Select(check => check[(area.Length + 1)..]).ToList()
                    : [];
                text.Append(' ').Append(recorded.Count == 0 ? "Supported" : $"Partial: {string.Join(", ", recorded)}").Append(" |");
            }
            text.Append('\n');
        }
        text.Append($"\n{checks.Count} checks in {areas.Count} areas.\n\n");

        text.Append("## Recorded differences\n\n");
        var differences = PLATFORMS.SelectMany(platform => expectations.TryGetValue(platform.Key, out var recorded)
            ? recorded.OrderBy(check => check.Key, StringComparer.Ordinal).Select(check => (platform.Heading, check.Key, check.Value))
            : []).ToList();
        if (differences.Count == 0)
            text.Append("None.\n\n");
        else
        {
            text.Append("| Check | Platform | Reason |\n| --- | --- | --- |\n");
            foreach (var (heading, check, reason) in differences)
                text.Append("| `").Append(check).Append("` | ").Append(heading).Append(" | ").Append(reason).Append(" |\n");
            text.Append('\n');
        }

        text.Append("## Beyond the suite\n\n");
        text.Append("*Supported*, *Partial* and *Absent* are facts a plan step verified in the guest on the three ISAs unless the fact names\n");
        text.Append("fewer; *Planned* names the milestone or phase that brings the capability. An absent capability fails explicitly, never\n");
        text.Append("silently.\n\n");
        text.Append("| Capability | Status | Step | Fact |\n| --- | --- | --- | --- |\n");
        foreach (var capability in beyond.Capabilities)
            text.Append("| ").Append(capability.Name).Append(" | ").Append(capability.Status).Append(" | ").Append(capability.Step)
                .Append(" | ").Append(capability.Note).Append(" |\n");

        text.Append("\n## Hardware\n\n");
        text.Append("The hardware matrix of Strategy §146.\n\n");
        text.Append("| Platform | Status | Note |\n| --- | --- | --- |\n");
        foreach (var hardware in beyond.Hardware)
            text.Append("| ").Append(hardware.Platform).Append(" | ").Append(hardware.Status).Append(" | ").Append(hardware.Note).Append(" |\n");
        return text.ToString();
    }

    #endregion

    #region Nested Types

    /// <summary>
    /// A capability beyond the suite, by name: its status on WitOS, the step that gives it and the fact behind the status.
    /// </summary>
    public sealed record Capability(string Name, string Status, string Step, string Note);

    /// <summary>
    /// A hardware platform and its status.
    /// </summary>
    public sealed record Hardware(string Platform, string Status, string Note);

    /// <summary>
    /// What build/compatibility-matrix.json holds.
    /// </summary>
    public sealed record Beyond(IReadOnlyList<Capability> Capabilities, IReadOnlyList<Hardware> Hardware);

    #endregion
}
