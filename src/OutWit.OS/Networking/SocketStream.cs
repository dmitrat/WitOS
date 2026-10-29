using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OutWit.OS.Communication;
using OutWit.OS.Resources;

namespace OutWit.OS.Networking;

/// <summary>
/// A TCP connection of the network service as a <see cref="Stream"/> (plan step E2.2): a session of its own with the
/// service, one stream socket connected to the peer, reads and writes that wait on the session's readiness events when
/// the service answers EAGAIN, each wait bounded. What a protocol client over a byte stream — the SMB client of the
/// namespace service — needs from a service that holds no libc sockets.
/// </summary>
public sealed class SocketStream : Stream
{
    #region Fields

    private readonly SocketClient m_client;
    private readonly uint m_socket;
    private readonly int m_timeoutSeconds;
    private int m_disposed;

    #endregion

    #region Constructors

    private SocketStream(SocketClient client, uint socket, int timeoutSeconds)
    {
        m_client = client;
        m_socket = socket;
        m_timeoutSeconds = timeoutSeconds;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Connects to a peer, as a blocking connect does: the connect asked, its writability or its error awaited.
    /// </summary>
    /// <param name="clients">The network service's clients' endpoint; it stays the caller's.</param>
    /// <param name="peer">The peer's address and port.</param>
    /// <param name="timeoutSeconds">How long the connect and each later read or write may wait, in the guest's time.</param>
    /// <returns>The stream.</returns>
    /// <exception cref="SocketStreamException">The service refused the socket, or the connect failed or timed out.</exception>
    public static SocketStream Connect(KernelHandle clients, SocketAddress peer, int timeoutSeconds)
    {
        var client = SocketClient.Connect(clients);
        try
        {
            Check(client.Open(SocketMessages.STREAM, out var socket), "open a stream socket");
            var error = client.ConnectTo(socket, peer);
            if (error is not 0 and not NetworkErrors.IN_PROGRESS)
                throw new SocketStreamException(error, "connect");
            var deadline = Clock.MonotonicAfter(timeoutSeconds);
            while ((client.Poll(socket) & (NetworkStack.WRITABLE | NetworkStack.ERROR | NetworkStack.HANGUP)) == 0)
            {
                if (client.WaitEvent(deadline) is null)
                    throw new SocketStreamException(NetworkErrors.TIMED_OUT, "connect");
            }
            Check(client.GetOption(socket, SocketMessages.SOCKET_LEVEL, SocketMessages.ERROR, out var pending), "read the connect's outcome");
            if (pending != 0)
                throw new SocketStreamException((int)pending, "connect");
            return new SocketStream(client, socket, timeoutSeconds);
        }
        catch
        {
            client.Dispose(); // its sockets close with the session
            throw;
        }
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(m_disposed != 0, this);
        if (buffer.IsEmpty)
            return 0;
        var deadline = Clock.MonotonicAfter(m_timeoutSeconds);
        for (; ; )
        {
            var error = m_client.Receive(m_socket, buffer[..Math.Min(buffer.Length, SocketClient.WINDOW_BYTES)], out var received, out _);
            if (error == 0)
                return received; // zero at the end of the stream
            if (error != NetworkErrors.AGAIN)
                throw new SocketStreamException(error, "receive");
            if (m_client.WaitEvent(deadline) is null)
                throw new SocketStreamException(NetworkErrors.TIMED_OUT, "receive");
        }
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(m_disposed != 0, this);
        var deadline = Clock.MonotonicAfter(m_timeoutSeconds);
        while (!buffer.IsEmpty)
        {
            var error = m_client.Send(m_socket, buffer[..Math.Min(buffer.Length, SocketClient.WINDOW_BYTES)], SocketAddress.None, out var sent);
            if (error == 0)
            {
                buffer = buffer[sent..];
                deadline = Clock.MonotonicAfter(m_timeoutSeconds); // the peer takes bytes: the wait starts again
                continue;
            }
            if (error != NetworkErrors.AGAIN)
                throw new SocketStreamException(error, "send");
            if (m_client.WaitEvent(deadline) is null)
                throw new SocketStreamException(NetworkErrors.TIMED_OUT, "send");
        }
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    // The service's session is a channel the caller waits on: the asynchronous forms complete the synchronous ones.
    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref m_disposed, 1) == 0)
        {
            m_client.Close(m_socket);
            m_client.Dispose();
        }
        base.Dispose(disposing);
    }

    private static void Check(int error, string what)
    {
        if (error != 0)
            throw new SocketStreamException(error, what);
    }

    #endregion

    #region Properties

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    #endregion
}

/// <summary>
/// A failure of a <see cref="SocketStream"/>, with the errno value the network service answered.
/// </summary>
/// <param name="error">The errno value: ECONNREFUSED, ETIMEDOUT, EHOSTUNREACH…</param>
/// <param name="operation">What failed.</param>
public sealed class SocketStreamException(int error, string operation) : IOException($"The connection could not {operation}: errno {error}")
{
    #region Properties

    /// <summary>
    /// The errno value.
    /// </summary>
    public int Error { get; } = error;

    #endregion
}
