using System;
using System.IO;
using OutWit.OS.Communication;
using OutWit.OS.Hardware;
using OutWit.OS.Resources;

namespace OutWit.OS.Drivers.VirtioConsole.Virtio;

/// <summary>
/// The serial protocol on the driver's side (witos/serial.h, plan step N4.3a): the client's window mapped, a WRITE
/// transmitted through the device, a READ answered from what the device received or held until something comes, and
/// SIZE from the device's configuration; every request checked before anything reaches the device.
/// </summary>
internal sealed class SerialServer : IDisposable
{
    #region Fields

    private readonly VirtioConsoleDevice m_device;
    private readonly string m_name;
    private MemoryMapping? m_window;
    private SerialRequest? m_pendingRead;

    #endregion

    #region Constructors

    /// <summary>
    /// The server of a device.
    /// </summary>
    /// <param name="device">The device.</param>
    /// <param name="name">The device's identity, for the lines.</param>
    public SerialServer(VirtioConsoleDevice device, string name)
    {
        m_device = device;
        m_name = name;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Serves one message of the serial channel; the message's handles stay the caller's to close.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>The reply's bytes, or null for a READ held until the device receives something.</returns>
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
        var handles = request.Operation == SerialMessages.ATTACH ? 1 : 0;
        var error = message.Handles.Count != handles ? SerialMessages.INVALID : SerialMessages.Check(request, m_window?.Length ?? 0);
        if (error != 0)
            return SerialMessages.Encode(new SerialReply(request.Operation, error));
        try
        {
            switch (request.Operation)
            {
                case SerialMessages.ATTACH:
                    return SerialMessages.Encode(Attach(request, message.Handles[0]));
                case SerialMessages.SIZE_QUERY:
                    return SerialMessages.Encode(new SerialReply(request.Operation, 0, 0, m_device.Columns, m_device.Rows));
                case SerialMessages.WRITE:
                    m_device.Transmit(m_window!.Bytes.Slice((int)request.Window, (int)request.Length));
                    return SerialMessages.Encode(new SerialReply(request.Operation, 0, request.Length));
                default:
                    if (m_pendingRead is not null)
                        return SerialMessages.Encode(new SerialReply(request.Operation, SerialMessages.BUSY));
                    if (m_device.Available > 0)
                        return SerialMessages.Encode(Read(request));
                    if ((request.Flags & SerialMessages.NONBLOCK) != 0)
                        return SerialMessages.Encode(new SerialReply(request.Operation, 0, 0));
                    m_pendingRead = request;
                    return null;
            }
        }
        catch (IOException exception)
        {
            Console.WriteLine($"[VIRTIO-CONSOLE] driver of {m_name}: {exception.Message}; the client gets EIO");
            return SerialMessages.Encode(new SerialReply(request.Operation, SerialMessages.DEVICE_ERROR));
        }
    }

    /// <summary>
    /// The reply of the READ held, once the device received something.
    /// </summary>
    /// <returns>The reply's bytes, or null while no READ waits or nothing came.</returns>
    public byte[]? Completed()
    {
        if (m_pendingRead is not { } request || m_device.Available == 0)
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
            return new SerialReply(SerialMessages.ATTACH, exception.Error);
        }
        Detach();
        m_window = mapping;
        return new SerialReply(SerialMessages.ATTACH, 0, request.Length);
    }

    private SerialReply Read(SerialRequest request)
    {
        var count = m_device.Read(m_window!.Writable.Slice((int)request.Window, (int)request.Length));
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
}
