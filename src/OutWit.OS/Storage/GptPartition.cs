using System;

namespace OutWit.OS.Storage;

/// <summary>
/// A partition of a GUID partition table (UEFI 2.10, §5.3.3; storage document §24–25): its identity apart from its
/// place, and its range of the disk's blocks.
/// </summary>
/// <param name="Index">The entry's index in the table.</param>
/// <param name="Type">The partition type's GUID.</param>
/// <param name="Id">The partition's own GUID, its stable identity.</param>
/// <param name="FirstBlock">The first logical block.</param>
/// <param name="LastBlock">The last logical block, inclusive.</param>
/// <param name="Attributes">The entry's attribute bits.</param>
/// <param name="Name">The entry's name.</param>
public sealed record GptPartition(int Index, Guid Type, Guid Id, long FirstBlock, long LastBlock, ulong Attributes, string Name)
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
        return new PartitionBlockStorage(disk, FirstBlock * block, (LastBlock - FirstBlock + 1) * block, isReadOnly);
    }

    #endregion
}
