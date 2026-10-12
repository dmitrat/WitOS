using System;

namespace OutWit.OS.FileSystems;

/// <summary>
/// What a filesystem knows of an entry (storage document §20).
/// </summary>
/// <param name="Path">Its absolute path in the volume.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind">A file, a directory, a symbolic link or another kind the format records.</param>
/// <param name="Length">A file's or a link's bytes; zero for a directory.</param>
/// <param name="Attributes">Its attributes.</param>
/// <param name="Created">When it was created, when the format records it.</param>
/// <param name="Modified">When it was last written, when the format records it.</param>
/// <param name="Accessed">When it was last read, when the format records it.</param>
/// <param name="Unix">Its owner, permissions and links, when the format records them.</param>
public sealed record FileSystemEntry(string Path, string Name, FileSystemEntryKind Kind, long Length, FileSystemAttributes Attributes,
    DateTimeOffset? Created, DateTimeOffset? Modified, DateTimeOffset? Accessed, FileSystemUnixMetadata? Unix = null)
{
    #region Properties

    /// <summary>
    /// Whether it is a directory.
    /// </summary>
    public bool IsDirectory => Kind == FileSystemEntryKind.Directory;

    #endregion
}
