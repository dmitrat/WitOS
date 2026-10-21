using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WitOS.Apps.Tests;

/// <summary>
/// The layer of base applications in a directory and its folder feed (ADR 0027, plan step C4.3): a feed written and read
/// back, a version installed from it beside the old and the rollback, and every refusal leaving the layer as it was — a
/// package whose bytes differ from its entry's SHA-256, a zip entry that leaves the version's directory, a package without
/// a command's assembly.
/// </summary>
[TestFixture]
public sealed class AppsDirectoryTests
{
    #region Constants

    private const string PLACEHOLDER = "c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2";

    #endregion

    #region Fields

    private DirectoryInfo m_scratch = null!;

    #endregion

    #region Functions

    [SetUp]
    public void SetUp() => m_scratch = Directory.CreateTempSubdirectory("witos-apps-");

    [TearDown]
    public void TearDown() => m_scratch.Delete(recursive: true);

    [Test]
    public async Task FeedVersionInstalledBesideTheOldAndRolledBackTest()
    {
        var feed = Path.Combine(m_scratch.FullName, "feed");
        var first = await WriteVersionAsync(feed, "1.0");
        var second = await WriteVersionAsync(feed, "2.0");
        var reading = AppFeed.Read(feed);
        Assert.That(reading.Problems, Is.Empty);
        Assert.That(reading.Entries.Select(entry => entry.Manifest.Version), Is.EqualTo(new[] { "1.0", "2.0" }));
        Assert.That(reading.Entries[1].Sha256, Is.EqualTo(second.Sha256));

        var layer = new AppsDirectory(Path.Combine(m_scratch.FullName, "apps"));
        var steps = new List<string>();
        Assert.That(await layer.InstallAsync(reading.Entries[0], Apphost(), steps.Add), Is.True);
        Assert.That(await layer.InstallAsync(first, Apphost()), Is.False, "a version the layer has is not written again");
        Assert.That(await layer.InstallAsync(reading.Entries[1], Apphost()), Is.True);
        Assert.That(steps, Has.Some.StartsWith("Verifying Tool 1.0").And.Some.EqualTo("Installed Tool 1.0"));
        Assert.That(layer.Applications(), Is.EqualTo(new[] { "Tool" }));
        Assert.That(layer.Versions("Tool"), Is.EqualTo(new[] { "1.0", "2.0" }), "the new version beside the old");
        Assert.That(layer.Current("Tool"), Is.EqualTo("2.0"));
        Assert.That(LauncherTarget(layer), Is.EqualTo("../Tool/2.0/tool.dll"));
        Assert.That(File.ReadAllText(Path.Combine(layer.Root, "Tool", "1.0", "lib", "data.txt")), Is.EqualTo("data of 1.0"), "the old version's files stay");
        var installed = File.ReadAllText(Path.Combine(layer.Root, "Tool", "2.0", AppsDirectory.MANIFEST));
        Assert.That(AppManifest.Parse(installed).Version, Is.EqualTo("2.0"));
        Assert.That(installed, Does.Contain("\"purpose\": \"a test\"").And.Not.Contain("\r"), "the feed's manifest whole, with line feeds");
        Assert.That(Directory.EnumerateDirectories(Path.Combine(layer.Root, "Tool")).Select(Path.GetFileName), Has.None.StartsWith("."), "no partial directory is left");

        layer.Use("Tool", "1.0", Apphost());
        Assert.That(layer.Current("Tool"), Is.EqualTo("1.0"), "the rollback");
        Assert.That(LauncherTarget(layer), Is.EqualTo("../Tool/1.0/tool.dll"));
        Assert.Throws<InvalidOperationException>(() => layer.Use("Tool", "3.0", Apphost()));
    }

    [Test]
    public async Task PackageOfOtherBytesIsRefusedTest()
    {
        var feed = Path.Combine(m_scratch.FullName, "feed");
        var entry = await WriteVersionAsync(feed, "1.0");
        await using (var stream = new FileStream(entry.Package, FileMode.Append))
            stream.WriteByte(0);
        var layer = new AppsDirectory(Path.Combine(m_scratch.FullName, "apps"));
        var error = Assert.ThrowsAsync<InvalidDataException>(() => layer.InstallAsync(AppFeed.Read(feed).Entries.Single(), Apphost()));
        Assert.That(error!.Message, Does.Contain("SHA-256").And.Contain("nothing was installed"));
        AssertUntouched(layer);
    }

    [Test]
    public async Task ZipEntryLeavingTheDirectoryIsRefusedTest()
    {
        var feed = Path.Combine(m_scratch.FullName, "feed");
        var entry = await WriteVersionAsync(feed, "1.0");
        await using (var stream = new FileStream(entry.Package, FileMode.Open))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update))
        {
            await using var writer = new StreamWriter(await zip.CreateEntry("../escape.txt").OpenAsync());
            await writer.WriteAsync("out");
        }
        var rehashed = entry with { Sha256 = await AppFeed.HashAsync(entry.Package) };
        var layer = new AppsDirectory(Path.Combine(m_scratch.FullName, "apps"));
        var error = Assert.ThrowsAsync<InvalidDataException>(() => layer.InstallAsync(rehashed, Apphost()));
        Assert.That(error!.Message, Does.Contain("../escape.txt"));
        Assert.That(File.Exists(Path.Combine(layer.Root, "Tool", "escape.txt")), Is.False);
        AssertUntouched(layer);
    }

    [Test]
    public async Task FeedChecksEachEntryWholeTest()
    {
        var feed = Path.Combine(m_scratch.FullName, "feed");
        await WriteVersionAsync(feed, "1.0");
        Assert.ThrowsAsync<InvalidDataException>(() => AppFeed.WriteAsync(feed, Manifest("3.0"), [("other.dll", Source("x"))]),
            "the files hold the command's assembly");
        Assert.ThrowsAsync<InvalidDataException>(() => AppFeed.WriteAsync(feed, Manifest("3.0"), [("tool.dll", Source("x")), ("../up.txt", Source("x"))]),
            "a path within the version's directory");
        var stray = Path.Combine(feed, "Tool", "4.0.json");
        File.Copy(Path.Combine(feed, "Tool", "1.0.json"), stray);
        File.WriteAllText(Path.Combine(feed, "Tool", "5.0.json"), "{ \"manifest\": " + Manifest("5.0") + ", \"package\": { \"file\": \"5.0.zip\", \"sha256\": \"" + new string('a', 64) + "\" } }");
        var reading = AppFeed.Read(feed);
        Assert.That(reading.Entries.Select(entry => entry.Manifest.Version), Is.EqualTo(new[] { "1.0" }), "the valid entry stays usable");
        Assert.That(reading.Problems, Has.Count.EqualTo(2));
        Assert.That(reading.Problems, Has.Some.Contains("4.0.json").And.Some.Contains("5.0.zip of Tool 5.0 is missing"));
        Assert.Throws<DirectoryNotFoundException>(() => AppFeed.Read(Path.Combine(m_scratch.FullName, "none")));
    }

    // The SDK a base application brings (C2.5): putting a version in use writes global.json above the layer with the
    // roots of the current versions that hold sdk/, and takes the file away once none does.
    [Test]
    public void SdkRootsFollowTheVersionsInUseTest()
    {
        var layer = new AppsDirectory(Path.Combine(m_scratch.FullName, "apps"));
        var globalJson = Path.Combine(m_scratch.FullName, "global.json");
        foreach (var (version, sdk) in new[] { ("1.0", true), ("2.0", false) })
        {
            var directory = Path.Combine(layer.Root, "Tool", version);
            Directory.CreateDirectory(Path.Combine(directory, sdk ? "sdk" : "lib"));
            File.WriteAllText(Path.Combine(directory, AppsDirectory.MANIFEST), Manifest(version, commands: ""));
        }
        layer.Use("Tool", "1.0", Apphost());
        Assert.That(File.ReadAllText(globalJson), Is.EqualTo(SdkRoots.GlobalJson(["apps/Tool/1.0"])));
        Assert.That(Directory.Exists(Path.Combine(layer.Root, AppsDirectory.BIN)) && Directory.EnumerateFiles(Path.Combine(layer.Root, AppsDirectory.BIN)).Any(),
            Is.False, "no launcher for an application without a command");
        layer.Use("Tool", "2.0", Apphost());
        Assert.That(File.Exists(globalJson), Is.False, "the version in use holds no SDK");
    }

    [Test]
    public void ManifestIsCheckedWholeTest()
    {
        var manifest = AppManifest.Parse(Manifest("1.2.3"));
        Assert.That((manifest.Id, manifest.Version, manifest.Commands.Single()), Is.EqualTo(("Tool", "1.2.3", new AppCommand("tool", "tool.dll"))));
        Assert.That(AppManifest.Parse(Manifest("1.0", commands: "")).Commands, Is.Empty, "an application without a command, which others reach");
        Assert.Throws<InvalidDataException>(() => AppManifest.Parse("not json"));
        Assert.Throws<InvalidDataException>(() => AppManifest.Parse(Manifest("v1")), "a version of numbers");
        Assert.Throws<InvalidDataException>(() => AppManifest.Parse(Manifest("1.0").Replace("tool.dll", "../tool.dll")), "an assembly within the directory");
        Assert.Throws<InvalidDataException>(() => AppManifest.Parse(Manifest("1.0").Replace("\"Tool\"", "\"tool\"")), "an identity of the catalog's form");
    }

    [Test]
    public void VersionsOrderByTheirNumbersTest()
    {
        string[] versions = ["2026.9.19", "7.6", "2026.9.1", "7.6.7", "10.0"];
        Assert.That(versions.Order(AppVersion.ORDER), Is.EqualTo(new[] { "7.6", "7.6.7", "10.0", "2026.9.1", "2026.9.19" }));
        Assert.That(AppVersion.Latest(versions), Is.EqualTo("2026.9.19"));
        Assert.That(AppVersion.Latest([]), Is.Null);
    }

    private static void AssertUntouched(AppsDirectory layer)
    {
        Assert.That(layer.Versions("Tool"), Is.Empty, "nothing was installed");
        Assert.That(layer.Current("Tool"), Is.Null);
        Assert.That(File.Exists(Path.Combine(layer.Root, AppsDirectory.BIN, "tool")), Is.False, "no launcher");
        var directory = Path.Combine(layer.Root, "Tool");
        Assert.That(Directory.Exists(directory) ? Directory.EnumerateFileSystemEntries(directory) : [], Is.Empty, "no partial directory is left");
    }

    private async Task<FeedEntry> WriteVersionAsync(string feed, string version) =>
        await AppFeed.WriteAsync(feed, Manifest(version), [("tool.dll", Source("IL of " + version)), ("lib/data.txt", Source("data of " + version))]);

    private string Source(string text)
    {
        var path = Path.Combine(m_scratch.FullName, Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, text);
        return path;
    }

    private static string LauncherTarget(AppsDirectory layer)
    {
        var text = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(layer.Root, AppsDirectory.BIN, "tool")));
        var at = text.IndexOf("../", StringComparison.Ordinal);
        return text[at..text.IndexOf('\0', at)];
    }

    // An apphost's stand-in: bytes before, the placeholder and the zeros of the host's buffer, bytes after.
    private static byte[] Apphost() => Encoding.UTF8.GetBytes("ELF-head" + PLACEHOLDER + new string('\0', 1025 - PLACEHOLDER.Length) + "tail");

    private static string Manifest(string version, string commands = """{ "name": "tool", "assembly": "tool.dll" }""") =>
        $$"""{ "id": "Tool", "version": "{{version}}", "framework": "Microsoft.NETCore.App 10.0", "commands": [ {{commands}} ], "purpose": "a test" }""";

    #endregion
}
