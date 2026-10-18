using System.Text;
using WitOS.Dev.Commands;
using WitOS.Dev.Runtime;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Runtime;

/// <summary>
/// The layer of base applications (ADR 0027, plan step C4.1): the catalog's entries checked whole, a launcher written as
/// the SDK writes an apphost, and a new version installed beside the old with the rollback to it, in a directory of the
/// host and on an ext4 volume through debugfs; the file manager's entry built from its source (C4.2).
/// </summary>
[TestFixture]
public sealed class AppsLayerTests
{
    #region Constants

    private const string PLACEHOLDER = "c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2";

    #endregion

    #region Functions

    [Test]
    public async Task CatalogHoldsPowerShellTest()
    {
        var catalog = await AppCatalog.ReadAsync(TestEnvironment.Root);
        var powershell = catalog.Single(entry => entry.Id == "PowerShell");
        Assert.That(powershell.Source.Kind, Is.EqualTo(AppCatalog.ARCHIVE));
        Assert.That(powershell.Commands.Select(command => (command.Name, command.Assembly)), Is.EqualTo(new[] { ("pwsh", "pwsh.dll") }));
        Assert.That(powershell.Native?.Library, Is.EqualTo("libpsl-native.so"));
        Assert.That(File.Exists(Path.Combine(TestEnvironment.Root, "src", "Substrate", "powershell.lock.json")), Is.False,
            "the entry moved from the substrate's lock to the catalog");
    }

    [Test]
    public async Task CatalogHoldsTheFileManagerTest()
    {
        var catalog = await AppCatalog.ReadAsync(TestEnvironment.Root);
        var commander = catalog.Single(entry => entry.Id == "Arlecchino.Commander");
        Assert.That(commander.Source.Kind, Is.EqualTo(AppCatalog.SOURCE));
        Assert.That(commander.Source.Repository, Is.EqualTo("https://github.com/The1fEst/Arlecchino.Commander"));
        Assert.That(commander.Source.Properties, Is.EqualTo(new[] { "PublishAot=false" }), "a portable application, not NativeAOT");
        Assert.That(commander.Commands.Select(command => (command.Name, command.Assembly)), Is.EqualTo(new[] { ("arlc", "arlc.dll") }));
        Assert.That(commander.Native, Is.Null, "no native part: input and output through System.Console");
    }

    [Test]
    public void CatalogEntriesAreCheckedWholeTest()
    {
        Assert.That(AppCatalog.Parse(Entry("Commander", "1.0", """{ "kind": "source", "repository": "https://github.com/a/b", "commit": "#", "project": "App.csproj", "properties": [ "PublishAot=false" ] }"""),
            "Commander").Source.Properties, Is.EqualTo(new[] { "PublishAot=false" }));
        Assert.Throws<InvalidDataException>(() => AppCatalog.Parse(Entry("Commander", "1.0",
            """{ "kind": "source", "repository": "https://github.com/a/b", "commit": "#", "project": "App.csproj", "properties": [ "A=1;B=2" ] }"""), "Commander"),
            "one MSBuild property an item");
        Assert.That(AppCatalog.Parse(Entry("Tool", "1.2.3", """{ "kind": "tool", "package": "csharprepl", "sha256": "%" }"""), "Tool").Source.Package,
            Is.EqualTo("csharprepl"));
        Assert.That(AppCatalog.Parse(Entry("Commander", "2026.9.19", """{ "kind": "source", "repository": "https://github.com/a/b", "commit": "#", "project": "src/App.csproj" }"""),
            "Commander").Source.Commit, Is.EqualTo(new string('1', 40)));
        Assert.Throws<InvalidDataException>(() => AppCatalog.Parse(Entry("Other", "1.0", Archive()), "Tool"), "the identity names the file");
        Assert.Throws<InvalidDataException>(() => AppCatalog.Parse(Entry("Tool", "v1", Archive()), "Tool"), "a version of numbers");
        Assert.Throws<InvalidDataException>(() => AppCatalog.Parse(Entry("Tool", "1.0", """{ "kind": "binary", "url": "https://x/y" }"""), "Tool"),
            "one of the three kinds");
        Assert.Throws<InvalidDataException>(() => AppCatalog.Parse(Entry("Tool", "1.0", """{ "kind": "archive", "url": "http://x/a.tar.gz", "file": "a.tar.gz", "sha256": "%" }"""),
            "Tool"), "an https archive");
        Assert.Throws<InvalidDataException>(() => AppCatalog.Parse(Entry("Tool", "1.0", Archive(), command: """{ "name": "tool", "assembly": "../tool.dll" }"""), "Tool"),
            "an assembly within the application");
    }

    [Test]
    public void LauncherCarriesTheApplicationsPathTest()
    {
        var apphost = Apphost();
        var launcher = AppsLayer.Launcher(apphost, "../PowerShell/7.6.7/pwsh.dll");
        Assert.That(launcher.Length, Is.EqualTo(apphost.Length));
        var text = Encoding.UTF8.GetString(launcher);
        Assert.That(text, Does.Contain("../PowerShell/7.6.7/pwsh.dll\0"), "the path, ended by zeros, where the placeholder was");
        Assert.That(text, Does.Not.Contain(PLACEHOLDER));
        Assert.That(text, Does.StartWith("ELF-head").And.EndWith("tail"), "the rest of the apphost stays");
        Assert.Throws<InvalidDataException>(() => AppsLayer.Launcher(Encoding.UTF8.GetBytes("no placeholder"), "a.dll"));
        Assert.Throws<InvalidDataException>(() => AppsLayer.Launcher(apphost, new string('a', 1024)), "the path fits the host's buffer");
    }

    [Test]
    public async Task NewVersionBesideTheOldAndRollbackTest()
    {
        var directory = Directory.CreateTempSubdirectory("witos-apps-");
        try
        {
            var store = new DirectoryLayerStore(Path.Combine(directory.FullName, "volume"));
            await InstallAndRollBackAsync(store, directory.FullName);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Ext4VolumeTakesTheLayerThroughDebugfsTest()
    {
        if (RuntimeExt4Volume.FindMkfs() is null)
            Assert.Ignore("The host has no e2fsprogs (mkfs.ext4).");
        var directory = Directory.CreateTempSubdirectory("witos-apps-ext4-");
        try
        {
            var disk = Path.Combine(directory.FullName, "data.img");
            await RuntimeDataDisk.CreateAsync(disk);
            var before = Head(disk);
            await RuntimeDataDisk.EditExt4Async(disk, volume => InstallAndRollBackAsync(new Ext4LayerStore(volume, Path.Combine(directory.FullName, "debugfs")),
                directory.FullName));
            Assert.That(Head(disk), Is.EqualTo(before), "the partition table and the partitions before the ext4 one stay as they were");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public void AppsCommandReadsItsOptionsTest()
    {
        var (architecture, disk, use) = CommandApps.Parse(["--arch", "arm64", "--disk", "data.img", "--use", "PowerShell=7.6.6"]);
        Assert.That((architecture.Name, disk, use), Is.EqualTo(("arm64", "data.img", ((string, string)?)("PowerShell", "7.6.6"))));
        Assert.That(CommandApps.Parse(["--disk", "d.img"]).Use, Is.Null);
        Assert.Throws<ArgumentException>(() => CommandApps.Parse([]), "the disk is required");
        Assert.Throws<ArgumentException>(() => CommandApps.Parse(["--disk", "d.img", "--use", "PowerShell"]));
    }

    // Version 1.0 of a fake application with a command, then 2.0 beside it, then the rollback to 1.0: the files of each
    // version stay, current names the version in use, and the launcher names its assembly.
    private static async Task InstallAndRollBackAsync(ILayerStore store, string scratch)
    {
        var apphost = Apphost();
        var program = Path.Combine(scratch, "tool.dll");
        await File.WriteAllTextAsync(program, "IL");
        var first = AppCatalog.Parse(Entry("Tool", "1.0", Archive()), "Tool");
        Assert.That(await AppsLayer.InstallAsync(store, first, [("tool.dll", program), ("lib/data.txt", program)], apphost, Path.Combine(scratch, "launchers")),
            Is.True);
        Assert.That(await AppsLayer.InstallAsync(store, first, [("tool.dll", program)], apphost, Path.Combine(scratch, "launchers")), Is.False,
            "a version the layer has is not written again");
        var second = AppCatalog.Parse(Entry("Tool", "2.0", Archive()), "Tool");
        await AppsLayer.InstallAsync(store, second, [("tool.dll", program)], apphost, Path.Combine(scratch, "launchers"));
        Assert.That(await AppsLayer.VersionsAsync(store, "Tool"), Is.EqualTo(new[] { "1.0", "2.0" }), "the new version beside the old");
        Assert.That(await AppsLayer.CurrentAsync(store, "Tool"), Is.EqualTo("2.0"));
        Assert.That(await LauncherTargetAsync(store, scratch), Is.EqualTo("../Tool/2.0/tool.dll"));
        Assert.That(await store.ReadTextAsync("apps/Tool/1.0/lib/data.txt"), Is.EqualTo("IL"), "the old version's files stay");
        Assert.That(await store.ReadTextAsync("apps/Tool/2.0/witos-app.json"), Does.Contain("\"version\": \"2.0\""), "the manifest is the entry");

        await AppsLayer.UseAsync(store, "Tool", "1.0", apphost, Path.Combine(scratch, "launchers"));
        Assert.That(await AppsLayer.CurrentAsync(store, "Tool"), Is.EqualTo("1.0"), "the rollback");
        Assert.That(await LauncherTargetAsync(store, scratch), Is.EqualTo("../Tool/1.0/tool.dll"));
        Assert.ThrowsAsync<InvalidOperationException>(() => AppsLayer.UseAsync(store, "Tool", "3.0", apphost, Path.Combine(scratch, "launchers")));
    }

    private static async Task<string> LauncherTargetAsync(ILayerStore store, string scratch)
    {
        var text = await store.ReadTextAsync("apps/bin/tool") ?? throw new InvalidOperationException("No launcher.");
        var at = text.IndexOf("../", StringComparison.Ordinal);
        return text[at..text.IndexOf('\0', at)];
    }

    // The disk's bytes before its ext4 partition: the table, the test partition and the FAT volume.
    private static byte[] Head(string disk) =>
        System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(disk).AsSpan(0, (int)(RuntimeDataDisk.EXT4_PARTITION_FIRST_BLOCK * 512)));

    // An apphost's stand-in: bytes before, the placeholder and the zeros of the host's buffer, bytes after.
    private static byte[] Apphost() => Encoding.UTF8.GetBytes("ELF-head" + PLACEHOLDER + new string('\0', 1025 - PLACEHOLDER.Length) + "tail");

    private static string Archive() => """{ "kind": "archive", "url": "https://example.org/tool.tar.gz", "file": "tool.tar.gz", "sha256": "%" }""";

    private static string Entry(string id, string version, string source, string command = """{ "name": "tool", "assembly": "tool.dll" }""") =>
        $$"""{ "id": "{{id}}", "version": "{{version}}", "framework": "Microsoft.NETCore.App 10.0", "source": {{source}}, "commands": [ {{command}} ], "purpose": "a test" }"""
            .Replace("%", new string('a', 64)).Replace("#", new string('1', 40));

    #endregion
}
