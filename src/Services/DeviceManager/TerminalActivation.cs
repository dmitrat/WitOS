using System;
using System.Collections.Generic;
using System.IO;
using OutWit.OS.Communication;
using OutWit.OS.Execution;
using OutWit.OS.Resources;
using OutWit.OS.Terminals;

namespace OutWit.OS.DeviceManager;

/// <summary>
/// The terminal service started with the consoles' serial channels (plan steps N4.3a and C2.2): the device manager moves
/// the channels to the service in a DEVICE message before the service runs — the main console first, whose size the
/// terminal takes, then the console that mirrors it — and keeps the clients' endpoint the service answers with for the
/// root task's process manager, which gives it every process it starts afterwards.
/// </summary>
internal sealed class TerminalActivation : IDisposable
{
    #region Constants

    /// <summary>
    /// The service's program in the boot package.
    /// </summary>
    public const string PATH = TerminalMessages.PATH;

    /// <summary>
    /// The consoles a terminal takes at most: the main one and a mirror.
    /// </summary>
    public const int CONSOLES = 2;

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
    /// Starts the service with the consoles' serial channels, which move to it.
    /// </summary>
    /// <param name="consoles">The service's ends of the consoles' serial channels, the main console first, one or two; moved.</param>
    /// <returns>The activation: ready with the clients' endpoint, or its status.</returns>
    /// <exception cref="ArgumentException">No console, or more than the terminal takes.</exception>
    /// <exception cref="FileNotFoundException">The boot package has no terminal service.</exception>
    /// <exception cref="CapabilityException">A capability could not be had.</exception>
    public static TerminalActivation Start(IReadOnlyList<KernelHandle> consoles)
    {
        if (consoles.Count is < 1 or > CONSOLES)
        {
            foreach (var console in consoles)
                console.Dispose();
            throw new ArgumentException($"A terminal of {consoles.Count} consoles.", nameof(consoles));
        }
        if (!File.Exists(PATH))
        {
            foreach (var console in consoles)
                console.Dispose();
            throw new FileNotFoundException($"The boot package has no {PATH}.", PATH);
        }
        var (channel, theirs) = Channel.Create();
        try
        {
            try
            {
                channel.Send(TerminalMessages.EncodeDevice(), [.. consoles]);
            }
            finally
            {
                foreach (var console in consoles)
                    console.Dispose();
            }
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
