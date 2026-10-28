namespace OutWit.OS.FileSystems;

/// <summary>
/// What a mounted volume is.
/// </summary>
/// <param name="Format">The on-disk format, such as "FAT16", "FAT32" or "exFAT".</param>
/// <param name="Label">The volume's label, or null.</param>
/// <param name="Length">The bytes the volume spans.</param>
/// <param name="AllocationUnit">The bytes it allocates at a time.</param>
/// <param name="IsReadOnly">Whether every change is refused.</param>
/// <param name="Identity">The volume's own identity as its format records it (ADR 0032 decision 1): an ext4 volume's UUID,
/// a FAT or exFAT volume's serial number; null where the format keeps none.</param>
public sealed record FileSystemVolume(string Format, string? Label, long Length, int AllocationUnit, bool IsReadOnly, string? Identity = null);
