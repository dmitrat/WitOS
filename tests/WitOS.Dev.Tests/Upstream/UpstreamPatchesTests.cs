using System.Text.Json;
using WitOS.Dev.Tests.Support;
using WitOS.Dev.Upstream;

namespace WitOS.Dev.Tests.Upstream;

/// <summary>
/// WitOS changes to upstream files in patches/: each one is made from the pinned bytes, applied by the tool and
/// reproduces its recorded output; the applier rejects any other input.
/// </summary>
[TestFixture]
public sealed class UpstreamPatchesTests
{
    #region Constants

    private const string SAMPLE = "Source: src/sample.c\nOutput: sample.witos.c\nBefore: {0}\nAfter: {1}\n\n" +
        "Changes the middle line.\n\n--- a/src/sample.c\n+++ b/src/sample.c\n@@ -1,3 +1,3 @@\n one\n-two\n+TWO\n three\n";

    #endregion

    #region Functions

    // A patch is made from the canonical upstream bytes: its base hash is the pin itself.
    [Test]
    public void PatchesStartFromThePinnedBytesTest()
    {
        var root = TestEnvironment.Root;
        var pins = Pins(root);
        var patches = Patches(root).ToList();
        Assert.That(patches, Is.Not.Empty);
        foreach (var (repository, output) in patches)
        {
            var patch = UpstreamPatches.Read(root, repository, output);
            Assert.That(pins.TryGetValue((repository, patch.Source), out var pinned), Is.True,
                $"{repository}/{output}: {patch.Source} is not pinned");
            Assert.That(patch.Before, Is.EqualTo(pinned), $"{repository}/{output} is not made from the pinned bytes");
        }
    }

    // The tool names every patch output; a patch nobody applies is stale.
    [Test]
    public void EveryPatchIsAppliedTest()
    {
        var root = TestEnvironment.Root;
        var tool = string.Join("\n", Directory.EnumerateFiles(Path.Combine(root, "tools", "WitOS.Dev"), "*.cs",
            SearchOption.AllDirectories).Select(File.ReadAllText));
        foreach (var (repository, output) in Patches(root))
            Assert.That(tool.Contains($"\"{output}\"", StringComparison.Ordinal), Is.True,
                $"{repository}/{output}.patch is not applied by the tool");
    }

    // With the upstream download cache present, every patch reproduces its recorded output.
    [Test]
    public void PatchesReproduceTheirOutputTest()
    {
        var root = TestEnvironment.Root;
        var applied = 0;
        foreach (var (repository, output) in Patches(root))
        {
            var patch = UpstreamPatches.Read(root, repository, output);
            // The witos patch set's sources are in its own pinned checkout (R1.1); a file it adds starts from no text.
            var cache = repository == "runtime"
                ? Path.Combine(root, ".tools", "upstream", $"runtime-witos-{WitOS.Dev.Runtime.RuntimeWitos.ReadLock(root).Version}")
                : WitOS.Dev.Substrate.MuslLibc.SourceDirectory(root);
            var source = Path.Combine(cache, patch.Source);
            var added = patch.Before == UpstreamPatches.Hash("");
            if (!added && !File.Exists(source))
                continue;
            var text = UpstreamPatches.Apply(patch, added ? "" : File.ReadAllText(source).Replace("\r\n", "\n"));
            Assert.That(UpstreamPatches.Hash(text), Is.EqualTo(patch.After));
            ++applied;
        }
        if (applied == 0)
            Assert.Ignore("No upstream sources are cached; run setup and runtime-witos once.");
    }

    [Test]
    public void ApplyChangesOnlyThePinnedTextTest()
    {
        const string before = "one\ntwo\nthree\n";
        const string after = "one\nTWO\nthree\n";
        var patch = UpstreamPatches.Parse(string.Format(SAMPLE, UpstreamPatches.Hash(before), UpstreamPatches.Hash(after)));
        Assert.That(patch.Purpose, Is.EqualTo("Changes the middle line."));
        Assert.That(UpstreamPatches.Apply(patch, before), Is.EqualTo(after));
        Assert.Throws<InvalidDataException>(() => UpstreamPatches.Apply(patch, "one\ntwo\nthree\nfour\n"));
        Assert.Throws<InvalidDataException>(() => UpstreamPatches.Apply(patch with { After = new string('0', 64) }, before));
        // A hunk that no longer matches fails even when the base hash is forged to agree.
        var moved = patch with { Before = UpstreamPatches.Hash("zero\ntwo\nthree\n") };
        Assert.Throws<InvalidDataException>(() => UpstreamPatches.Apply(moved, "zero\ntwo\nthree\n"));
    }

    // A file WitOS adds to an upstream tree is a patch from no text (R1.1); any existing text is refused.
    [Test]
    public void ApplyCreatesAnAddedFileTest()
    {
        const string after = "one\ntwo\n";
        var patch = UpstreamPatches.Parse(
            $"Source: src/added.c\nOutput: added.c\nBefore: {UpstreamPatches.Hash("")}\nAfter: {UpstreamPatches.Hash(after)}\n\n" +
            "Adds a file.\n\n--- a/src/added.c\n+++ b/src/added.c\n@@ -0,0 +1,2 @@\n+one\n+two\n");
        Assert.That(UpstreamPatches.Apply(patch, ""), Is.EqualTo(after));
        Assert.Throws<InvalidDataException>(() => UpstreamPatches.Apply(patch, "existing\n"));
    }

    // An upstream text without a final line end keeps it missing (R1.1); a hunk that reaches that last line is refused.
    [Test]
    public void ApplyKeepsAMissingFinalLineEndTest()
    {
        const string before = "one\ntwo\nthree\nfour";
        const string after = "one\nTWO\nthree\nfour";
        var patch = UpstreamPatches.Parse(string.Format(SAMPLE, UpstreamPatches.Hash(before), UpstreamPatches.Hash(after)));
        Assert.That(UpstreamPatches.Apply(patch, before), Is.EqualTo(after));
        const string threeLines = "one\ntwo\nthree";
        var last = patch with { Before = UpstreamPatches.Hash(threeLines) };
        Assert.Throws<InvalidDataException>(() => UpstreamPatches.Apply(last, threeLines));
    }

    [Test]
    public void ParseRejectsMalformedPatchesTest()
    {
        var hash = new string('a', 64);
        var good = string.Format(SAMPLE, hash, hash);
        Assert.That(UpstreamPatches.Parse(good).Hunks, Has.Length.EqualTo(1));
        Assert.Throws<InvalidDataException>(() => UpstreamPatches.Parse(good.Replace("@@ -1,3 +1,3 @@", "@@ -1,4 +1,3 @@")));
        Assert.Throws<InvalidDataException>(() => UpstreamPatches.Parse(good.Replace("+++ b/src/sample.c", "+++ b/src/other.c")));
        Assert.Throws<InvalidDataException>(() => UpstreamPatches.Parse(good.Replace("Before: " + hash, "Before: 1234")));
        Assert.Throws<InvalidDataException>(() => UpstreamPatches.Parse(good.Replace("Changes the middle line.\n", "")));
        Assert.Throws<InvalidDataException>(() => UpstreamPatches.Parse(good.Replace(" three\n", "\\ three\n")));
    }

    #endregion

    #region Tools

    private static IEnumerable<(string Repository, string Output)> Patches(string root)
        => Directory.EnumerateFiles(UpstreamPatches.Directory(root), "*.patch", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => (Path.GetFileName(Path.GetDirectoryName(path)!), Path.GetFileNameWithoutExtension(path)));

    private static Dictionary<(string, string), string> Pins(string root)
    {
        var pins = new Dictionary<(string, string), string>();
        // The Unix-form runtime's patch set: the files it changes, by their bytes at the pinned commit (R1.1).
        foreach (var (path, sha256) in WitOS.Dev.Runtime.RuntimeWitos.ReadLock(root).Sources)
            pins[("runtime", path)] = sha256;
        // The C library of the system layer: the files WitOS patches, by their bytes in the pinned tarball (S1.1).
        var musl = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, WitOS.Dev.Substrate.MuslLibc.LOCK))).RootElement;
        foreach (var source in musl.GetProperty("sources").EnumerateArray())
            pins[("musl", source.GetProperty("path").GetString()!)] = source.GetProperty("sha256").GetString()!;
        return pins;
    }

    #endregion
}
