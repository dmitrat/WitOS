using System;
using System.Collections.Generic;
using System.Threading;
using OutWit.OS.Communication;
using OutWit.OS.Namespaces;
using OutWit.OS.Resources;

namespace OutWit.OS.Namespace.Server;

/// <summary>
/// A client connected (plan step D5.1): its session channel and window, served on a thread of its own, one request at
/// a time, until the client closes its end, which closes everything it opened, or the service stops (D6.1), after which
/// every request is refused with ESHUTDOWN.
/// </summary>
internal sealed class NamespaceConnection
{
    #region Constants

    private const string PREFIX = "[NAMESPACE] ";

    private const int SHUTDOWN = 108; // ESHUTDOWN

    #endregion

    #region Fields

    private static readonly List<NamespaceConnection> m_live = [];

    private readonly Channel m_channel;
    private readonly MappedWindow m_window;
    private readonly NamespaceSession m_session;
    private readonly object m_gate = new();
    private bool m_stopped;

    #endregion

    #region Constructors

    private NamespaceConnection(Channel channel, MappedWindow window, MountTable mounts)
    {
        m_channel = channel;
        m_window = window;
        m_session = new NamespaceSession(mounts, window);
    }

    #endregion

    #region Functions

    /// <summary>
    /// Accepts a CONNECT and serves the session on a thread of its own.
    /// </summary>
    /// <param name="message">The CONNECT: the session's end and the window's object in its handle slots.</param>
    /// <param name="mounts">The namespace's volumes.</param>
    /// <returns>Whether the client connected.</returns>
    public static bool Accept(ChannelMessage message, MountTable mounts)
    {
        NamespaceRequest request;
        try
        {
            request = NamespaceMessages.DecodeRequest(message.Bytes);
        }
        catch (FormatException)
        {
            message.Dispose();
            return false;
        }
        if (request.Operation != NamespaceMessages.CONNECT || message.Handles.Count != 2 || request.Length <= 0 ||
            request.Length > 64 * MemoryObject.PAGE || request.Length % MemoryObject.PAGE != 0)
        {
            message.Dispose();
            return false;
        }
        var channel = new Channel(message.Handles[0]);
        try
        {
            var connection = new NamespaceConnection(channel, MappedWindow.Map(message.Handles[1], (int)request.Length), mounts);
            lock (m_live)
                m_live.Add(connection);
            new Thread(connection.Serve) { IsBackground = true, Name = "namespace session" }.Start();
            return true;
        }
        catch (CapabilityException exception)
        {
            Console.WriteLine($"{PREFIX}a client's window could not be mapped: {exception.Message}");
            channel.Dispose();
            return false;
        }
    }

    /// <summary>
    /// Stops every session (D6.1): each finishes the request it is serving, closes the files it opened and refuses every
    /// later request.
    /// </summary>
    /// <returns>The sessions stopped.</returns>
    public static int StopAll()
    {
        NamespaceConnection[] live;
        lock (m_live)
            live = [.. m_live];
        foreach (var connection in live)
        {
            lock (connection.m_gate)
            {
                connection.m_stopped = true;
                connection.m_session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        return live.Length;
    }

    // The session's requests until the client closes its end.
    private void Serve()
    {
        try
        {
            for (; ; )
            {
                byte[] reply;
                using (var message = m_channel.Receive())
                {
                    NamespaceRequest request;
                    try
                    {
                        request = NamespaceMessages.DecodeRequest(message.Bytes);
                    }
                    catch (FormatException)
                    {
                        reply = NamespaceMessages.Encode(new NamespaceReply(0, 71)); // EPROTO
                        m_channel.Send(reply);
                        continue;
                    }
                    lock (m_gate)
                    {
                        try
                        {
                            reply = NamespaceMessages.Encode(m_stopped
                                ? new NamespaceReply(request.Operation, SHUTDOWN)
                                : m_session.HandleAsync(request).AsTask().GetAwaiter().GetResult());
                        }
                        catch (Exception exception) when (exception is not CapabilityException)
                        {
                            // A failure the session does not translate: the client gets EIO, the log the failure.
                            Console.WriteLine($"{PREFIX}request {request.Operation} failed: {exception.GetType().Name}: {exception.Message}");
                            reply = NamespaceMessages.Encode(new NamespaceReply(request.Operation, 5));
                        }
                    }
                }
                m_channel.Send(reply);
            }
        }
        catch (CapabilityException exception) when (exception.Error == Channel.PEER_CLOSED)
        {
        }
        finally
        {
            lock (m_live)
                m_live.Remove(this);
            lock (m_gate)
                m_session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            m_window.Dispose();
            m_channel.Dispose();
        }
    }

    #endregion
}
