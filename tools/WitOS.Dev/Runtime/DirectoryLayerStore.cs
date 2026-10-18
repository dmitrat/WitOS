namespace WitOS.Dev.Runtime;

/// <summary>
/// The layer in a directory of the host (plan step C4.1): what a new data disk is made from, its files copied with their
/// modes, and what the host tests use.
/// </summary>
/// <param name="root">The directory that stands for the volume's root.</param>
internal sealed class DirectoryLayerStore(string root) : ILayerStore
{
    #region ILayerStore

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string path) => Task.FromResult(File.Exists(Full(path)) || Directory.Exists(Full(path)));

    /// <inheritdoc />
    public async Task<string?> ReadTextAsync(string path) => File.Exists(Full(path)) ? await File.ReadAllTextAsync(Full(path)) : null;

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListAsync(string directory)
    {
        var full = Full(directory);
        IReadOnlyList<string> names = Directory.Exists(full)
            ? [.. Directory.EnumerateFileSystemEntries(full).Select(entry => Path.GetFileName(entry)).Order(StringComparer.Ordinal)]
            : [];
        return Task.FromResult(names);
    }

    /// <inheritdoc />
    public Task WriteFileAsync(string path, string source)
    {
        var target = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true); // a copy keeps the source's mode on Unix
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task WriteTextAsync(string path, string text)
    {
        var target = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, text);
    }

    /// <inheritdoc />
    public Task FlushAsync() => Task.CompletedTask;

    #endregion

    #region Functions

    private string Full(string path) => Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

    #endregion
}
