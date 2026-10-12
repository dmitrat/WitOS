using System;

namespace OutWit.OS.FileSystems;

/// <summary>
/// What a filesystem knows of a file or a directory (storage document §20).
/// </summary>
/// <param name="Path">Its absolute path in the volume.</param>
/// <param name="Name">Its name.</param>
/// <param name="IsDirectory">Whether it is a directory.</param>
/// <param name="Length">A file's bytes; zero for a directory.</param>
/// <param name="Attributes">Its attributes.</param>
/// <param name="Created">When it was created, when the format records it.</param>
/// <param name="Modified">When it was last written, when the format records it.</param>
/// <param name="Accessed">When it was last read, when the format records it.</param>
public sealed record FileSystemEntry(string Path, string Name, bool IsDirectory, long Length, FileSystemAttributes Attributes,
    DateTimeOffset? Created, DateTimeOffset? Modified, DateTimeOffset? Accessed);
