using System;
using System.Collections.Generic;
using OutWit.OS.Communication;
using OutWit.OS.Display;
using OutWit.OS.Hardware;
using OutWit.OS.Resources;

namespace OutWit.OS.DisplayService.Server;

/// <summary>
/// The service's side of the keyboard driver's input channel (witos/input.h, plan step C2.2): a window attached and one
/// READ always outstanding, so that every key the device reports reaches the layout at once; the service waits on the
/// channel's endpoint beside its others and pumps the reply when it comes.
/// </summary>
internal sealed class KeyboardLink : IDisposable
{
    #region Constants

    private const string PREFIX = "[DISPLAY] ";

    #endregion

    #region Fields

    private readonly Channel m_channel;
    private readonly KernelHandle m_windowObject;
    private readonly MemoryMapping m_window;
    private readonly UsKeyboardLayout m_layout = new();

    #endregion

    #region Constructors

    private KeyboardLink(Channel channel, KernelHandle windowObject, MemoryMapping window)
    {
        m_channel = channel;
        m_windowObject = windowObject;
        m_window = window;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Opens the link: the window attached and the first READ sent.
    /// </summary>
    /// <param name="input">The driver's input channel, the service's end.</param>
    /// <returns>The link.</returns>
    /// <exception cref="CapabilityException">A capability could not be had.</exception>
    /// <exception cref="InvalidOperationException">The driver refused the window.</exception>
    public static KeyboardLink Open(KernelHandle input)
    {
        var channel = new Channel(input);
        var windowObject = MemoryObject.Create(InputMessages.WINDOW_BYTES);
        var window = MemoryMapping.Map(windowObject, 0, InputMessages.WINDOW_BYTES, MemoryMapping.READ_WRITE);
        var link = new KeyboardLink(channel, windowObject, window);
        try
        {
            using (var attached = windowObject.Duplicate(Rights.Map | Rights.Write | Rights.Transfer))
                channel.Send(InputMessages.Encode(new InputRequest(InputMessages.ATTACH, 0, InputMessages.WINDOW_BYTES)), attached);
            var attach = link.Reply();
            if (attach.Error != 0)
                throw new InvalidOperationException($"The input driver refused the window: errno {attach.Error}.");
            link.SendRead();
            return link;
        }
        catch
        {
            link.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Takes the reply that arrived — the events of the READ held — and sends the next READ.
    /// </summary>
    /// <returns>The bytes a terminal sends for the keys, in their order.</returns>
    /// <exception cref="CapabilityException">The driver went (PEER_CLOSED).</exception>
    public byte[] Pump()
    {
        var reply = Reply();
        var bytes = new List<byte>();
        if (reply.Operation == InputMessages.READ && reply.Error == 0)
        {
            var events = m_window.Bytes.Slice(0, (int)Math.Min(reply.Length, InputMessages.WINDOW_BYTES));
            for (var at = 0; at + InputMessages.EVENT_BYTES <= events.Length; at += InputMessages.EVENT_BYTES)
            {
                var input = InputMessages.ReadEvent(events[at..]);
                if (input.Type == InputMessages.KEY)
                {
                    bytes.AddRange(m_layout.Translate(input.Code, input.Value));
                    Keys++;
                }
            }
        }
        else if (reply.Operation == InputMessages.READ)
            Console.WriteLine($"{PREFIX}the input driver failed a read: errno {reply.Error}");
        else
            Console.WriteLine($"{PREFIX}the input driver answered operation {reply.Operation}, which nothing asked");
        SendRead();
        return [.. bytes];
    }

    private void SendRead() =>
        m_channel.Send(InputMessages.Encode(new InputRequest(InputMessages.READ, 0, InputMessages.WINDOW_BYTES)));

    private InputReply Reply()
    {
        using var message = m_channel.Receive();
        return InputMessages.DecodeReply(message.Bytes);
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        m_channel.Dispose();
        m_window.Dispose();
        m_windowObject.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The channel's endpoint, to wait on.
    /// </summary>
    public KernelHandle Endpoint => m_channel.Endpoint;

    /// <summary>
    /// The key events taken so far.
    /// </summary>
    public long Keys { get; private set; }

    #endregion
}
