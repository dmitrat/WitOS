namespace OutWit.OS.FileSystems;

/// <summary>
/// What a mounted volume is.
/// </summary>
/// <param name="Format">The on-disk format, such as "FAT16", "FAT32" or "exFAT".</param>
/// <param name="Label">The volume's label, or null.</param>
/// <param name="Length">The bytes the volume spans.</param>
/// <param name="AllocationUnit">The bytes it allocates at a time.</param>
/// <param name="IsReadOnly">Whether every change is refused.</param>
public sealed record FileSystemVolume(string Format, string? Label, long Length, int AllocationUnit, bool IsReadOnly);
