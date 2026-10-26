using System;
using System.Collections.Generic;
using System.IO;
using OutWit.OS.Communication;
using OutWit.OS.DeviceManager.Uhi;
using OutWit.OS.Display;
using OutWit.OS.Execution;
using OutWit.OS.Hardware;
using OutWit.OS.Resources;

namespace OutWit.OS.DeviceManager;

/// <summary>
/// The display service started with the firmware's framebuffer and the keyboard driver's input channel (plan step C2.2):
/// the device manager acquires the framebuffer, starts the service through the process manager with the grant of a
/// channel to itself and moves the device, its descriptor and the keyboard's channel to it over that channel; the
/// service answers when it drew the screen, with the end of the console channel it serves, which the terminal service
/// takes as its main console.
/// </summary>
internal sealed class DisplayActivation : IDisposable
{
    #region Constants

    /// <summary>
    /// The service's program in the boot package.
    /// </summary>
    public const string PATH = DisplayStart.PATH;

    #endregion

    #region Constructors

    private DisplayActivation(DeviceRecord record, Channel channel, KernelHandle process, int status, KernelHandle? console)
    {
        Record = record;
        Channel = channel;
        Process = process;
        Status = status;
        Console = console;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Starts the service with the framebuffer and the keyboards' channels, which move to it.
    /// </summary>
    /// <param name="table">The device table, with ACQUIRE.</param>
    /// <param name="record">The framebuffer.</param>
    /// <param name="keyboards">The keyboard drivers' input channels, the service's ends, moved: none, one or two (H5.2: a
    /// PC's PS/2 keyboard beside its USB keyboards).</param>
    /// <returns>The activation: ready with the console channel's end, or its status.</returns>
    /// <exception cref="FileNotFoundException">The boot package has no display service.</exception>
    /// <exception cref="CapabilityException">A capability could not be had.</exception>
    public static DisplayActivation Start(KernelHandle table, DeviceRecord record, IReadOnlyList<KernelHandle> keyboards)
    {
        if (!File.Exists(PATH))
        {
            foreach (var keyboard in keyboards)
                keyboard.Dispose();
            throw new FileNotFoundException($"The boot package has no {PATH}.", PATH);
        }
        var device = Device.Acquire(table, record.Descriptor.Index);
        var (channel, theirs) = Channel.Create();
        try
        {
            // The framebuffer moves with the start, the device manager's last reference to it, the keyboards' channels
            // beside it, before the service runs.
            using (device)
            {
                try
                {
                    channel.Send(DisplayStart.Encode(record.Descriptor), [device, .. keyboards]);
                }
                finally
                {
                    foreach (var keyboard in keyboards)
                        keyboard.Dispose();
                }
            }
            var process = ServiceLauncher.Start(PATH, [record.Id], theirs);
            try
            {
                var answer = channel.Receive();
                var status = ServiceStatus.Decode(answer.Bytes);
                if (status == 0 && answer.Handles.Count == 1)
                    return new DisplayActivation(record, channel, process, 0, answer.Handles[0]);
                answer.Dispose();
                return new DisplayActivation(record, channel, process, status == 0 ? ServiceStatus.PROTOCOL_ERROR : status, null);
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

    /// <summary>
    /// Takes the console channel the service offered, for the terminal service to hold; the activation keeps none
    /// afterwards.
    /// </summary>
    /// <returns>The channel's end.</returns>
    /// <exception cref="InvalidOperationException">The service offered none, or it was taken.</exception>
    public KernelHandle TakeConsole()
    {
        var console = Console ?? throw new InvalidOperationException($"The display service {PATH} offers no console channel.");
        Console = null;
        return console;
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        Console?.Dispose();
        Channel.Dispose();
        Process.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The framebuffer the service draws into.
    /// </summary>
    public DeviceRecord Record { get; }

    /// <summary>
    /// The channel the service was granted, on which the device manager asks it to stop.
    /// </summary>
    public Channel Channel { get; }

    /// <summary>
    /// The service's process.
    /// </summary>
    public KernelHandle Process { get; }

    /// <summary>
    /// Zero when the service is ready, or its errno value.
    /// </summary>
    public int Status { get; }

    /// <summary>
    /// The end of the console channel a ready service offered; null unless ready, or once taken.
    /// </summary>
    public KernelHandle? Console { get; private set; }

    #endregion
}
