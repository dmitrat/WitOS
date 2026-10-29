using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.FileSystems.Smb;

/// <summary>
/// An open file of a share: READ and WRITE by offset in pieces the server takes (its largest read or write, at most a
/// MiB, charged a credit per 64 KiB), SET_INFO for its length and FLUSH; its length is the server's at the open, then
/// what its own writes and cuts make it.
/// </summary>
internal sealed class SmbFileHandle : IFileHandle
{
    #region Constants

    private const int LARGEST_PIECE = 1 << 20;

    #endregion

    #region Fields

    private readonly SmbFileSystem m_volume;
    private readonly byte[] m_fileId;
    private long m_length;
    private int m_disposed;

    #endregion

    #region Constructors

    /// <summary>
    /// A handle CREATE opened.
    /// </summary>
    /// <param name="volume">Its share.</param>
    /// <param name="fileId">Its file id.</param>
    /// <param name="length">The file's length at the open.</param>
    /// <param name="canRead">Whether it reads.</param>
    /// <param name="canWrite">Whether it writes.</param>
    public SmbFileHandle(SmbFileSystem volume, byte[] fileId, long length, bool canRead, bool canWrite)
    {
        m_volume = volume;
        m_fileId = fileId;
        m_length = length;
        CanRead = canRead;
        CanWrite = canWrite;
    }

    #endregion

    #region Functions

    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!CanRead)
            throw new FileSystemException(FileSystemError.AccessDenied, "The handle was opened for writing alone.");
        var total = 0;
        while (total < buffer.Length)
        {
            var piece = Piece(buffer.Length - total, m_volume.Connection.MaxReadSize);
            var body = new byte[49];
            BinaryPrimitives.WriteUInt16LittleEndian(body, 49);
            body[2] = 0x50; // the data's offset in the response: after its header and its 16 fixed bytes
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (uint)piece);
            BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(8), offset + total);
            m_fileId.CopyTo(body, 16);
            var response = await m_volume.RequestAsync(SmbConnection.READ, body, m_volume.Connection.ChargeOf(piece), cancellationToken, SmbStatus.END_OF_FILE)
                .ConfigureAwait(false);
            if (response.Status == SmbStatus.END_OF_FILE)
                break;
            var data = response.Buffer(response.Body[2], (int)BinaryPrimitives.ReadUInt32LittleEndian(response.Body[4..]));
            if (data.Length > piece)
                throw new SmbException(0, $"The server answered a read of {piece} bytes with {data.Length}.");
            data.CopyTo(buffer.Span[total..]);
            total += data.Length;
            if (data.Length < piece)
                break; // the end of the file
        }
        return total;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!CanWrite)
            throw new FileSystemException(FileSystemError.AccessDenied, "The handle was opened for reading alone.");
        var total = 0;
        while (total < buffer.Length)
        {
            var piece = Piece(buffer.Length - total, m_volume.Connection.MaxWriteSize);
            var body = new byte[48 + piece];
            BinaryPrimitives.WriteUInt16LittleEndian(body, 49);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), SmbConnection.HEADER + 48);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (uint)piece);
            BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(8), offset + total);
            m_fileId.CopyTo(body, 16);
            buffer.Span.Slice(total, piece).CopyTo(body.AsSpan(48));
            var response = await m_volume.RequestAsync(SmbConnection.WRITE, body, m_volume.Connection.ChargeOf(piece), cancellationToken).ConfigureAwait(false);
            var written = (int)BinaryPrimitives.ReadUInt32LittleEndian(response.Body[4..]);
            if (written is <= 0 || written > piece)
                throw new SmbException(0, $"The server answered a write of {piece} bytes with {written}.");
            total += written;
        }
        m_length = Math.Max(m_length, offset + buffer.Length);
    }

    /// <inheritdoc />
    public async ValueTask SetLengthAsync(long length, CancellationToken cancellationToken = default)
    {
        if (!CanWrite)
            throw new FileSystemException(FileSystemError.AccessDenied, "The handle was opened for reading alone.");
        var information = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(information, length);
        await m_volume.SetInfoAsync(m_fileId, SmbFileSystem.CLASS_END_OF_FILE, information, cancellationToken).ConfigureAwait(false);
        m_length = length;
    }

    /// <inheritdoc />
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!CanWrite)
            return; // FLUSH needs write access, and a reading handle has nothing to write back
        var body = new byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 24);
        m_fileId.CopyTo(body, 8);
        await m_volume.RequestAsync(SmbConnection.FLUSH, body, 1, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref m_disposed, 1) != 0)
            return;
        await m_volume.CloseAsync(m_fileId, CancellationToken.None).ConfigureAwait(false);
    }

    // The bytes of the next piece: what is left, within the server's largest and a MiB, and within the credits it granted.
    private int Piece(int left, uint largest)
    {
        var piece = (int)Math.Min(Math.Min(left, LARGEST_PIECE), Math.Max(largest, 1));
        var credits = Math.Max(1, m_volume.Connection.Credits);
        return m_volume.Connection.ChargeOf(piece) > credits ? Math.Min(piece, credits * SmbConnection.CREDIT_BYTES) : piece;
    }

    #endregion

    #region Properties

    /// <inheritdoc />
    public long Length => m_length;

    /// <inheritdoc />
    public bool CanRead { get; }

    /// <inheritdoc />
    public bool CanWrite { get; }

    #endregion
}
