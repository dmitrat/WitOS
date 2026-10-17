using System;
using System.Collections.Generic;
using OutWit.OS.Communication;
using OutWit.OS.Hardware;
using OutWit.OS.Resources;

namespace OutWit.OS.Drivers.VirtioInput.Virtio;

/// <summary>
/// The input protocol (witos/input.h, plan step C2.2) served over the device: a window attached, the events the device
/// reported queued — at most QUEUE, the oldest dropped beyond — and a READ answered from the queue or held until an
/// event comes; every request checked before anything is done.
/// </summary>
internal sealed class InputServer : IDisposable
{
    #region Fields

    private readonly Queue<InputEvent> m_events = new();
    private MemoryMapping? m_window;
    private InputRequest? m_pendingRead;

    #endregion

    #region Functions

    /// <summary>
    /// Queues the events the device reported.
    /// </summary>
    /// <param name="events">The events, in the device's order.</param>
    public void Report(IReadOnlyList<(ushort Type, ushort Code, int Value)> events)
    {
        foreach (var (type, code, value) in events)
        {
            if (m_events.Count >= InputMessages.QUEUE)
            {
                m_events.Dequeue();
                Dropped++;
            }
            m_events.Enqueue(new InputEvent(type, code, value));
        }
    }

    /// <summary>
    /// Serves one message of the input channel; the message's handles stay the caller's to close.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>The reply's bytes, or null for a READ held until an event comes.</returns>
    public byte[]? Serve(ChannelMessage message)
    {
        InputRequest request;
        try
        {
            request = InputMessages.DecodeRequest(message.Bytes);
        }
        catch (FormatException)
        {
            return InputMessages.Encode(new InputReply(0, ServiceStatus.PROTOCOL_ERROR));
        }
        var error = InputMessages.Check(request, m_window?.Length ?? 0);
        if (error != 0)
            return InputMessages.Encode(new InputReply(request.Operation, error));
        if (request.Operation == InputMessages.ATTACH)
            return InputMessages.Encode(message.Handles.Count == 1
                ? Attach(request, message.Handles[0])
                : new InputReply(InputMessages.ATTACH, InputMessages.INVALID));
        if (m_window is null)
            return InputMessages.Encode(new InputReply(InputMessages.READ, InputMessages.INVALID));
        if (m_pendingRead is not null)
            return InputMessages.Encode(new InputReply(InputMessages.READ, InputMessages.BUSY));
        if (m_events.Count > 0)
            return InputMessages.Encode(Read(request));
        if ((request.Flags & InputMessages.NONBLOCK) != 0)
            return InputMessages.Encode(new InputReply(InputMessages.READ, 0, 0));
        m_pendingRead = request;
        return null;
    }

    /// <summary>
    /// The reply of the READ held, once events are there.
    /// </summary>
    /// <returns>The reply's bytes, or null while no READ waits or nothing came.</returns>
    public byte[]? Completed()
    {
        if (m_pendingRead is not { } request || m_events.Count == 0)
            return null;
        m_pendingRead = null;
        return InputMessages.Encode(Read(request));
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

    private InputReply Attach(InputRequest request, KernelHandle window)
    {
        MemoryMapping mapping;
        try
        {
            mapping = MemoryMapping.Map(window, 0, (int)request.Length, MemoryMapping.READ_WRITE);
        }
        catch (CapabilityException exception)
        {
            return new InputReply(InputMessages.ATTACH, exception.Error);
        }
        Detach();
        m_window = mapping;
        return new InputReply(InputMessages.ATTACH, 0, request.Length);
    }

    private InputReply Read(InputRequest request)
    {
        var count = (int)Math.Min(request.Length / InputMessages.EVENT_BYTES, m_events.Count);
        var target = m_window!.Writable.Slice((int)request.Window, count * InputMessages.EVENT_BYTES);
        for (var i = 0; i < count; i++)
            InputMessages.WriteEvent(target.Slice(i * InputMessages.EVENT_BYTES), m_events.Dequeue());
        return new InputReply(InputMessages.READ, 0, count * InputMessages.EVENT_BYTES);
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
    /// The events dropped because the client read slowly.
    /// </summary>
    public long Dropped { get; private set; }

    /// <summary>
    /// The events waiting for a READ.
    /// </summary>
    public int Pending => m_events.Count;

    #endregion
}
