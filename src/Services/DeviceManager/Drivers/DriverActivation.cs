using System;
using System.IO;
using OutWit.OS.Communication;
using OutWit.OS.DeviceManager.Uhi;
using OutWit.OS.Execution;
using OutWit.OS.Hardware;
using OutWit.OS.Resources;

namespace OutWit.OS.DeviceManager.Drivers;

/// <summary>
/// The activation of a driver (plan step D1.2, ADR 0025, decision 3): the device manager acquires the device, starts the
/// driver through the process manager with the grant of a channel to itself, and moves the device and its descriptor to
/// it over that channel; the driver answers when it took the device, with the endpoint of the service it offers.
/// </summary>
internal sealed class DriverActivation : IDisposable
{
    #region Constructors

    private DriverActivation(string path, DeviceRecord record, Channel channel, KernelHandle process, int status, KernelHandle? service)
    {
        Path = path;
        Record = record;
        Channel = channel;
        Process = process;
        Status = status;
        Service = service;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Starts the driver of a device and waits for its answer.
    /// </summary>
    /// <param name="table">The device table, with ACQUIRE.</param>
    /// <param name="record">The device.</param>
    /// <param name="path">The driver program.</param>
    /// <returns>The activation; its Status says whether the driver took the device.</returns>
    /// <exception cref="CapabilityException">The device, the channel or the driver's start could not be had.</exception>
    public static DriverActivation Start(KernelHandle table, DeviceRecord record, string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"The boot package has no driver {path}.", path);
        var device = Device.Acquire(table, record.Descriptor.Index);
        var (channel, theirs) = Channel.Create();
        try
        {
            // The device moves with the start, the device manager's last reference to it, before the driver runs.
            channel.Send(DriverStart.Encode(record.Descriptor), device);
            var process = ServiceLauncher.Start(path, [record.Id], theirs);
            try
            {
                var answer = channel.Receive();
                var status = ServiceStatus.Decode(answer.Bytes);
                if (status == 0 && answer.Handles.Count <= 1)
                    return new DriverActivation(path, record, channel, process, 0, answer.Handles.Count == 1 ? answer.Handles[0] : null);
                answer.Dispose();
                return new DriverActivation(path, record, channel, process, status == 0 ? ServiceStatus.PROTOCOL_ERROR : status, null);
            }
            catch
            {
                process.Dispose(); // a driver that ended before it answered
                throw;
            }
        }
        catch
        {
            device.Dispose();
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
        Service?.Dispose();
        Channel.Dispose();
        Process.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The driver program.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// The device it drives.
    /// </summary>
    public DeviceRecord Record { get; }

    /// <summary>
    /// The device manager's end of the driver's channel.
    /// </summary>
    public Channel Channel { get; }

    /// <summary>
    /// The driver's process.
    /// </summary>
    public KernelHandle Process { get; }

    /// <summary>
    /// The driver's answer: zero when it took the device, otherwise its errno value.
    /// </summary>
    public int Status { get; }

    /// <summary>
    /// The endpoint of the service a ready driver offers, which moved with its answer (D3.2: a block driver's block
    /// channel); null for a driver that offers none.
    /// </summary>
    public KernelHandle? Service { get; }

    #endregion
}
