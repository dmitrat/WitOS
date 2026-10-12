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
