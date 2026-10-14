using System;
using System.IO;
using OutWit.OS.Communication;
using OutWit.OS.Execution;
using OutWit.OS.Resources;
using OutWit.OS.Terminals;

namespace OutWit.OS.DeviceManager;

/// <summary>
/// The terminal service started with the console driver's serial channel (plan step N4.3a): the device manager moves
/// the channel to the service in a DEVICE message before the service runs, and keeps the clients' endpoint the
/// service answers with for the root task's process manager, which gives it every process it starts afterwards.
/// </summary>
internal sealed class TerminalActivation : IDisposable
{
    #region Constants

    /// <summary>
    /// The service's program in the boot package.
    /// </summary>
    public const string PATH = TerminalMessages.PATH;

    #endregion

    #region Constructors

    private TerminalActivation(Channel channel, KernelHandle process, int status, KernelHandle? clients)
    {
        Channel = channel;
        Process = process;
        Status = status;
        Clients = clients;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Starts the service with the console driver's serial channel, which moves to it.
    /// </summary>
    /// <param name="serial">The service's end of the console driver's serial channel, moved.</param>
    /// <returns>The activation: ready with the clients' endpoint, or its status.</returns>
    /// <exception cref="FileNotFoundException">The boot package has no terminal service.</exception>
    /// <exception cref="CapabilityException">A capability could not be had.</exception>
    public static TerminalActivation Start(KernelHandle serial)
    {
        if (!File.Exists(PATH))
        {
            serial.Dispose();
            throw new FileNotFoundException($"The boot package has no {PATH}.", PATH);
        }
        var (channel, theirs) = Channel.Create();
        try
        {
            using (serial)
                channel.Send(TerminalMessages.EncodeDevice(), serial);
            var process = ServiceLauncher.Start(PATH, [], theirs);
            try
            {
                var answer = channel.Receive();
                var status = ServiceStatus.Decode(answer.Bytes);
                if (status == 0 && answer.Handles.Count == 1)
                    return new TerminalActivation(channel, process, 0, answer.Handles[0]);
                answer.Dispose();
                return new TerminalActivation(channel, process, status == 0 ? ServiceStatus.PROTOCOL_ERROR : status, null);
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
    /// The clients' endpoint the service offered (SEND, DUPLICATE, TRANSFER); null unless ready.
    /// </summary>
    public KernelHandle? Clients { get; }

    #endregion
}
