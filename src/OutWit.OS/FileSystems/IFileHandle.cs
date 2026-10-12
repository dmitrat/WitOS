using System;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.FileSystems;

/// <summary>
/// An open file (storage document §53, plan step D4.1): bytes addressed by offset, with no shared position (§8), as a
/// namespace service serves pread and pwrite.
/// </summary>
public interface IFileHandle : IAsyncDisposable
{
    #region Functions

    /// <summary>
    /// Reads bytes of the file.
    /// </summary>
    /// <param name="offset">The first byte.</param>
    /// <param name="buffer">What to fill.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes read: fewer than the buffer only at the end of the file.</returns>
    ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes bytes to the file, growing it when they go past its end; a gap reads as zeros.
    /// </summary>
    /// <param name="offset">The first byte.</param>
    /// <param name="buffer">The bytes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>When the bytes are the file's.</returns>
    ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cuts or grows the file; grown bytes read as zeros.
    /// </summary>
    /// <param name="length">The new length.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>When the file has the length.</returns>
    ValueTask SetLengthAsync(long length, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes what the handle holds back and flushes the filesystem.
    /// </summary>
    /// <param name="cancellationToken">Cancels the flush.</param>
    /// <returns>When the file is durable.</returns>
    ValueTask FlushAsync(CancellationToken cancellationToken = default);

    #endregion

    #region Properties

    /// <summary>
    /// The file's bytes.
    /// </summary>
    long Length { get; }

    /// <summary>
    /// Whether the handle reads.
    /// </summary>
    bool CanRead { get; }

    /// <summary>
    /// Whether the handle writes.
    /// </summary>
    bool CanWrite { get; }

    #endregion
}
