using System;
using System.Collections.Generic;
using System.Text;
using OutWit.OS.Communication;
using OutWit.OS.Display;
using OutWit.OS.Hardware;
using OutWit.OS.Resources;

namespace OutWit.OS.DisplayService.Server;

/// <summary>
/// The display as a console of the serial protocol (witos/serial.h, plan step C2.2): a WRITE paints its bytes through the
/// terminal emulator and the renderer before it is answered, so that what a program wrote is on the screen when its
/// write returns; a READ takes the bytes the keys gave and the emulator's answers to the queries it serves, held until
/// there are any unless NONBLOCK; SIZE is the grid. Every request is checked against the window before anything is done.
/// </summary>
internal sealed class DisplayConsole : IDisposable
{
    #region Constants

    private const string PREFIX = "[DISPLAY] ";

    #endregion

    #region Fields

    private readonly TerminalScreen m_screen;
    private readonly FrameRenderer m_renderer;
    private readonly Queue<byte> m_input = new();
    private MemoryMapping? m_window;
    private SerialRequest? m_pendingRead;

    #endregion

    #region Constructors

    /// <summary>
    /// The console over a screen and its renderer.
    /// </summary>
    /// <param name="screen">The terminal emulator of the renderer's size.</param>
    /// <param name="renderer">The renderer into the framebuffer.</param>
    public DisplayConsole(TerminalScreen screen, FrameRenderer renderer)
    {
        m_screen = screen;
        m_renderer = renderer;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Serves one message of the console channel; the message's handles stay the caller's to close.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>The reply's bytes, or null for a READ held until a key comes.</returns>
    public byte[]? Serve(ChannelMessage message)
    {
        SerialRequest request;
        try
        {
            request = SerialMessages.DecodeRequest(message.Bytes);
        }
        catch (FormatException)
        {
            return SerialMessages.Encode(new SerialReply(0, ServiceStatus.PROTOCOL_ERROR));
        }
        var error = SerialMessages.Check(request, m_window?.Length ?? 0);
        if (error != 0)
            return SerialMessages.Encode(new SerialReply(request.Operation, error));
        switch (request.Operation)
        {
            case SerialMessages.ATTACH:
                return SerialMessages.Encode(message.Handles.Count == 1
                    ? Attach(request, message.Handles[0])
                    : new SerialReply(SerialMessages.ATTACH, SerialMessages.INVALID));
            case SerialMessages.SIZE_QUERY:
                return SerialMessages.Encode(new SerialReply(SerialMessages.SIZE_QUERY, 0, 0, m_screen.Columns, m_screen.Rows));
            case SerialMessages.WRITE:
                if (m_window is null)
                    return SerialMessages.Encode(new SerialReply(SerialMessages.WRITE, SerialMessages.INVALID));
                var answers = m_screen.Feed(m_window.Bytes.Slice((int)request.Window, (int)request.Length));
                if (answers.Length > 0)
                    Keys(Encoding.ASCII.GetBytes(answers));
                m_renderer.Render(m_screen);
                Written += request.Length;
                return SerialMessages.Encode(new SerialReply(SerialMessages.WRITE, 0, request.Length));
            default:
                if (m_window is null)
                    return SerialMessages.Encode(new SerialReply(SerialMessages.READ, SerialMessages.INVALID));
                if (m_pendingRead is not null)
                    return SerialMessages.Encode(new SerialReply(SerialMessages.READ, SerialMessages.BUSY));
                if (m_input.Count > 0)
                    return SerialMessages.Encode(Read(request));
                if ((request.Flags & SerialMessages.NONBLOCK) != 0)
                    return SerialMessages.Encode(new SerialReply(SerialMessages.READ, 0, 0));
                m_pendingRead = request;
                return null;
        }
    }

    /// <summary>
    /// Bytes the keys gave: queued for the reads.
    /// </summary>
    /// <param name="bytes">The bytes.</param>
    public void Keys(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
            m_input.Enqueue(b);
    }

    /// <summary>
    /// The reply of the READ held, once bytes are there.
    /// </summary>
    /// <returns>The reply's bytes, or null while no READ waits or nothing came.</returns>
    public byte[]? Completed()
    {
        if (m_pendingRead is not { } request || m_input.Count == 0)
            return null;
        m_pendingRead = null;
        return SerialMessages.Encode(Read(request));
    }

    /// <summary>
    /// Ends the window's mapping and forgets the READ held: the client is gone.
    /// </summary>
    public void Detach()
    {
        m_window?.Dispose();
        m_window = null;
        m_pendingRead = null;
    }

    // The window, mapped in place of the one before; the mapping holds the object, so its handle closes with the message.
    private SerialReply Attach(SerialRequest request, KernelHandle window)
    {
        MemoryMapping mapping;
        try
        {
            mapping = MemoryMapping.Map(window, 0, (int)request.Length, MemoryMapping.READ_WRITE);
        }
        catch (CapabilityException exception)
        {
            Console.WriteLine($"{PREFIX}the console's window could not be mapped: {exception.Message}");
            return new SerialReply(SerialMessages.ATTACH, exception.Error);
        }
        Detach();
        m_window = mapping;
        return new SerialReply(SerialMessages.ATTACH, 0, request.Length);
    }

    private SerialReply Read(SerialRequest request)
    {
        var count = (int)Math.Min(request.Length, m_input.Count);
        var target = m_window!.Writable.Slice((int)request.Window, count);
        for (var i = 0; i < count; i++)
            target[i] = m_input.Dequeue();
        return new SerialReply(SerialMessages.READ, 0, count);
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        Detach();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The bytes written to the screen so far.
    /// </summary>
    public long Written { get; private set; }

    #endregion
}
