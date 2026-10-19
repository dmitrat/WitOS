using System;
using System.Collections.Generic;
using System.IO;
using OutWit.OS.Communication;
using OutWit.OS.Execution;
using OutWit.OS.Networking;
using OutWit.OS.Resources;

namespace OutWit.OS.DeviceManager;

/// <summary>
/// The network service's start (plan step W1.2a, witos/socket.h): the device manager starts /system/Network through the
/// process manager with the grant of a channel to itself, moves each adapter's packet channel to it, and takes its answer,
/// whose one handle is the clients' endpoint. The device manager connects to it for its own probe; processes reach it
/// through the libc from W2.1.
/// </summary>
internal sealed class NetworkActivation : IDisposable
{
    #region Constants

    /// <summary>
    /// The service's program.
    /// </summary>
    public const string PATH = "/system/Network";

    #endregion

    #region Constructors

    private NetworkActivation(Channel channel, KernelHandle process, int status, KernelHandle? clients)
    {
        Channel = channel;
        Process = process;
        Status = status;
        Clients = clients;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Starts the service with the adapters and waits for its answer.
    /// </summary>
    /// <param name="adapters">Each adapter's device and packet channel; the channels move to the service.</param>
    /// <returns>The activation; its Status says whether the service is ready.</returns>
    /// <exception cref="CapabilityException">The channel or the service's start could not be had.</exception>
    /// <exception cref="FileNotFoundException">The boot package has no network service.</exception>
    public static NetworkActivation Start(IReadOnlyList<(string Device, Channel Packets)> adapters)
    {
        if (!File.Exists(PATH))
            throw new FileNotFoundException($"The boot package has no {PATH}.", PATH);
        var (channel, theirs) = Channel.Create();
        try
        {
            // A queue holds four messages: three adapters and the end before the service starts.
            foreach (var (device, packets) in adapters)
            {
                using (packets)
                    channel.Send(SocketMessages.EncodeAdapter(device), packets.Endpoint);
            }
            channel.Send(SocketMessages.EncodeAdapter("", SocketMessages.ADAPTERS_END));
            var process = ServiceLauncher.Start(PATH, [], theirs);
            try
            {
                var answer = channel.Receive();
                var status = ServiceStatus.Decode(answer.Bytes);
                if (status == 0 && answer.Handles.Count == 1)
                    return new NetworkActivation(channel, process, 0, answer.Handles[0]);
                answer.Dispose();
                return new NetworkActivation(channel, process, status == 0 ? ServiceStatus.PROTOCOL_ERROR : status, null);
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }
        catch
        {
            theirs.Dispose();
            channel.Dispose();
            throw;
        }
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        Clients?.Dispose();
        Channel.Dispose();
        Process.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The device manager's end of the service's channel.
    /// </summary>
    public Channel Channel { get; }

    /// <summary>
    /// The service's process.
    /// </summary>
    public KernelHandle Process { get; }

    /// <summary>
    /// The service's answer: zero when it is ready, otherwise its errno value.
    /// </summary>
    public int Status { get; }

    /// <summary>
    /// The clients' endpoint (SEND, DUPLICATE, TRANSFER) the service offered; null when it is not ready.
    /// </summary>
    public KernelHandle? Clients { get; }

    #endregion
}
