using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using WitOS.Dev.Host;
using WitOS.Dev.Upstream;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The rehearsal of an upgrade (plan step R8, RFC 0015 section 4): the witos patch set carried from the pinned
/// dotnet/runtime commit to another tag. Each patched file is read at the pin and at the tag: a file upstream did not
/// change keeps its patch, a changed one is merged three ways (the patched text, the pinned text and the tag's text,
/// with git merge-file), and a merge that conflicts is left for a person with its markers. The carried patches go to
/// artifacts/runtime-rebase/&lt;tag&gt; with a report; with --apply, a carry without conflicts replaces patches/runtime and
/// moves the pin.
/// </summary>
internal static class RuntimeRebase
{
    #region Functions

    /// <summary>
    /// Carries the patch set to a tag and reports what it took.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="tag">A dotnet/runtime tag, such as v10.0.12.</param>
    /// <param name="apply">Whether a carry without conflicts replaces patches/runtime and moves the pin to the tag.</param>
    public static async Task RunAsync(string root, string tag, bool apply)
    {
        var pin = RuntimeWitos.ReadLock(root);
        var repository = Path.Combine(root, ".tools", "upstream", "runtime-rebase");
        if (!Directory.Exists(Path.Combine(repository, ".git")))
        {
            Directory.CreateDirectory(repository);
            await GitAsync(repository, ["init", "-q"]);
            await GitAsync(repository, ["remote", "add", "origin", pin.Repository + ".git"]);
        }
        await GitAsync(repository, ["fetch", "-q", "--depth=1", "origin", pin.Commit], 1800);
        await GitAsync(repository, ["fetch", "-q", "--depth=1", "origin", $"refs/tags/{tag}:refs/tags/{tag}"], 1800);
        var commit = (await GitAsync(repository, ["rev-list", "-n", "1", tag])).Trim();

        var output = Path.Combine(root, "artifacts", "runtime-rebase", tag);
        if (Directory.Exists(output))
            Directory.Delete(output, recursive: true);
        Directory.CreateDirectory(output);
        var scratch = Path.Combine(output, ".merge");
        Directory.CreateDirectory(scratch);
        var kinds = new SortedDictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["unchanged"] = [],
            ["merged"] = [],
            ["conflicts"] = [],
            ["added"] = [],
            ["gone"] = []
        };
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var (pinnedAdded, pinnedRemoved, carriedAdded, carriedRemoved) = (0, 0, 0, 0);
        foreach (var (path, name) in RuntimeWitos.PATCHES.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var patch = UpstreamPatches.Read(root, "runtime", name);
            var original = await File.ReadAllTextAsync(Path.Combine(root, "patches", "runtime", name + ".patch"));
            var originalLines = Lines(original);
            pinnedAdded += originalLines.Added;
            pinnedRemoved += originalLines.Removed;
            var pinned = Text(await ShowAsync(repository, pin.Commit, path) ?? []);
            var patched = UpstreamPatches.Apply(patch, pinned);
            var nextBytes = await ShowAsync(repository, commit, path);
            if (nextBytes is null && pinned.Length > 0)
            {
                kinds["gone"].Add(path);
                continue;
            }
            nextBytes ??= [];
            // The pin holds the bytes the checkout has; a patch applies to their text.
            hashes[path] = Convert.ToHexString(SHA256.HashData(nextBytes)).ToLowerInvariant();
            var next = Text(nextBytes);
            string kind, result;
            if (pinned.Length == 0 && next.Length > 0)
            {
                // A file WitOS adds that upstream now has too is a person's decision.
                kinds["conflicts"].Add(path);
                await File.WriteAllTextAsync(Path.Combine(output, name + ".patch.conflict"), next);
                continue;
            }
            if (pinned.Length == 0)
                (kind, result) = ("added", patched);
            else if (next == pinned)
                (kind, result) = ("unchanged", patched);
            else
            {
                var ours = Path.Combine(scratch, "ours");
                var common = Path.Combine(scratch, "base");
                var theirs = Path.Combine(scratch, "theirs");
                await File.WriteAllTextAsync(ours, patched);
                await File.WriteAllTextAsync(common, pinned);
                await File.WriteAllTextAsync(theirs, next);
                // git merge-file leaves the merge in the first file and exits with the number of conflicts, at most 127.
                var merge = await Processes.RunAsync("git", ["merge-file", ours, common, theirs], scratch, 120);
                if (merge.TimedOut || merge.ExitCode is < 0 or > 127)
                    throw new InvalidOperationException($"git merge-file failed for {path}. {merge.Error}");
                var merged = await File.ReadAllTextAsync(ours);
                if (merge.ExitCode > 0)
                {
                    kinds["conflicts"].Add(path);
                    await File.WriteAllTextAsync(Path.Combine(output, name + ".patch.conflict"), merged);
                    continue;
                }
                (kind, result) = ("merged", merged);
            }
            kinds[kind].Add(path);
            var target = Path.Combine(output, name + ".patch");
            if (kind != "merged")
            {
                // The tag's text is the pinned one: the patch carries over as it is.
                await File.WriteAllTextAsync(target, original);
                carriedAdded += originalLines.Added;
                carriedRemoved += originalLines.Removed;
                continue;
            }
            // The purpose as written, the lines between the header and the diff.
            var start = original.IndexOf("\n\n", StringComparison.Ordinal) + 2;
            var purpose = original[start..original.IndexOf("\n--- a/", start, StringComparison.Ordinal)].TrimEnd('\n');
            var carried = $"Source: {path}\nOutput: {name}\nBefore: {UpstreamPatches.Hash(next)}\nAfter: {UpstreamPatches.Hash(result)}\n\n" +
                $"{purpose}\n\n{await DiffAsync(scratch, path, next, result)}";
            // The carried patch applies as UpstreamPatches will apply it, to the tag's exact text.
            if (UpstreamPatches.Apply(UpstreamPatches.Parse(carried), next) != result)
                throw new InvalidDataException($"The carried patch of {path} does not reproduce the merged text.");
            await File.WriteAllTextAsync(target, carried);
            var carriedLines = Lines(carried);
            carriedAdded += carriedLines.Added;
            carriedRemoved += carriedLines.Removed;
        }
        Directory.Delete(scratch, recursive: true);
        var report = Path.Combine(output, "report.json");
        // The size of the set in lines it adds and removes: at the pin, and carried, without the files that conflict.
        var size = new
        {
            pinned = new { added = pinnedAdded, removed = pinnedRemoved },
            carried = new { added = carriedAdded, removed = carriedRemoved }
        };
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { from = pin.Tag, tag, commit, files = kinds, size },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"The witos patch set from dotnet/runtime {pin.Tag} to {tag} ({commit[..12]}): " +
            string.Join(", ", kinds.Select(entry => $"{entry.Value.Count} {entry.Key}")) +
            $"; +{pinnedAdded}/-{pinnedRemoved} lines at the pin, +{carriedAdded}/-{carriedRemoved} carried ({report}).");
        foreach (var path in kinds["conflicts"])
            Console.WriteLine($"  conflict: {path}");
        if (!apply)
            return;
        if (kinds["conflicts"].Count > 0 || kinds["gone"].Count > 0)
            throw new InvalidOperationException($"The patch set does not carry to {tag} without a person; patches/runtime and the pin stay.");

        // The pin moves with the patches: the tag, its commit and the bytes of every file the set changes there.
        foreach (var name in RuntimeWitos.PATCHES.Values)
            File.Copy(Path.Combine(output, name + ".patch"), Path.Combine(root, "patches", "runtime", name + ".patch"), overwrite: true);
        var lockPath = Path.Combine(root, RuntimeWitos.LOCK);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(lockPath))!.AsObject();
        document["tag"] = tag;
        document["version"] = tag[1..];
        document["commit"] = commit;
        var sources = document["sources"]!.AsArray();
        if (sources.Count != hashes.Count)
            throw new InvalidDataException($"{RuntimeWitos.LOCK} pins {sources.Count} files; the patch set changes {hashes.Count}.");
        foreach (var source in sources)
            source!["sha256"] = hashes[source["path"]!.GetValue<string>()];
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        await File.WriteAllTextAsync(lockPath, document.ToJsonString(options) + "\n");
        Console.WriteLine($"patches/runtime and {RuntimeWitos.LOCK} now pin dotnet/runtime {tag}; run runtime-witos for both ISAs.");
    }

    #endregion

    #region Tools

    // The lines a patch's diff adds and removes.
    private static (int Added, int Removed) Lines(string patch)
    {
        var diff = patch[patch.IndexOf("\n--- a/", StringComparison.Ordinal)..].Split('\n')
            .Where(line => !line.StartsWith("--- a/", StringComparison.Ordinal) && !line.StartsWith("+++ b/", StringComparison.Ordinal)).ToList();
        return (diff.Count(line => line.StartsWith('+')), diff.Count(line => line.StartsWith('-')));
    }

    // A file at a commit, the bytes git stores, or null where the commit has none. The bytes are read from git's output
    // stream as they are, since a decoded capture drops a byte order mark.
    private static async Task<byte[]?> ShowAsync(string repository, string commit, string path)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = repository, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in (string[])["-c", "safe.directory=*", "show", $"{commit}:{path}"])
            start.ArgumentList.Add(argument);
        using var git = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        using var bytes = new MemoryStream();
        var error = git.StandardError.ReadToEndAsync();
        await git.StandardOutput.BaseStream.CopyToAsync(bytes);
        await git.WaitForExitAsync();
        await error;
        return git.ExitCode == 0 ? bytes.ToArray() : null;
    }

    // The text a patch applies to, as runtime-witos reads a file of the tree: decoded without a byte order mark, with LF
    // line ends.
    private static string Text(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    // A unified diff of the tag's text and the carried one, under the a/ and b/ names of the patch format.
    private static async Task<string> DiffAsync(string scratch, string path, string before, string after)
    {
        var a = Path.Combine(scratch, "a");
        var b = Path.Combine(scratch, "b");
        var output = Path.Combine(scratch, "diff");
        await File.WriteAllTextAsync(a, before);
        await File.WriteAllTextAsync(b, after);
        var diff = await Processes.RunAsync("git", ["diff", "--no-index", "--no-color", "-U3", $"--output={output}", a, b], scratch, 120);
        if (diff.TimedOut || diff.ExitCode > 1)
            throw new InvalidOperationException($"git diff failed for {path}. {diff.Error}");
        // git names the enclosing function after a hunk header; the patch format keeps the bare header.
        var lines = (await File.ReadAllTextAsync(output)).Split('\n').SkipWhile(line => !line.StartsWith("@@", StringComparison.Ordinal))
            .Select(line => line.StartsWith("@@", StringComparison.Ordinal) ? line[..(line.IndexOf(" @@", 2, StringComparison.Ordinal) + 3)] : line);
        return $"--- a/{path}\n+++ b/{path}\n" + string.Join("\n", lines);
    }

    private static async Task<string> GitAsync(string directory, string[] arguments, int timeout = 120)
    {
        var result = await Processes.RunAsync("git", ["-c", "safe.directory=*", .. arguments], directory, timeout);
        if (result.TimedOut || result.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments[0]} failed in {directory}. {result.Output}\n{result.Error}");
        return result.Output;
    }

    #endregion
}
