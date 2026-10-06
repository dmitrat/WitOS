using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.Images;
using WitOS.Dev.NativeAot;
using WitOS.Dev.NativeAot.Acceptance;

namespace WitOS.Dev.CoreClr;

/// <summary>
/// The guest's coreclr.dll (P6.4.k2): upstream CoreCLR's objects, as the pinned reference build compiled them, linked
/// as a WitOS C++ library links, without default libraries, over the WitOS C++ runtime, UCRT subset, STL sources and
/// the guest's native support, with upstream's own static libraries. The Windows libraries upstream links are left
/// out; what they would supply is the image's unresolved externals, which must equal the recorded expectation in both
/// directions. The inventory includes the references only code generation makes (P6.4.k3a3a). A link with none is not
/// yet a runtime that runs in the guest.
/// </summary>
internal static class CoreClrGuest
{
    #region Constants

    /// <summary>
    /// The expected unresolved externals of coreclr.dll.
    /// </summary>
    public const string EXPECTED = "experiments/CoreClr/guest-link.json";

    private const string PROFILE = "coreclr-reference";

    private const string TARGET = @"dlls\mscoree\coreclr\coreclr.dll";

    #endregion

    #region Functions

    /// <summary>
    /// Links the guest's coreclr.dll as one recorded attempt.
    /// </summary>
    /// <param name="root">Repository root.</param>
    internal static Task RunAsync(string root) =>
        RuntimeBootAttempt.RunInDirectoryAsync(Path.Combine(root, "artifacts/coreclr-guest"), "coreclr-guest",
            attempt => RunAsync(root, attempt));

    #endregion

    #region Tools

    private static async Task RunAsync(string root, RuntimeBootAttempt attempt)
    {
        var pin = RuntimeExperiment.ReadLock(root);
        var tree = Path.Combine(root, ".tools/upstream", "runtime-" + pin.RuntimeVersion,
            "artifacts/obj/coreclr/windows.x64.Release", PROFILE);
        var ninja = Path.Combine(tree, "build.ninja");
        var cache = Path.Combine(tree, "CMakeCache.txt");
        if (!File.Exists(ninja) || !File.Exists(cache))
            throw new FileNotFoundException("The CoreCLR reference build is missing; run coreclr-source first.", ninja);
        // The objects carry link-time code generation from the reference build's compiler, so its own linker links them.
        var linker = File.ReadLines(cache).Select(line => line.Trim())
            .Where(line => line.StartsWith("CMAKE_LINKER:FILEPATH=", StringComparison.Ordinal))
            .Select(line => line["CMAKE_LINKER:FILEPATH=".Length..].Replace('/', '\\')).SingleOrDefault()
            ?? throw new InvalidDataException("The CoreCLR reference build names no linker.");
        var edge = LinkEdge.Read(ninja, TARGET);

        var output = Path.Combine(attempt.RunDirectory, "build");
        Directory.CreateDirectory(output);
        var msvc = await Toolchain.FindMsvcAsync(root);
        await UserImage.PrepareAbiAsync(root, output, msvc);
        var support = await CoreClrMemoryImage.BuildSupportAsync(root, output, msvc);
        var runtimes = await HostRuntimeImage.CompileRuntimesAsync(root, output, msvc);
        var library = await CoreClrMemoryImage.BuildLibrarySupportAsync(root, output, msvc, support);

        // Upstream's own static libraries stay; the Windows and CRT libraries are what WitOS replaces.
        var upstream = edge.Libraries.Where(name => name.Contains('\\')).ToArray();
        var windows = edge.Libraries.Where(name => !name.Contains('\\')).Distinct(StringComparer.OrdinalIgnoreCase).Append("ucrt.lib").ToArray();
        var image = Path.Combine(output, "coreclr.dll");
        // Link-time code generation makes the calls to the compiler's intrinsic functions (the mathematics, strncmp,
        // strncpy) only after its first pass, which stops at that pass's unresolved externals. The link forces past
        // them to reach code generation and its references; the forced image is deleted, never published or loaded.
        // Like the host, the image carries no CFG or EH continuation metadata, which WitOS does not enforce.
        string[] arguments = ["/nologo", "/dll", "/entry:wit_library_cxx_entry", "/include:_tls_used", "/nodefaultlib",
            "/machine:x64", "/LTCG", "/OPT:REF", "/OPT:ICF", "/FORCE:UNRESOLVED", "/DEF:" + edge.Definition,
            "/out:" + image, .. edge.Objects, .. upstream, .. runtimes, .. library];
        var response = Path.Combine(output, "coreclr.rsp");
        await File.WriteAllLinesAsync(response, arguments.Select(argument => argument.Contains(' ') ? '"' + argument + '"' : argument));
        Console.WriteLine("Linking upstream CoreCLR for the guest; this takes a while.");
        var result = await Processes.RunAsync(linker, ["@" + response], tree, 1800);
        var log = result.Output + result.Error;
        await File.WriteAllTextAsync(Path.Combine(output, "coreclr.link.log"), log);
        File.Delete(image);
        if (result.TimedOut)
            throw new TimeoutException("Linking coreclr.dll timed out.");
        var other = log.Split('\n').Where(line => line.Contains(" error LNK", StringComparison.Ordinal) &&
            !line.Contains("LNK2001", StringComparison.Ordinal) && !line.Contains("LNK2019", StringComparison.Ordinal)).ToArray();
        var generating = log.IndexOf("Generating code", StringComparison.Ordinal);
        var generated = log.IndexOf("Finished generating code", StringComparison.Ordinal);
        if (other.Length > 0 || result.ExitCode != 0 || generating < 0 || generated < generating)
            throw new InvalidDataException($"Linking coreclr.dll failed beyond unresolved externals:\n{string.Join('\n', other)}\n{log}");
        // Code generation reports the first pass's externals again, through their imports.
        var first = Unresolved(log[..generating]).ToHashSet(StringComparer.Ordinal);
        var unresolved = new SortedSet<string>(first, StringComparer.Ordinal);
        var known = first.Select(Bare).ToHashSet(StringComparer.Ordinal);
        foreach (var symbol in Unresolved(log[generated..]))
            if (known.Add(Bare(symbol)))
                unresolved.Add(symbol);

        // Each external by the Windows or CRT library upstream takes it from, for the report.
        var owners = await OwnersAsync(Path.GetDirectoryName(linker)!, msvc, windows);
        var groups = unresolved.GroupBy(symbol => owners.GetValueOrDefault(symbol, "(none of upstream's libraries)"))
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count());

        var actual = new SortedDictionary<string, string[]>(StringComparer.Ordinal) { ["coreclr"] = [.. unresolved] };
        var options = new JsonSerializerOptions { WriteIndented = true };
        var report = Path.Combine(attempt.RunDirectory, "guest-link.json");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(actual, options));
        var expected = JsonSerializer.Deserialize<SortedDictionary<string, string[]>>(await File.ReadAllTextAsync(Path.Combine(root, EXPECTED)))!;
        var differences = new StringBuilder();
        foreach (var name in actual.Keys.Union(expected.Keys))
        {
            var now = actual.GetValueOrDefault(name, []);
            var before = expected.GetValueOrDefault(name, []);
            foreach (var symbol in now.Except(before))
                differences.AppendLine($"{name}: new unresolved {symbol}");
            foreach (var symbol in before.Except(now))
                differences.AppendLine($"{name}: no longer unresolved {symbol}");
        }
        if (differences.Length > 0)
            throw new InvalidDataException($"coreclr.dll's unresolved externals differ from {EXPECTED} (actual: {report}):\n{differences}");
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        attempt.Publish(new
        {
            guestRuntimeExecuted = false,
            runtimeCommit = pin.RuntimeCommit,
            linker,
            objects = edge.Objects.Length,
            upstreamLibraries = upstream,
            replacedLibraries = windows,
            unresolved = unresolved.Count,
            revealedByCodeGeneration = unresolved.Count - first.Count,
            byLibrary = groups,
            expectedSha256 = Hash(Path.Combine(root, EXPECTED)),
            scope = "Upstream CoreCLR objects of the pinned reference build linked for the guest over WitOS's runtimes; " +
                "unresolved externals inventory, not a runtime that runs in the guest"
        });
        Console.WriteLine($"[CORECLR-GUEST] coreclr: {unresolved.Count} unresolved externals, as expected " +
            $"({unresolved.Count - first.Count} revealed by code generation).");
        foreach (var (owner, count) in groups)
            Console.WriteLine($"[CORECLR-GUEST]   {count,4} {owner}");
    }

    // The public symbols of each library, mapped to the first library in link order that defines them.
    private static async Task<Dictionary<string, string>> OwnersAsync(string tools, string msvc, IEnumerable<string> libraries)
    {
        var sdk = Path.GetDirectoryName(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!;
        string[] directories = [sdk, Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(sdk)!)!, "ucrt", "x64"),
            Path.GetFullPath(Path.Combine(msvc, "../../../lib/x64"))];
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in libraries)
        {
            var file = directories.Select(directory => Path.Combine(directory, name)).FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("A library upstream links is missing.", name);
            var listing = await Processes.RunAsync(Path.Combine(tools, "dumpbin.exe"), ["/nologo", "/linkermember:1", file], tools, 600);
            if (listing.TimedOut || listing.ExitCode != 0)
                throw new InvalidDataException("Listing a library's symbols failed: " + name);
            foreach (Match match in MEMBER.Matches(listing.Output))
                owners.TryAdd(match.Groups["name"].Value, name.ToLowerInvariant());
        }
        return owners;
    }

    // The unresolved externals a part of a link log reports, by their decorated names.
    private static IEnumerable<string> Unresolved(string log) => UNRESOLVED.Matches(log)
        .Select(match => match.Groups["decorated"].Success ? match.Groups["decorated"].Value : match.Groups["name"].Value);

    // A symbol without its import prefix.
    private static string Bare(string symbol) =>
        symbol.StartsWith("__imp_", StringComparison.Ordinal) ? symbol["__imp_".Length..] : symbol;

    // "unresolved external symbol name" or "unresolved external symbol "undecorated" (decorated)".
    private static readonly Regex UNRESOLVED = new(
        @"error LNK20(?:01|19): unresolved external symbol (?:""[^""]*"" \((?<decorated>[^)\s]+)\)|(?<name>\S+))",
        RegexOptions.Compiled);

    // A public symbol line of dumpbin /linkermember:1: its offset and its name.
    private static readonly Regex MEMBER = new(@"^\s+[0-9A-F]+\s+(?<name>\S+)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    #endregion
}

/// <summary>
/// One link step of a ninja build: its object inputs, the libraries it links and its module definition file.
/// </summary>
internal sealed record LinkEdge(string[] Objects, string[] Libraries, string Definition)
{
    #region Functions

    /// <summary>
    /// Reads the link step that produces a target from build.ninja.
    /// </summary>
    /// <param name="ninja">The build.ninja path.</param>
    /// <param name="target">The target, as ninja names it.</param>
    /// <returns>The step.</returns>
    internal static LinkEdge Read(string ninja, string target)
    {
        var lines = File.ReadAllText(ninja).Replace("$\r\n", "").Replace("$\n", "").Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, line => line.StartsWith("build " + target.Replace(":", "$:") + " ", StringComparison.Ordinal));
        if (start < 0)
            throw new InvalidDataException("No link step produces " + target + ".");
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = start + 1; i < lines.Length && lines[i].StartsWith("  ", StringComparison.Ordinal); ++i)
        {
            var separator = lines[i].IndexOf(" = ", StringComparison.Ordinal);
            if (separator > 0)
                variables[lines[i][..separator].Trim()] = lines[i][(separator + 3)..];
        }
        var inputs = lines[start][(lines[start].IndexOf(": ", StringComparison.Ordinal) + 2)..];
        inputs = inputs[(inputs.IndexOf(' ') + 1)..]; // the rule
        var explicitInputs = inputs.Split(" || ")[0].Split(" | ")[0];
        string[] Tokens(string text)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            for (var i = 0; i < text.Length; ++i)
            {
                if (text[i] == '$' && i + 1 < text.Length)
                    current.Append(text[++i]); // ninja escapes "$ ", "$:" and "$$"
                else if (text[i] == ' ')
                {
                    if (current.Length > 0)
                        tokens.Add(current.ToString());
                    current.Clear();
                }
                else
                    current.Append(text[i]);
            }
            if (current.Length > 0)
                tokens.Add(current.ToString());
            return [.. tokens];
        }
        var flags = Tokens(variables.GetValueOrDefault("LINK_FLAGS", ""));
        var definition = flags.Select(flag => flag.StartsWith("/DEF:", StringComparison.OrdinalIgnoreCase) ? flag[5..] : null)
            .SingleOrDefault(flag => flag is not null) ?? throw new InvalidDataException(target + " has no module definition file.");
        return new LinkEdge(Tokens(explicitInputs), Tokens(variables.GetValueOrDefault("LINK_LIBRARIES", "")), definition);
    }

    #endregion
}
