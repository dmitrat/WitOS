using System;
using OutWit.OS.Resources;

namespace OutWit.OS.Hardware.Virtio;

/// <summary>
/// A virtio PCI device's interrupt, bound to the driver's event (ADR 0029, plan step H1.2b): an MSI-X message where the
/// kernel serves message interrupts and the function has MSI-X — entry 0 for every queue, the configuration change left
/// without a vector — and otherwise the function's INTx line, as before. The message is bound and programmed before the
/// device's reset, since a queue takes its vector when it is set up (<see cref="VirtioPciTransport.EnableQueue"/>); the
/// line is bound once the driver set the device up, so that no interrupt the firmware left wakes its first request.
/// </summary>
public sealed class VirtioInterrupt : IDisposable
{
    #region Fields

    private readonly VirtioPciTransport m_transport;
    private readonly MsiX? m_msiX;
    private InterruptBinding? m_binding;

    #endregion

    #region Constructors

    private VirtioInterrupt(VirtioPciTransport transport, KernelHandle signaled, MsiX? msiX, InterruptBinding? binding, string description)
    {
        m_transport = transport;
        Signaled = signaled;
        m_msiX = msiX;
        m_binding = binding;
        Description = description;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Prepares the device's interrupt before its reset: an event, and an MSI-X message bound and programmed when the
    /// kernel and the function both have them.
    /// </summary>
    /// <param name="transport">The device's transport, before <see cref="VirtioPciTransport.Reset"/>.</param>
    /// <returns>The interrupt, to start once the device is set up.</returns>
    /// <exception cref="CapabilityException">The event or the message could not be had.</exception>
    public static VirtioInterrupt Prepare(VirtioPciTransport transport)
    {
        var signaled = Event.Create();
        MsiX? msiX = null;
        InterruptBinding? binding = null;
        try
        {
            if (InterruptBinding.MessagesSupported)
                msiX = MsiX.Find(transport.Configuration, transport.Device, transport.Descriptor);
            if (msiX is null)
                return new VirtioInterrupt(transport, signaled, null, null, "its INTx line");
            binding = InterruptBinding.BindMessage(transport.Device, signaled, out var address, out var data);
            msiX.Program(0, address, data);
            msiX.Enable();
            transport.QueueVector = 0;
            return new VirtioInterrupt(transport, signaled, msiX, binding, $"MSI-X entry 0, message {data:x2} at {address:x}");
        }
        catch
        {
            binding?.Dispose();
            msiX?.Dispose();
            signaled.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Starts the interrupt once the driver set the device up: the INTx line bound where no message is.
    /// </summary>
    /// <exception cref="CapabilityException">The line could not be bound.</exception>
    public void Start()
    {
        m_binding ??= InterruptBinding.Bind(m_transport.Device, 0, Signaled);
    }

    /// <summary>
    /// Reads what the interrupt the event reported was: for INTx the ISR status, whose read ends its cause; a message
    /// ends itself.
    /// </summary>
    /// <returns>The ISR status for INTx (bit 0 a queue, bit 1 a configuration change); 1 for a message, which only the
    /// queues send.</returns>
    public byte ReadStatus() => m_msiX is not null ? (byte)1 : m_transport.ReadInterruptStatus();

    /// <summary>
    /// Unmasks the INTx line once the driver served the interrupt; a message leaves nothing masked.
    /// </summary>
    public void Acknowledge()
    {
        if (m_msiX is null)
            m_binding?.Acknowledge();
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        m_msiX?.Disable();
        m_binding?.Dispose();
        m_msiX?.Dispose();
        Signaled.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The event the kernel sets on each interrupt, which the driver waits on.
    /// </summary>
    public KernelHandle Signaled { get; }

    /// <summary>
    /// Whether the interrupt is an MSI-X message.
    /// </summary>
    public bool IsMessage => m_msiX is not null;

    /// <summary>
    /// What the interrupt is, for the driver's log: its INTx line or its MSI-X message.
    /// </summary>
    public string Description { get; }

    #endregion
}
