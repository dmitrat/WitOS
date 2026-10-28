using System.Security.Cryptography;
using System.Text;
using WitOS.Apps;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The host's half of plan step R9.2: a version's native code, compiled on the Linux host by the tree's crossgen2 for the
/// architecture exactly as the guest's installer compiles it (WitOS.Apps.NativeCode decides what is compiled and with
/// which references), so that a disk the tool writes carries native-&lt;arch&gt;/ beside each version with commands and the
/// launchers name it. The references are the frameworks the install lays out for the architecture. A compilation is kept
/// under .tools/apps-native, named by the hash of everything it reads: the compiler, the rules, the framework and the
/// version's files.
/// </summary>
internal sealed class NativeCodeBuild
{
    #region Constants

    // A compilation's wall-clock limit: PowerShell's assemblies take seconds on the host, its largest a few.
    private const int CROSSGEN2_TIMEOUT_SECONDS = 1800;

    private const int TAIL_LINES = 20;

    #endregion

    #region Fields

    private readonly string m_root;

    private readonly string m_crossgen2;

    private readonly string m_framework;

    #endregion

    #region Constructors

    private NativeCodeBuild(string root, KernelArchitecture architecture, string crossgen2, string framework)
    {
        m_root = root;
        Architecture = architecture;
        m_crossgen2 = crossgen2;
        m_framework = framework;
    }

    #endregion

    #region Functions

    /// <summary>
    /// The builder of an architecture whose framework is ReadyToRun (R9), or null where the layer stays IL alone (riscv64,
    /// until R9.3).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The builder, or null.</returns>
    /// <exception cref="InvalidOperationException">The tree holds no host crossgen2 or no framework for the architecture.</exception>
    public static NativeCodeBuild? For(string root, KernelArchitecture architecture)
    {
        if (!architecture.ReadyToRunFramework)
            return null;
        var crossgen2 = RuntimeWitos.HostCrossgen2Path(root);
        var framework = RuntimeWitos.InstalledFrameworkDirectory(root, architecture);
        if (!File.Exists(crossgen2) || !Directory.Exists(framework))
            throw new InvalidOperationException($"No host crossgen2 at {crossgen2} or no framework for witos-{architecture.Name} at {framework}. " +
                $"Run: dotnet run --project tools/WitOS.Dev -- runtime-witos --arch {architecture.Name}");
        return new NativeCodeBuild(root, architecture, crossgen2, framework);
    }

    /// <summary>
    /// A version's native directory, compiled unless the cache holds it: its files by their paths in the native directory,
    /// the marker last, so that a store that writes them in order has the marker only once the rest is there. A version
    /// without commands has none, since nothing launches it.
    /// </summary>
    /// <param name="entry">The version's catalog entry.</param>
    /// <param name="files">Its files, by their paths in the version's directory.</param>
    /// <returns>The native directory's files.</returns>
    public async Task<IReadOnlyList<(string Path, string Source)>> FilesAsync(AppEntry entry, IReadOnlyList<(string Path, string Source)> files) =>
        entry.Commands.Count == 0 ? [] : await CompileAsync($"{entry.Id}-{entry.Version}", $"{entry.Id} {entry.Version}", files);

    /// <summary>
    /// Any set of files compiled as a version is (the system's own managed parts too: crossgen2 and the installer in the
    /// boot package): the native directory's files, the marker last.
    /// </summary>
    /// <param name="name">The cache's name for the set.</param>
    /// <param name="label">What the console lines call it.</param>
    /// <param name="files">The files, by their paths in the set's directory.</param>
    /// <returns>The native directory's files.</returns>
    public async Task<IReadOnlyList<(string Path, string Source)>> CompileAsync(string name, string label, IReadOnlyList<(string Path, string Source)> files)
    {
        var key = await KeyAsync(files);
        var cache = Path.Combine(m_root, ".tools", "apps-native", Architecture.Name, $"{name}-{key}");
        var version = Path.Combine(cache, "version");
        var native = Path.Combine(version, NativeCode.DirectoryName(Architecture.Name));
        if (!NativeCode.IsCompiled(version, Architecture.Name))
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, recursive: true);
            foreach (var (path, source) in files)
            {
                var target = Path.Combine(version, path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }
            var started = DateTime.UtcNow;
            await NativeCode.CompileAsync(version, Architecture.Name, [m_framework], RunAsync,
                line => Console.WriteLine($"{label} for {Architecture.Name}: {line}"));
            Console.WriteLine($"{label} for {Architecture.Name}: compiled in {(DateTime.UtcNow - started).TotalSeconds:0} s");
            // The set's earlier compilations, by another compiler, framework or rules, are not read again.
            foreach (var stale in Directory.EnumerateDirectories(Path.GetDirectoryName(cache)!, name + "-*").Where(other => other != cache &&
                         Path.GetFileName(other).Length == name.Length + 1 + key.Length))
                Directory.Delete(stale, recursive: true);
        }
        var report = NativeCode.ReadMarker(native);
        Console.WriteLine($"Native code of {label} for {Architecture.Name}: {report.Compiled.Count} assemblies compiled" +
            (report.Failed.Count == 0 ? "" : $", {report.Failed.Count} kept as IL ({string.Join(", ", report.Failed)})") + $" ({cache}).");
        return
        [
            .. Directory.EnumerateFiles(native, "*", SearchOption.AllDirectories)
                .Select(file => (Path: Path.GetRelativePath(native, file).Replace('\\', '/'), Source: file))
                .Where(file => file.Path != NativeCode.MARKER).OrderBy(file => file.Path, StringComparer.Ordinal),
            (NativeCode.MARKER, Path.Combine(native, NativeCode.MARKER))
        ];
    }

    // Runs the tree's crossgen2 with a response file; a failure's output goes to the console.
    private async Task<int> RunAsync(string response, CancellationToken cancellationToken)
    {
        var result = await Processes.RunAsync(m_crossgen2, ["@" + response], Path.GetDirectoryName(response)!, CROSSGEN2_TIMEOUT_SECONDS);
        if (result.TimedOut || result.ExitCode != 0)
        {
            Console.WriteLine($"crossgen2 failed (exit {result.ExitCode}, timeout={result.TimedOut}):");
            foreach (var line in (result.Output + "\n" + result.Error).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(TAIL_LINES))
                Console.WriteLine("  " + line.TrimEnd());
        }
        return result.TimedOut ? -1 : result.ExitCode;
    }

    // The hash of what a compilation reads: the compiler, the rules (WitOS.Apps' module), the framework's files and the
    // version's files, each by its name and its bytes' SHA-256.
    private async Task<string> KeyAsync(IReadOnlyList<(string Path, string Source)> files)
    {
        var text = new StringBuilder();
        text.Append("crossgen2 ").Append(await HashFileAsync(m_crossgen2)).Append('\n');
        text.Append("rules ").Append(typeof(NativeCode).Assembly.ManifestModule.ModuleVersionId).Append('\n');
        text.Append("architecture ").Append(Architecture.Name).Append('\n');
        foreach (var file in Directory.EnumerateFiles(m_framework).Order(StringComparer.Ordinal))
            text.Append("framework ").Append(Path.GetFileName(file)).Append(' ').Append(await HashFileAsync(file)).Append('\n');
        foreach (var (path, source) in files.OrderBy(file => file.Path, StringComparer.Ordinal))
            text.Append("file ").Append(path).Append(' ').Append(await HashFileAsync(source)).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16].ToLowerInvariant();
    }

    private static async Task<string> HashFileAsync(string file)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    #endregion

    #region Properties

    /// <summary>
    /// The architecture the builder compiles for.
    /// </summary>
    public KernelArchitecture Architecture { get; }

    #endregion
}
