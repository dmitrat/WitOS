using System.Text;
using WitOS.Dev.Host;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The layer on the ext4 volume of an existing data disk (plan step C4.1), through the host's debugfs: writes are queued
/// and run as one debugfs script when the store is flushed or read, which keeps the volume's own bitmaps and counts;
/// a file is written from its directory, since debugfs links a new file by its name into the directory it stands in.
/// Needs e2fsprogs on a Linux host, as the volume's making does.
/// </summary>
internal sealed class Ext4LayerStore : ILayerStore
{
    #region Constants

    private const int TIMEOUT_SECONDS = 600;

    // The errors a script expects: a removal of a file that is not there, a directory that is.
    private static readonly string[] EXPECTED_ERRORS = ["rm: ", "mkdir: ", "ext2fs_mkdir: Ext2 directory already exists"];

    #endregion

    #region Fields

    private readonly string m_debugfs;
    private readonly string m_image;
    private readonly string m_scratch;
    private readonly List<string> m_script = [];
    private readonly HashSet<string> m_directories = new(StringComparer.Ordinal);
    private int m_texts;

    #endregion

    #region Constructors

    /// <summary>
    /// A store over a volume's image.
    /// </summary>
    /// <param name="image">The ext4 volume's image, not a whole disk.</param>
    /// <param name="scratch">A directory for the script and the texts written.</param>
    /// <exception cref="InvalidOperationException">The host has no e2fsprogs.</exception>
    public Ext4LayerStore(string image, string scratch)
    {
        var mkfs = RuntimeExt4Volume.FindMkfs() ?? throw new InvalidOperationException("Updating a data disk's layer needs e2fsprogs (debugfs) on a Linux host.");
        m_debugfs = Path.Combine(Path.GetDirectoryName(mkfs)!, "debugfs");
        m_image = image;
        m_scratch = scratch;
        Directory.CreateDirectory(scratch);
    }

    #endregion

    #region ILayerStore

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string path)
    {
        await FlushAsync();
        var result = await DebugfsAsync(["-R", $"stat {Quote("/" + path)}", m_image]);
        return result.Output.Contains("Inode:", StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public async Task<string?> ReadTextAsync(string path)
    {
        await FlushAsync();
        var output = Path.Combine(m_scratch, "read.txt");
        File.Delete(output);
        await DebugfsAsync(["-R", $"dump {Quote("/" + path)} {Quote(output)}", m_image]);
        if (!File.Exists(output) || !await ExistsAsync(path))
            return null;
        return await File.ReadAllTextAsync(output);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListAsync(string directory)
    {
        await FlushAsync();
        var result = await DebugfsAsync(["-R", $"ls -p {Quote("/" + directory)}", m_image]);
        // ls -p prints /inode/mode/uid/gid/name/size/ for each entry.
        return [.. result.Output.Split('\n').Select(line => line.Trim().Split('/')).Where(fields => fields.Length >= 7 && fields[1].Length > 0)
            .Select(fields => fields[5]).Where(name => name is not ("." or "..")).Order(StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public Task WriteFileAsync(string path, string source)
    {
        var directory = DirectoryOf(path);
        MakeDirectories(directory);
        m_script.Add($"cd {Quote("/" + directory)}");
        m_script.Add($"rm {Quote(NameOf(path))}");
        m_script.Add($"write {Quote(Path.GetFullPath(source))} {Quote(NameOf(path))}");
        m_script.Add("cd /");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task WriteTextAsync(string path, string text)
    {
        var source = Path.Combine(m_scratch, $"text-{m_texts++}");
        await File.WriteAllTextAsync(source, text);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(source, (UnixFileMode)Convert.ToInt32("644", 8));
        await WriteFileAsync(path, source);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">debugfs reported an error the script did not expect.</exception>
    public async Task FlushAsync()
    {
        if (m_script.Count == 0)
            return;
        var script = Path.Combine(m_scratch, "layer.debugfs");
        await File.WriteAllTextAsync(script, string.Join('\n', m_script) + "\n");
        m_script.Clear();
        var result = await DebugfsAsync(["-w", "-f", script, m_image]);
        // debugfs echoes the script to its output and reports a command's failure on its error stream as "command: why",
        // after its banner.
        var errors = result.Error.Split('\n').Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("debugfs ", StringComparison.Ordinal))
            .Where(line => !EXPECTED_ERRORS.Any(expected => line.StartsWith(expected, StringComparison.Ordinal)))
            .ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException($"debugfs failed to write the layer: {string.Join("; ", errors)}");
    }

    #endregion

    #region Functions

    private void MakeDirectories(string directory)
    {
        var path = new StringBuilder();
        foreach (var part in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path.Append('/').Append(part);
            if (m_directories.Add(path.ToString()))
                m_script.Add($"mkdir {Quote(path.ToString())}");
        }
    }

    private async Task<ProcessResult> DebugfsAsync(string[] arguments)
    {
        var result = await Processes.RunAsync(m_debugfs, arguments, m_scratch, TIMEOUT_SECONDS);
        if (result.TimedOut || result.ExitCode != 0)
            throw new InvalidOperationException($"debugfs {string.Join(' ', arguments)} exited with {result.ExitCode}: {result.Error}");
        return result;
    }

    private static string DirectoryOf(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    // debugfs's own parser takes a double-quoted argument whole; a path with a quote in it has no place in the layer.
    private static string Quote(string path) => path.Contains('"')
        ? throw new InvalidDataException($"The layer holds no path with a quote: {path}")
        : $"\"{path}\"";

    #endregion
}
