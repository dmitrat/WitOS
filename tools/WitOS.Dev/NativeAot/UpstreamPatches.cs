using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WitOS.Dev.NativeAot;

/// <summary>
/// Reads and applies the WitOS changes to pinned upstream files kept in patches/&lt;repository&gt;/&lt;output&gt;.patch.
/// A patch applies only to the exact text it was made from and must produce the exact recorded text; there is no
/// fuzz, so an upstream update fails until the patch is remade.
/// </summary>
internal static class UpstreamPatches
{
    #region Fields

    private static readonly Regex HUNK = new(@"^@@ -(?<os>\d+)(,(?<oc>\d+))? \+(?<ns>\d+)(,(?<nc>\d+))? @@");

    private static readonly Regex HASH = new("^[0-9a-f]{64}$");

    #endregion

    #region Functions

    /// <summary>
    /// Directory of the patches.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The directory.</returns>
    public static string Directory(string root) => Path.Combine(root, "patches");

    /// <summary>
    /// Reads patches/&lt;repository&gt;/&lt;output&gt;.patch.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="repository">Upstream repository: runtime or openlibm.</param>
    /// <param name="output">Name of the patched output file.</param>
    /// <returns>The patch.</returns>
    public static UpstreamPatch Read(string root, string repository, string output)
    {
        var patch = Parse(File.ReadAllText(Path.Combine(Directory(root), repository, output + ".patch")));
        if (patch.Output != output)
            throw new InvalidDataException($"Patch {repository}/{output}.patch names output {patch.Output}.");
        return patch;
    }

    /// <summary>
    /// Applies patches/&lt;repository&gt;/&lt;output&gt;.patch to the LF text of an upstream file.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="repository">Upstream repository: runtime or openlibm.</param>
    /// <param name="source">Upstream path the text was read from.</param>
    /// <param name="output">Name of the patched output file.</param>
    /// <param name="text">Upstream text with LF line ends.</param>
    /// <returns>The patched text.</returns>
    public static string Apply(string root, string repository, string source, string output, string text)
    {
        var patch = Read(root, repository, output);
        if (patch.Source != source)
            throw new InvalidDataException($"Patch {repository}/{output}.patch applies to {patch.Source}, not {source}.");
        return Apply(patch, text);
    }

    /// <summary>
    /// Applies a patch to the exact text it was made from.
    /// </summary>
    /// <param name="patch">The patch.</param>
    /// <param name="text">Upstream text with LF line ends.</param>
    /// <returns>The patched text, whose hash is the recorded one.</returns>
    /// <exception cref="InvalidDataException">The text, a hunk or the result differs from the patch.</exception>
    public static string Apply(UpstreamPatch patch, string text)
    {
        // A file WitOS adds to an upstream tree is made from no text (plan step R1.1): its patch starts from the hash of
        // the empty string and holds one hunk of added lines. A text without a final line end keeps it missing, and no
        // hunk may reach its last line, whose end a unified diff would have to mark.
        var before = Hash(text);
        if (before != patch.Before)
            throw new InvalidDataException(
                $"{patch.Output}: {patch.Source} has sha256 {before}; the patch was made from {patch.Before}.");
        var finalLineEnd = text.Length == 0 || text.EndsWith('\n');
        string[] old = text.Length == 0 ? [] : finalLineEnd ? text[..^1].Split('\n') : text.Split('\n');
        if (!finalLineEnd && patch.Hunks.Any(hunk => hunk.OldStart - 1 + hunk.OldCount >= old.Length))
            throw new InvalidDataException($"{patch.Output}: a hunk reaches the last line of {patch.Source}, which has no line end.");
        var result = new List<string>(old.Length);
        var cursor = 0;
        foreach (var hunk in patch.Hunks)
        {
            var start = hunk.OldCount == 0 ? hunk.OldStart : hunk.OldStart - 1;
            if (start < cursor || start > old.Length)
                throw new InvalidDataException($"{patch.Output}: hunk at line {hunk.OldStart} is out of order.");
            result.AddRange(old[cursor..start]);
            cursor = start;
            if (result.Count != (hunk.NewCount == 0 ? hunk.NewStart : hunk.NewStart - 1))
                throw new InvalidDataException($"{patch.Output}: hunk at line {hunk.OldStart} has a wrong new start.");
            foreach (var line in hunk.Lines)
            {
                var kind = line.Length == 0 ? ' ' : line[0];
                var body = line.Length == 0 ? line : line[1..];
                if (kind != '+')
                {
                    if (cursor >= old.Length || old[cursor] != body)
                        throw new InvalidDataException($"{patch.Output}: line {cursor + 1} differs from the hunk.");
                    ++cursor;
                }
                if (kind != '-')
                    result.Add(body);
            }
        }
        result.AddRange(old[cursor..]);
        var patched = string.Join('\n', result) + (finalLineEnd ? "\n" : "");
        var after = Hash(patched);
        if (after != patch.After)
            throw new InvalidDataException($"{patch.Output}: the patched text has sha256 {after}; recorded {patch.After}.");
        return patched;
    }

    /// <summary>
    /// Parses a patch: the Source, Output, Before and After lines, the purpose up to the diff, then the unified diff
    /// of the source against itself with LF line ends.
    /// </summary>
    /// <param name="text">Patch text.</param>
    /// <returns>The patch.</returns>
    /// <exception cref="InvalidDataException">The text is not a well-formed patch.</exception>
    public static UpstreamPatch Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines[^1].Length == 0)
            lines = lines[..^1];
        var source = Field(lines, 0, "Source");
        var output = Field(lines, 1, "Output");
        var before = Field(lines, 2, "Before");
        var after = Field(lines, 3, "After");
        if (!HASH.IsMatch(before) || !HASH.IsMatch(after) || output.Contains('/') || output.Contains('\\'))
            throw new InvalidDataException($"Patch {output}: malformed header.");
        var index = Array.FindIndex(lines, line => line.StartsWith("--- ", StringComparison.Ordinal));
        if (index < 4 || index + 1 >= lines.Length || lines[index] != "--- a/" + source ||
            lines[index + 1] != "+++ b/" + source)
            throw new InvalidDataException($"Patch {output}: the diff must compare a/{source} with b/{source}.");
        var purpose = string.Join(' ', lines[4..index].Where(line => line.Length > 0));
        if (purpose.Length == 0)
            throw new InvalidDataException($"Patch {output}: missing purpose.");
        var hunks = new List<UpstreamPatchHunk>();
        for (index += 2; index < lines.Length;)
        {
            var header = HUNK.Match(lines[index++]);
            if (!header.Success)
                throw new InvalidDataException($"Patch {output}: expected a hunk header at line {index}.");
            var hunk = new UpstreamPatchHunk(Number(header, "os"), Number(header, "oc"), Number(header, "ns"),
                Number(header, "nc"), []);
            var body = new List<string>();
            int removed = 0, added = 0;
            while (removed < hunk.OldCount || added < hunk.NewCount)
            {
                var line = index < lines.Length ? lines[index++] :
                    throw new InvalidDataException($"Patch {output}: hunk at line {hunk.OldStart} is truncated.");
                var kind = line.Length == 0 ? ' ' : line[0];
                if (kind is not (' ' or '-' or '+'))
                    throw new InvalidDataException($"Patch {output}: unexpected line {index}.");
                removed += kind == '+' ? 0 : 1;
                added += kind == '-' ? 0 : 1;
                body.Add(line);
            }
            if (removed != hunk.OldCount || added != hunk.NewCount)
                throw new InvalidDataException($"Patch {output}: hunk at line {hunk.OldStart} has wrong counts.");
            hunks.Add(hunk with { Lines = body.ToArray() });
        }
        if (hunks.Count == 0)
            throw new InvalidDataException($"Patch {output}: no hunks.");
        return new(source, output, before, after, purpose, hunks.ToArray());
    }

    /// <summary>
    /// Provenance record of a patch: its path, file hash and the recorded text hashes.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="repository">Upstream repository: runtime or openlibm.</param>
    /// <param name="output">Name of the patched output file.</param>
    /// <returns>An object for a JSON report.</returns>
    public static object Describe(string root, string repository, string output)
    {
        var path = Path.Combine(Directory(root), repository, output + ".patch");
        var patch = Read(root, repository, output);
        return new
        {
            file = $"patches/{repository}/{output}.patch",
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
            source = patch.Source,
            before = patch.Before,
            after = patch.After
        };
    }

    /// <summary>
    /// SHA-256 of a text in UTF-8, as recorded in the patch header.
    /// </summary>
    /// <param name="text">Text.</param>
    /// <returns>Lowercase hexadecimal hash.</returns>
    public static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    #endregion

    #region Tools

    private static string Field(string[] lines, int index, string name)
        => index < lines.Length && lines[index].StartsWith(name + ": ", StringComparison.Ordinal)
            ? lines[index][(name.Length + 2)..]
            : throw new InvalidDataException($"Patch header line {index + 1} must be {name}.");

    private static int Number(Match header, string group)
        => header.Groups[group].Success ? int.Parse(header.Groups[group].Value) : 1;

    #endregion
}
