using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.FileSystems.Ext4;

/// <summary>
/// An inode's record in the transaction's copy of its inode table block (D4.3): the fields a writable mount changes,
/// and the seal that sets the record's checksum again. A new record starts empty with extents, the extra size the
/// superblock asks, the four times and a fresh generation.
/// </summary>
internal sealed class Ext4InodeRecord
{
    #region Constants

    /// <summary>i_flags' EXTENTS_FL.</summary>
    public const uint FLAG_EXTENTS = 0x80000;

    private const uint FLAG_HUGE_FILE = 0x40000;
    private const int MODE = 0x00;
    private const int SIZE_LO = 0x04;
    private const int ACCESSED = 0x08;
    private const int CHANGED = 0x0C;
    private const int MODIFIED = 0x10;
    private const int DELETED = 0x14;
    private const int LINKS = 0x1A;
    private const int BLOCKS_LO = 0x1C;
    private const int FLAGS = 0x20;
    private const int BLOCK = 0x28;
    private const int GENERATION = 0x64;
    private const int SIZE_HI = 0x6C;
    private const int BLOCKS_HI = 0x74;
    private const int CHECKSUM_LO = 0x7C;
    private const int EXTRA_SIZE = 0x80;
    private const int CHECKSUM_HI = 0x82;
    private const int CHANGED_EXTRA = 0x84;
    private const int MODIFIED_EXTRA = 0x88;
    private const int ACCESSED_EXTRA = 0x8C;
    private const int CREATED = 0x90;
    private const int CREATED_EXTRA = 0x94;
    private const int OLD_SIZE = 128;

    #endregion

    #region Fields

    private readonly Ext4Superblock m_superblock;
    private readonly byte[] m_block;
    private readonly int m_offset;

    #endregion

    #region Constructors

    private Ext4InodeRecord(Ext4Superblock superblock, uint number, byte[] block, int offset)
    {
        m_superblock = superblock;
        Number = number;
        m_block = block;
        m_offset = offset;
    }

    #endregion

    #region Functions

    /// <summary>
    /// The record of an existing inode, in the transaction.
    /// </summary>
    /// <param name="volume">The volume.</param>
    /// <param name="transaction">The change.</param>
    /// <param name="number">The inode's number.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The record.</returns>
    public static async ValueTask<Ext4InodeRecord> OpenAsync(Ext4Volume volume, Ext4Transaction transaction, uint number,
        CancellationToken cancellationToken)
    {
        var (block, offset) = volume.InodeLocation(number);
        var bytes = await transaction.GetAsync(volume, block, cancellationToken).ConfigureAwait(false);
        Ext4Inode.Read(number, bytes.AsSpan(offset, volume.Superblock.InodeSize), volume.Superblock); // checked before it changes
        return new Ext4InodeRecord(volume.Superblock, number, bytes, offset);
    }

    /// <summary>
    /// A new record for a free inode: empty, mapped by extents, with its links, its mode and the time.
    /// </summary>
    /// <param name="volume">The volume.</param>
    /// <param name="transaction">The change.</param>
    /// <param name="number">The inode's number, just taken.</param>
    /// <param name="mode">Its type and permissions.</param>
    /// <param name="links">Its links.</param>
    /// <param name="now">The time.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The record.</returns>
    public static async ValueTask<Ext4InodeRecord> CreateAsync(Ext4Volume volume, Ext4Transaction transaction, uint number, int mode, int links,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (block, offset) = volume.InodeLocation(number);
        var bytes = await transaction.GetAsync(volume, block, cancellationToken).ConfigureAwait(false);
        var superblock = volume.Superblock;
        bytes.AsSpan(offset, superblock.InodeSize).Clear();
        var record = new Ext4InodeRecord(superblock, number, bytes, offset);
        var span = record.Bytes;
        BinaryPrimitives.WriteUInt16LittleEndian(span[MODE..], (ushort)mode);
        BinaryPrimitives.WriteUInt16LittleEndian(span[LINKS..], (ushort)links);
        if (superblock.InodeSize > OLD_SIZE)
            BinaryPrimitives.WriteUInt16LittleEndian(span[EXTRA_SIZE..], (ushort)Math.Min(superblock.WantExtraSize == 0 ? 32 : superblock.WantExtraSize,
                superblock.InodeSize - OLD_SIZE));
        BinaryPrimitives.WriteUInt32LittleEndian(span[GENERATION..], (uint)Random.Shared.Next());
        record.Flags = FLAG_EXTENTS;
        Ext4ExtentWriter.WriteEmptyRoot(record.Block);
        record.Touch(now, changed: true, modified: true, accessed: true);
        record.SetTime(CREATED, CREATED_EXTRA, now);
        return record;
    }

    /// <summary>
    /// The inode as the reader sees it.
    /// </summary>
    /// <returns>The inode, its checksum set first.</returns>
    public Ext4Inode Read()
    {
        Seal();
        return Ext4Inode.Read(Number, Bytes, m_superblock);
    }

    /// <summary>
    /// The seed of the checksums of the inode's blocks: the volume's, the number and the generation.
    /// </summary>
    /// <returns>The seed.</returns>
    public uint ChecksumSeed() =>
        Ext4Checksums.Crc32C(Ext4Checksums.Crc32C(m_superblock.ChecksumSeed, Number), BinaryPrimitives.ReadUInt32LittleEndian(Bytes[GENERATION..]));

    /// <summary>
    /// Sets the times a change touches.
    /// </summary>
    /// <param name="now">The time.</param>
    /// <param name="changed">The inode changed (ctime).</param>
    /// <param name="modified">The data changed (mtime).</param>
    /// <param name="accessed">The data were read (atime).</param>
    public void Touch(DateTimeOffset now, bool changed, bool modified, bool accessed = false)
    {
        if (changed)
            SetTime(CHANGED, CHANGED_EXTRA, now);
        if (modified)
            SetTime(MODIFIED, MODIFIED_EXTRA, now);
        if (accessed)
            SetTime(ACCESSED, ACCESSED_EXTRA, now);
    }

    /// <summary>
    /// Marks the inode deleted at a time: no links, no size, no blocks.
    /// </summary>
    /// <param name="now">The time.</param>
    public void Delete(DateTimeOffset now)
    {
        Links = 0;
        Size = 0;
        SectorCount = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes[DELETED..], (uint)now.ToUnixTimeSeconds());
        Touch(now, changed: true, modified: true);
    }

    /// <summary>
    /// Sets the record's checksum when metadata_csum is on; its high half where the extra size covers it.
    /// </summary>
    public void Seal()
    {
        if (!m_superblock.HasMetadataChecksums)
            return;
        var span = Bytes;
        var extra = span.Length > OLD_SIZE ? BinaryPrimitives.ReadUInt16LittleEndian(span[EXTRA_SIZE..]) : 0;
        var hasHigh = span.Length > OLD_SIZE && extra >= CHECKSUM_HI + 2 - OLD_SIZE;
        span.Slice(CHECKSUM_LO, 2).Clear();
        if (hasHigh)
            span.Slice(CHECKSUM_HI, 2).Clear();
        var crc = Ext4Checksums.Crc32C(Ext4Checksums.Crc32C(m_superblock.ChecksumSeed, Number), BinaryPrimitives.ReadUInt32LittleEndian(span[GENERATION..]));
        crc = Ext4Checksums.Crc32C(crc, span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[CHECKSUM_LO..], (ushort)crc);
        if (hasHigh)
            BinaryPrimitives.WriteUInt16LittleEndian(span[CHECKSUM_HI..], (ushort)(crc >> 16));
    }

    // A time's seconds as a signed 32-bit value, and in its extra field (where the extra size covers it) two epoch
    // bits and the nanoseconds above them.
    private void SetTime(int seconds, int extraOffset, DateTimeOffset time)
    {
        var span = Bytes;
        var extra = span.Length > OLD_SIZE ? BinaryPrimitives.ReadUInt16LittleEndian(span[EXTRA_SIZE..]) : 0;
        if (seconds >= OLD_SIZE && extra < seconds + 4 - OLD_SIZE)
            return; // the creation time lives past the first 128 bytes, where the record may not reach
        var total = time.ToUnixTimeSeconds();
        var low = unchecked((int)total);
        BinaryPrimitives.WriteInt32LittleEndian(span[seconds..], low);
        if (extra >= extraOffset + 4 - OLD_SIZE)
        {
            var epoch = (uint)((total - low) >> 32) & 3;
            var nanoseconds = (uint)((time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) % TimeSpan.TicksPerSecond * 100);
            BinaryPrimitives.WriteUInt32LittleEndian(span[extraOffset..], nanoseconds << 2 | epoch);
        }
    }

    #endregion

    #region Properties

    /// <summary>The inode's number.</summary>
    public uint Number { get; }

    /// <summary>The record's bytes in the transaction's copy.</summary>
    public Span<byte> Bytes => m_block.AsSpan(m_offset, m_superblock.InodeSize);

    /// <summary>i_block.</summary>
    public Span<byte> Block => Bytes.Slice(BLOCK, Ext4Inode.BLOCK_BYTES);

    /// <summary>i_mode.</summary>
    public int Mode => BinaryPrimitives.ReadUInt16LittleEndian(Bytes[MODE..]);

    /// <summary>The hard links.</summary>
    public int Links
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(Bytes[LINKS..]);
        set => BinaryPrimitives.WriteUInt16LittleEndian(Bytes[LINKS..], (ushort)value);
    }

    /// <summary>The bytes of the file, the directory or the link's target.</summary>
    public long Size
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Bytes[SIZE_LO..]) | (long)BinaryPrimitives.ReadUInt32LittleEndian(Bytes[SIZE_HI..]) << 32;
        set
        {
            BinaryPrimitives.WriteUInt32LittleEndian(Bytes[SIZE_LO..], (uint)value);
            BinaryPrimitives.WriteUInt32LittleEndian(Bytes[SIZE_HI..], (uint)(value >> 32));
        }
    }

    /// <summary>i_flags.</summary>
    public uint Flags
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Bytes[FLAGS..]);
        set => BinaryPrimitives.WriteUInt32LittleEndian(Bytes[FLAGS..], value);
    }

    /// <summary>The 512-byte sectors the inode holds (i_blocks without HUGE_FILE's scale, which a write clears).</summary>
    public ulong SectorCount
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Bytes[BLOCKS_LO..]) | (ulong)BinaryPrimitives.ReadUInt16LittleEndian(Bytes[BLOCKS_HI..]) << 32;
        set
        {
            BinaryPrimitives.WriteUInt32LittleEndian(Bytes[BLOCKS_LO..], (uint)value);
            BinaryPrimitives.WriteUInt16LittleEndian(Bytes[BLOCKS_HI..], (ushort)(value >> 32));
            Flags &= ~FLAG_HUGE_FILE;
        }
    }

    #endregion
}
