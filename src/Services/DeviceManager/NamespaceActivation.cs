using System;
using System.Collections.Generic;
using System.IO;
using OutWit.OS.Communication;
using OutWit.OS.Execution;
using OutWit.OS.Namespaces;
using OutWit.OS.Resources;
using OutWit.OS.Storage;

namespace OutWit.OS.DeviceManager;

/// <summary>
/// The namespace service's start (plan step D5.1, witos/namespace.h): the device manager starts /system/Namespace
/// through the process manager with the grant of a channel to itself, moves each disk's block channel to it, and takes
/// its answer, whose one handle is the clients' endpoint the device manager offers the root task in its own answer.
/// </summary>
internal sealed class NamespaceActivation : IDisposable
{
    #region Constants

    /// <summary>
    /// The service's program.
    /// </summary>
    public const string PATH = "/system/Namespace";

    #endregion

    #region Constructors

    private NamespaceActivation(Channel channel, KernelHandle process, int status, KernelHandle? clients)
    {
        Channel = channel;
        Process = process;
        Status = status;
        Clients = clients;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Starts the service with the disks and waits for its answer.
    /// </summary>
    /// <param name="disks">Each disk's device and resource; the resources give their channels up to the service.</param>
    /// <returns>The activation; its Status says whether the service is ready.</returns>
    /// <exception cref="CapabilityException">The channel or the service's start could not be had.</exception>
    /// <exception cref="FileNotFoundException">The boot package has no namespace service.</exception>
    public static NamespaceActivation Start(IReadOnlyList<(string Device, ChannelBlockStorage Disk)> disks)
    {
        if (!File.Exists(PATH))
            throw new FileNotFoundException($"The boot package has no {PATH}.", PATH);
        var (channel, theirs) = Channel.Create();
        try
        {
            foreach (var (device, disk) in disks)
            {
                var flags = disk.Info.IsReadOnly ? NamespaceMessages.DISK_READ_ONLY : 0;
                using var block = disk.Detach();
                channel.Send(NamespaceMessages.EncodeDisk(flags, device), block.Endpoint);
            }
            channel.Send(NamespaceMessages.EncodeDisk(NamespaceMessages.DISK_END, ""));
            var process = ServiceLauncher.Start(PATH, [], theirs);
            try
            {
                var answer = channel.Receive();
                var status = ServiceStatus.Decode(answer.Bytes);
                if (status == 0 && answer.Handles.Count == 1)
                    return new NamespaceActivation(channel, process, 0, answer.Handles[0]);
                answer.Dispose();
                return new NamespaceActivation(channel, process, status == 0 ? ServiceStatus.PROTOCOL_ERROR : status, null);
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
