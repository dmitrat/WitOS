namespace WitOS.Dev.Runtime;

/// <summary>
/// Where the layer of base applications lies (ADR 0027, plan step C4.1): a directory of the host that a new data disk is
/// made from, or the ext4 volume of an existing data disk. Paths are relative to the volume's root, with '/'.
/// </summary>
internal interface ILayerStore
{
    /// <summary>
    /// Whether a file or a directory is there.
    /// </summary>
    /// <param name="path">The path.</param>
    Task<bool> ExistsAsync(string path);

    /// <summary>
    /// A file's text, or null when there is none.
    /// </summary>
    /// <param name="path">The path.</param>
    Task<string?> ReadTextAsync(string path);

    /// <summary>
    /// The names in a directory, none when there is no directory.
    /// </summary>
    /// <param name="directory">The directory's path.</param>
    Task<IReadOnlyList<string>> ListAsync(string directory);

    /// <summary>
    /// Writes a file from a file of the host, replacing one that is there, executable when the source is; the
    /// directories above it are made.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="source">The host's file.</param>
    Task WriteFileAsync(string path, string source);

    /// <summary>
    /// Writes a file's text, replacing one that is there; the directories above it are made.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="text">The text.</param>
    Task WriteTextAsync(string path, string text);

    /// <summary>
    /// Makes every write so far reach the store.
    /// </summary>
    Task FlushAsync();
}
