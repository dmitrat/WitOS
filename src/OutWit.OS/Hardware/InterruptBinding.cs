using System;
using OutWit.OS.Native;
using OutWit.OS.Resources;

namespace OutWit.OS.Hardware;

/// <summary>
/// A device's interrupt line bound to an event (RFC 0011 §7.7): the kernel masks the line and sets the event on each
/// interrupt, and the driver unmasks it once it served the interrupt. The binding ends with its handle, masking the
/// line.
/// </summary>
public sealed class InterruptBinding : IDisposable
{
    #region Constructors

    private InterruptBinding(KernelHandle handle)
    {
        Handle = handle;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Binds a line of the device to the event.
    /// </summary>
    /// <param name="device">The device, with BIND.</param>
    /// <param name="line">The line's index in the descriptor.</param>
    /// <param name="signaled">The event the kernel sets on each interrupt.</param>
    /// <returns>The binding.</returns>
    /// <exception cref="CapabilityException">Another binding holds the line, or the line is beyond the descriptor.</exception>
    public static unsafe InterruptBinding Bind(KernelHandle device, int line, KernelHandle signaled)
    {
        ulong binding = 0;
        CapabilityException.ThrowOnError("INTERRUPT_BIND", Libwitos.InterruptBind(device.Value, (uint)line, signaled.Value, &binding));
        return new InterruptBinding(new KernelHandle(binding));
    }

    /// <summary>
    /// Binds a message target of the board to the event (ADR 0029, H1.2b): the driver writes the address and the data
    /// into its function's MSI capability or MSI-X entry, and each message the device writes sets the event.
    /// </summary>
    /// <param name="device">The device, with BIND.</param>
    /// <param name="signaled">The event the kernel sets on each message.</param>
    /// <param name="address">Where the device writes the message.</param>
    /// <param name="data">What it writes.</param>
    /// <returns>The binding.</returns>
    /// <exception cref="CapabilityException">The board has no message interrupts, or every target is bound.</exception>
    public static unsafe InterruptBinding BindMessage(KernelHandle device, KernelHandle signaled, out ulong address, out uint data)
    {
        ulong binding = 0, target = 0;
        uint message = 0;
        CapabilityException.ThrowOnError("INTERRUPT_BIND_MESSAGE",
            Libwitos.InterruptBindMessage(device.Value, signaled.Value, &binding, &target, &message));
        address = target;
        data = message;
        return new InterruptBinding(new KernelHandle(binding));
    }

    /// <summary>
    /// Whether the board serves message interrupts (QUERY's feature, H1.2b).
    /// </summary>
    public static bool MessagesSupported => Libwitos.MessageInterrupts() != 0;

    /// <summary>
    /// Unmasks the line after its interrupt was served.
    /// </summary>
    /// <exception cref="CapabilityException">The binding is gone.</exception>
    public void Acknowledge() => CapabilityException.ThrowOnError("INTERRUPT_ACK", Libwitos.InterruptAck(Handle.Value));

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        Handle.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The binding's handle, with ACK.
    /// </summary>
    public KernelHandle Handle { get; }

    #endregion
}
