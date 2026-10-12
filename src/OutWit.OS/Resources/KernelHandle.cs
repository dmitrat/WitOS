using System;
using System.Runtime.InteropServices;
using OutWit.OS.Native;

namespace OutWit.OS.Resources;

/// <summary>
/// A handle of the system layer: a capability of the process, closed when it is released. A handle that moves over a
/// channel is no longer the process's and is never closed by it.
/// </summary>
public sealed class KernelHandle : SafeHandle
{
    #region Constructors

    /// <summary>
    /// Takes ownership of a handle value the system layer gave the process.
    /// </summary>
    /// <param name="value">The handle; zero is no handle.</param>
    public KernelHandle(ulong value)
        : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle((IntPtr)(long)value);
    }

    #endregion

    #region Functions

    /// <summary>
    /// A new handle of the same object with fewer rights.
    /// </summary>
    /// <param name="rights">The rights of the new handle, a subset of this handle's.</param>
    /// <returns>The new handle.</returns>
    /// <exception cref="CapabilityException">The handle has not the rights asked or the quota is spent.</exception>
    public unsafe KernelHandle Duplicate(Rights rights)
    {
        ulong duplicate = 0;
        CapabilityException.ThrowOnError("HANDLE_DUPLICATE", Libwitos.HandleDuplicate(Value, (ulong)rights, &duplicate));
        return new KernelHandle(duplicate);
    }

    /// <summary>
    /// Gives the handle up after it moved to another process: the handle is no longer this process's to close.
    /// </summary>
    internal void Moved()
    {
        SetHandleAsInvalid();
    }

    #endregion

    #region SafeHandle

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        return Libwitos.HandleClose((ulong)(long)handle) == 0;
    }

    #endregion

    #region Properties

    /// <inheritdoc />
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>
    /// The handle's value, as the system layer knows it.
    /// </summary>
    internal ulong Value => (ulong)(long)DangerousGetHandle();

    #endregion
}
