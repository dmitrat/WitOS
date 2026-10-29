namespace OutWit.OS.Storage;

/// <summary>
/// A partition of a master boot record (plan step E1): a primary entry, or a logical partition of the extended one, by
/// its range of the disk's logical blocks. A USB drive Windows formats carries one.
/// </summary>
/// <param name="Index">Its number as Linux names it: 1–4 for the primary entries, 5 and on for the logical partitions.</param>
/// <param name="Type">The entry's partition type byte, such as 0x0C (FAT32 with LBA), 0x07 (exFAT or NTFS) or 0x83 (Linux).</param>
/// <param name="FirstBlock">The first logical block.</param>
/// <param name="BlockCount">The blocks it spans.</param>
/// <param name="IsBootable">Whether the entry is marked active.</param>
public sealed record MbrPartition(int Index, byte Type, long FirstBlock, long BlockCount, bool IsBootable)
{
    #region Functions

    /// <summary>
    /// The partition as a block storage resource of its own: a window of the disk its holder has authority over alone.
    /// </summary>
    /// <param name="disk">The disk the table was read from, which the window does not own.</param>
    /// <param name="isReadOnly">Whether the window refuses writes the disk would take.</param>
    /// <returns>The partition's resource.</returns>
    public PartitionBlockStorage Open(IBlockStorage disk, bool isReadOnly = false)
    {
        var block = disk.Info.LogicalBlockSize;
        return new PartitionBlockStorage(disk, FirstBlock * block, BlockCount * block, isReadOnly);
    }

    #endregion
}
