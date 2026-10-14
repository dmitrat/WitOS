using System;
using System.Buffers.Binary;
using System.Text;

namespace OutWit.OS.Namespaces;

/// <summary>
/// The namespace service's messages (witos/namespace.h, plan step D5.1): requests, replies with their stat, the
/// entries of a listing in the window, and the disks the device manager moves to the service.
/// </summary>
public static class NamespaceMessages
{
    #region Constants

    /// <summary>WIT_NAMESPACE_VERSION.</summary>
    public const uint VERSION = 1;

    /// <summary>The bytes of a request.</summary>
    public const int REQUEST_SIZE = 48;

    /// <summary>The bytes of a reply.</summary>
    public const int REPLY_SIZE = 136;

    /// <summary>The bytes of a disk message.</summary>
    public const int DISK_SIZE = 80;

    /// <summary>The bytes of an entry's header in a listing.</summary>
    public const int ENTRY_SIZE = 16;

    /// <summary>The window a client attaches.</summary>
    public const int WINDOW_BYTES = 16 * 4096;

    /// <summary>The session's end and the window, on the clients' endpoint.</summary>
    public const uint CONNECT = 1;

    /// <summary>The mount points into the window.</summary>
    public const uint MOUNTS = 2;

    /// <summary>A file or a directory opened.</summary>
    public const uint OPEN = 3;

    /// <summary>A handle closed.</summary>
    public const uint CLOSE = 4;

    /// <summary>Bytes of a file into the window.</summary>
    public const uint READ = 5;

    /// <summary>Bytes of the window to a file.</summary>
    public const uint WRITE = 6;

    /// <summary>A path's stat.</summary>
    public const uint STAT = 7;

    /// <summary>A handle's stat.</summary>
    public const uint FSTAT = 8;

    /// <summary>A directory's entries into the window.</summary>
    public const uint LIST = 9;

    /// <summary>A directory made.</summary>
    public const uint MKDIR = 10;

    /// <summary>A file or a directory removed.</summary>
    public const uint REMOVE = 11;

    /// <summary>An entry renamed.</summary>
    public const uint RENAME = 12;

    /// <summary>A file cut or grown.</summary>
    public const uint TRUNCATE = 13;

    /// <summary>A file made durable.</summary>
    public const uint SYNC = 14;

    /// <summary>A link's target into the window.</summary>
    public const uint READLINK = 15;

    /// <summary>
    /// WIT_NAMESPACE_MAP: a chunk of a file as a memory object the service shares (R4.2).
    /// </summary>
    public const uint MAP = 16;

    /// <summary>A path's permission bits set to Mode (N4.1); the reply's Stat.</summary>
    public const uint CHMOD = 17;

    /// <summary>A handle's permission bits set to Mode (N4.1); the reply's Stat.</summary>
    public const uint FCHMOD = 18;

    /// <summary>A path's times set from the times at Offset of the window as the flags say (N4.1); the reply's Stat.</summary>
    public const uint UTIMES = 19;

    /// <summary>A handle's times set from the times at Offset of the window as the flags say (N4.1); the reply's Stat.</summary>
    public const uint FUTIMES = 20;

    /// <summary>UTIMES, FUTIMES: the access time is the given one.</summary>
    public const uint TIME_ACCESSED = 0x100;

    /// <summary>UTIMES, FUTIMES: the access time is the service's now.</summary>
    public const uint TIME_ACCESSED_NOW = 0x200;

    /// <summary>UTIMES, FUTIMES: the modification time is the given one.</summary>
    public const uint TIME_MODIFIED = 0x400;

    /// <summary>UTIMES, FUTIMES: the modification time is the service's now.</summary>
    public const uint TIME_MODIFIED_NOW = 0x800;

    /// <summary>The bytes of the times of UTIMES and FUTIMES in the window (WitNamespaceTimes).</summary>
    public const int TIMES_SIZE = 24;

    /// <summary>OPEN: the handle reads.</summary>
    public const uint READ_ACCESS = 0x1;

    /// <summary>OPEN: the handle writes.</summary>
    public const uint WRITE_ACCESS = 0x2;

    /// <summary>OPEN: a missing file is created.</summary>
    public const uint CREATE = 0x4;

    /// <summary>OPEN with CREATE: the file must not exist.</summary>
    public const uint EXCLUSIVE = 0x8;

    /// <summary>OPEN: the file is cut to nothing.</summary>
    public const uint TRUNCATE_FILE = 0x10;

    /// <summary>OPEN: the path must be a directory; REMOVE: a directory is removed.</summary>
    public const uint DIRECTORY = 0x20;

    /// <summary>
    /// WIT_NAMESPACE_MAP_WRITE: MAP's object writable.
    /// </summary>
    public const uint MAP_WRITE = 0x40;

    /// <summary>
    /// WIT_NAMESPACE_CHUNK_BYTES: the bytes of one shared chunk of a file, 64 pages.
    /// </summary>
    public const int CHUNK_BYTES = 64 * 4096;

    /// <summary>A file.</summary>
    public const uint KIND_FILE = 1;

    /// <summary>A directory.</summary>
    public const uint KIND_DIRECTORY = 2;

    /// <summary>A symbolic link.</summary>
    public const uint KIND_LINK = 3;

    /// <summary>Another kind.</summary>
    public const uint KIND_OTHER = 4;

    /// <summary>A disk message's flag: the disk refuses writes.</summary>
    public const uint DISK_READ_ONLY = 0x1;

    /// <summary>A disk message's flag: the device manager moved every disk.</summary>
    public const uint DISK_END = 0x2;

    private const int DEVICE_BYTES = 64;

    #endregion

    #region Functions

    /// <summary>The bytes of a request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>Its bytes.</returns>
    public static byte[] Encode(NamespaceRequest request)
    {
        var bytes = Header(REQUEST_SIZE, request.Operation, request.Flags);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), request.Handle);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), request.Offset);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), request.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), request.Mode);
        return bytes;
    }

    /// <summary>The request a message's bytes carry.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The request.</returns>
    /// <exception cref="FormatException">The bytes are no request of this version.</exception>
    public static NamespaceRequest DecodeRequest(ReadOnlySpan<byte> bytes)
    {
        Check(bytes, REQUEST_SIZE, "request");
        return new NamespaceRequest(BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[32..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[40..]));
    }

    /// <summary>The bytes of a reply.</summary>
    /// <param name="reply">The reply.</param>
    /// <returns>Its bytes.</returns>
    public static byte[] Encode(NamespaceReply reply)
    {
        var bytes = Header(REPLY_SIZE, reply.Operation, (uint)reply.Error);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), reply.Handle);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), reply.Offset);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), reply.Length);
        if (reply.Stat is { } stat)
            Write(bytes.AsSpan(40), stat);
        return bytes;
    }

    /// <summary>The reply a message's bytes carry.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The reply; its stat when the stat holds a kind.</returns>
    /// <exception cref="FormatException">The bytes are no reply of this version.</exception>
    public static NamespaceReply DecodeReply(ReadOnlySpan<byte> bytes)
    {
        Check(bytes, REPLY_SIZE, "reply");
        var stat = bytes[40..];
        return new NamespaceReply(BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[32..]), BinaryPrimitives.ReadUInt32LittleEndian(stat) == 0 ? null : Read(stat));
    }

    /// <summary>
    /// Writes a listing's entry into the window: its header, its name, and padding to 8 bytes.
    /// </summary>
    /// <param name="window">Where the entry goes.</param>
    /// <param name="id">The entry's number.</param>
    /// <param name="kind">Its kind.</param>
    /// <param name="name">Its name.</param>
    /// <returns>The entry's bytes, or zero when the window has no room for it.</returns>
    public static int WriteEntry(Span<byte> window, long id, uint kind, string name)
    {
        var nameBytes = Encoding.UTF8.GetByteCount(name);
        var bytes = (ENTRY_SIZE + nameBytes + 7) & ~7;
        if (bytes > window.Length || nameBytes > ushort.MaxValue)
            return 0;
        window[..bytes].Clear();
        BinaryPrimitives.WriteInt64LittleEndian(window, id);
        BinaryPrimitives.WriteUInt32LittleEndian(window[8..], kind);
        BinaryPrimitives.WriteUInt16LittleEndian(window[12..], (ushort)bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(window[14..], (ushort)nameBytes);
        Encoding.UTF8.GetBytes(name, window[ENTRY_SIZE..]);
        return bytes;
    }

    /// <summary>The bytes of a disk message.</summary>
    /// <param name="flags">DISK_READ_ONLY, DISK_END.</param>
    /// <param name="device">The device's identity.</param>
    /// <returns>Its bytes.</returns>
    /// <summary>
    /// One time of a UTIMES or FUTIMES request as the window holds it (N4.1): the access time (0) or the modification time
    /// (1). A time the request's flags do not use is never read, so its bytes may hold anything.
    /// </summary>
    /// <param name="bytes">The WitNamespaceTimes.</param>
    /// <param name="index">0 for the access time, 1 for the modification time.</param>
    /// <returns>The time.</returns>
    /// <exception cref="FormatException">The bytes are too few.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The time lies outside what a DateTimeOffset holds.</exception>
    public static DateTimeOffset ReadTime(ReadOnlySpan<byte> bytes, int index)
    {
        if (bytes.Length < TIMES_SIZE)
            throw new FormatException($"Times of {bytes.Length} bytes.");
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, 1);
        return Time(BinaryPrimitives.ReadInt64LittleEndian(bytes[(index * 8)..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[(16 + index * 4)..]));
    }

    /// <summary>
    /// Writes the times of a UTIMES or FUTIMES request as the window holds them (N4.1).
    /// </summary>
    /// <param name="bytes">Where the WitNamespaceTimes goes.</param>
    /// <param name="accessed">The access time.</param>
    /// <param name="modified">The modification time.</param>
    public static void WriteTimes(Span<byte> bytes, DateTimeOffset accessed, DateTimeOffset modified)
    {
        DateTimeOffset[] times = [accessed, modified];
        for (var i = 0; i < 2; i++)
        {
            var ticks = times[i].UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
            var seconds = ticks / TimeSpan.TicksPerSecond - (ticks % TimeSpan.TicksPerSecond < 0 ? 1 : 0);
            BinaryPrimitives.WriteInt64LittleEndian(bytes[(i * 8)..], seconds);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[(16 + i * 4)..], (uint)((ticks - seconds * TimeSpan.TicksPerSecond) * 100));
        }
    }

    private static DateTimeOffset Time(long seconds, uint nanoseconds) =>
        nanoseconds >= 1_000_000_000 || seconds < -62_135_596_800 || seconds > 253_402_300_799
            ? throw new ArgumentOutOfRangeException(nameof(seconds), $"The time {seconds}.{nanoseconds:D9} lies outside what a date holds.")
            : DateTimeOffset.UnixEpoch.AddTicks(seconds * TimeSpan.TicksPerSecond + nanoseconds / 100);

    public static byte[] EncodeDisk(uint flags, string device)
    {
        var bytes = Header(DISK_SIZE, flags, 0);
        var identity = Encoding.UTF8.GetBytes(device);
        identity.AsSpan(0, Math.Min(identity.Length, DEVICE_BYTES - 1)).CopyTo(bytes.AsSpan(16));
        return bytes;
    }

    /// <summary>The disk a message's bytes carry.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>Its flags and device.</returns>
    /// <exception cref="FormatException">The bytes are no disk message of this version.</exception>
    public static (uint Flags, string Device) DecodeDisk(ReadOnlySpan<byte> bytes)
    {
        Check(bytes, DISK_SIZE, "disk message");
        var identity = bytes.Slice(16, DEVICE_BYTES);
        var end = identity.IndexOf((byte)0);
        return (BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), Encoding.UTF8.GetString(end < 0 ? identity : identity[..end]));
    }

    private static byte[] Header(int size, uint first, uint second)
    {
        var bytes = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, VERSION);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), first);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), second);
        return bytes;
    }

    private static void Check(ReadOnlySpan<byte> bytes, int size, string what)
    {
        if (bytes.Length != size || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != VERSION || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != size)
            throw new FormatException($"A namespace {what} of {bytes.Length} bytes is not of version {VERSION}.");
    }

    private static void Write(Span<byte> bytes, NamespaceStat stat)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, stat.Kind);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], stat.Mode);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], stat.Size);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[16..], stat.Id);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[24..], stat.Links);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[28..], stat.Volume);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[32..], stat.UserId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[36..], stat.GroupId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[40..], stat.BlockSize);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[48..], stat.Blocks);
        DateTimeOffset[] times = [stat.Accessed, stat.Modified, stat.Changed];
        for (var i = 0; i < 3; i++)
        {
            var ticks = times[i].UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
            var seconds = ticks / TimeSpan.TicksPerSecond - (ticks % TimeSpan.TicksPerSecond < 0 ? 1 : 0); // floored
            BinaryPrimitives.WriteInt64LittleEndian(bytes[(56 + i * 8)..], seconds);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[(80 + i * 4)..], (uint)((ticks - seconds * TimeSpan.TicksPerSecond) * 100));
        }
    }

    private static NamespaceStat Read(ReadOnlySpan<byte> bytes) =>
        new(BinaryPrimitives.ReadUInt32LittleEndian(bytes), BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[32..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[36..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[40..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[48..]), Time(bytes, 0), Time(bytes, 1),
            Time(bytes, 2));

    private static DateTimeOffset Time(ReadOnlySpan<byte> bytes, int index) =>
        DateTimeOffset.UnixEpoch.AddTicks(BinaryPrimitives.ReadInt64LittleEndian(bytes[(56 + index * 8)..]) * TimeSpan.TicksPerSecond +
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[(80 + index * 4)..]) / 100);

    #endregion
}
