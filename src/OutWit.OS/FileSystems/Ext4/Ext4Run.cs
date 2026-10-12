namespace OutWit.OS.FileSystems.Ext4;

/// <summary>
/// A run of an inode's data: logical blocks on contiguous physical blocks.
/// </summary>
/// <param name="Logical">The first logical block.</param>
/// <param name="Physical">The first physical block.</param>
/// <param name="Length">The blocks.</param>
/// <param name="Initialized">Whether the blocks hold data; an uninitialized extent reads as zeros.</param>
internal readonly record struct Ext4Run(ulong Logical, ulong Physical, uint Length, bool Initialized);
