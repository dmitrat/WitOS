using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Arlecchino.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WitOS.Installer;

namespace WitOS.Apps.Tests;

/// <summary>
/// The installer (plan step C4.3) on the host: its screen in Arlecchino's headless host, driven by keys — an update from
/// the feed beside the installed version, the rollback through the version picker, F10 — and its commands for scripts.
/// </summary>
[TestFixture]
public sealed class InstallerTests
{
    #region Constants

    private const string PLACEHOLDER = "c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2";

    private static readonly TimeSpan WAIT = TimeSpan.FromSeconds(30);

    #endregion

    #region Fields

    private DirectoryInfo m_scratch = null!;
    private InstallerOptions m_options = null!;

    #endregion

    #region Functions

    [SetUp]
    public async Task SetUpAsync()
    {
        m_scratch = Directory.CreateTempSubdirectory("witos-installer-");
        var feed = Path.Combine(m_scratch.FullName, "feed");
        var apphost = Path.Combine(m_scratch.FullName, "apphost");
        await File.WriteAllBytesAsync(apphost, Encoding.UTF8.GetBytes("ELF-head" + PLACEHOLDER + new string('\0', 1025 - PLACEHOLDER.Length) + "tail"));
        var old = await WriteVersionAsync(feed, "1.0");
        await WriteVersionAsync(feed, "2.0");
        m_options = new InstallerOptions(Path.Combine(m_scratch.FullName, "apps"), feed, apphost);
        // The layer holds 1.0, as the host's tool lays it; the feed offers 2.0 beside it.
        await new AppsDirectory(m_options.Layer).InstallAsync(old, await File.ReadAllBytesAsync(apphost));
    }

    [TearDown]
    public void TearDown() => m_scratch.Delete(recursive: true);

    [Test]
    public void ScreenUpdatesRollsBackAndQuitsTest()
    {
        var lifetime = new StoppingLifetime();
        using var app = new ArlecchinoTestHost(100, 30, builder =>
        {
            builder.Services.AddSingleton<IHostApplicationLifetime>(lifetime);
            InstallerScreen.Add(builder, m_options);
        });
        var layer = new AppsDirectory(m_options.Layer);
        Assert.That(app.FrameLineContaining("Tool"), Does.Contain("1.0").And.Contain("2.0 update"));

        app.Press(ConsoleKey.I);
        Assert.That(Until(app, "Installed Tool 2.0"), Is.True, app.Frame());
        Assert.That(layer.Versions("Tool"), Is.EqualTo(new[] { "1.0", "2.0" }), "the update beside the installed version");
        Assert.That(layer.Current("Tool"), Is.EqualTo("2.0"));
        Assert.That(app.FrameLineContaining("Tool"), Does.Contain("2.0 installed"));

        app.Press(ConsoleKey.U);
        Assert.That(app.Frame(), Does.Contain("Use which version of Tool?"));
        app.Press(ConsoleKey.UpArrow); // the picker starts on the version in use
        app.Press(ConsoleKey.Enter);
        Assert.That(Until(app, "Tool uses 1.0"), Is.True, app.Frame());
        Assert.That(layer.Current("Tool"), Is.EqualTo("1.0"), "the rollback");
        Assert.That(Launcher(), Does.Contain("../Tool/1.0/tool.dll\0"));

        app.Press(ConsoleKey.F10);
        app.Frame();
        Assert.That(lifetime.Stopped, Is.True);
    }

    [Test]
    public async Task CommandsListInstallAndUseTest()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.That(await InstallerCommands.RunAsync("list", [], m_options, output, error), Is.EqualTo(0));
        Assert.That(output.ToString(), Does.Contain("Tool: installed 1.0; in use 1.0; feed 2.0 (update)"));

        output.GetStringBuilder().Clear();
        Assert.That(await InstallerCommands.RunAsync("install", ["Tool"], m_options, output, error), Is.EqualTo(0), error.ToString());
        Assert.That(output.ToString(), Does.Contain("Verifying Tool 2.0").And.Contain("Installed Tool 2.0"));

        output.GetStringBuilder().Clear();
        Assert.That(await InstallerCommands.RunAsync("use", ["Tool", "1.0"], m_options, output, error), Is.EqualTo(0));
        Assert.That(output.ToString(), Does.Contain("Tool uses 1.0"));
        Assert.That(await InstallerCommands.RunAsync("use", ["Tool", "3.0"], m_options, output, error), Is.EqualTo(1));
        Assert.That(error.ToString(), Does.Contain("The layer has no Tool 3.0."));
        Assert.That(await InstallerCommands.RunAsync("install", ["Other"], m_options, output, error), Is.EqualTo(1));

        output.GetStringBuilder().Clear();
        await InstallerCommands.RunAsync("list", [], m_options, output, error);
        Assert.That(output.ToString(), Does.Contain("Tool: installed 1.0, 2.0; in use 1.0; feed 2.0 (installed)"));
    }

    [Test]
    public void OptionsAreReadWholeTest()
    {
        var (verb, operands, options) = InstallerOptions.Parse(["install", "Tool", "2.0", "--feed", "/fat/feed", "--layer", "/x/apps"]);
        Assert.That((verb, operands, options.Feed, options.Layer), Is.EqualTo(("install", (System.Collections.Generic.IReadOnlyList<string>)["Tool", "2.0"], "/fat/feed", "/x/apps")));
        Assert.That(InstallerOptions.Parse([]).Options.Layer, Is.EqualTo(AppsDirectory.GUEST_ROOT), "the screen on the data volume's layer");
        Assert.That(InstallerOptions.Parse(["--feed", "/fat/feed"]).Verb, Is.Empty);
        Assert.Throws<ArgumentException>(() => InstallerOptions.Parse(["install", "Tool"]), "an installation needs a feed");
        Assert.Throws<ArgumentException>(() => InstallerOptions.Parse(["use", "Tool"]), "use needs a version");
        Assert.Throws<ArgumentException>(() => InstallerOptions.Parse(["remove", "Tool"]));
        Assert.Throws<ArgumentException>(() => InstallerOptions.Parse(["--feed"]));
    }

    // Draws frames until the screen shows a text, the installation running off the frame thread meanwhile.
    private static bool Until(ArlecchinoTestHost app, string text)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < WAIT)
        {
            if (app.Frame().Contains(text, StringComparison.Ordinal))
                return true;
            Thread.Sleep(5);
        }
        return false;
    }

    private string Launcher() => Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(m_options.Layer, AppsDirectory.BIN, "tool")));

    private async Task<FeedEntry> WriteVersionAsync(string feed, string version)
    {
        var source = Path.Combine(m_scratch.FullName, "tool-" + version + ".dll");
        await File.WriteAllTextAsync(source, "IL of " + version);
        return await AppFeed.WriteAsync(feed,
            $$"""{ "id": "Tool", "version": "{{version}}", "framework": "Microsoft.NETCore.App 10.0", "commands": [ { "name": "tool", "assembly": "tool.dll" } ], "purpose": "a test" }""",
            [("tool.dll", source)]);
    }

    #endregion

    // The host's lifetime as far as the screen uses it: F10 asks it to stop.
    private sealed class StoppingLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public bool Stopped { get; private set; }

        public void StopApplication() => Stopped = true;
    }
}
