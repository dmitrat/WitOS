using System;
using OutWit.OS.Native;

namespace OutWit.OS.Resources;

/// <summary>
/// Events and the one wait of the system layer (RFC 0011 §7.4): an auto-reset event, and a wait for one of up to four
/// objects until an absolute monotonic deadline.
/// </summary>
public static class Event
{
    #region Constants

    /// <summary>
    /// The deadline of a wait without one.
    /// </summary>
    public const ulong INFINITE = ulong.MaxValue;

    #endregion

    #region Functions

    /// <summary>
    /// A new auto-reset event, with WAIT and SIGNAL.
    /// </summary>
    /// <returns>The event.</returns>
    /// <exception cref="CapabilityException">The event quota is spent.</exception>
    public static unsafe KernelHandle Create()
    {
        ulong handle = 0;
        CapabilityException.ThrowOnError("EVENT_CREATE", Libwitos.EventCreate(&handle));
        return new KernelHandle(handle);
    }

    /// <summary>
    /// Waits until one of the objects completes or the deadline passes.
    /// </summary>
    /// <param name="deadline">An absolute monotonic deadline, or INFINITE.</param>
    /// <param name="handles">One to four waitable objects, each with WAIT.</param>
    /// <returns>The index of the object that completed.</returns>
    /// <exception cref="CapabilityException">The deadline passed (ETIMEDOUT) or a handle cannot be waited for.</exception>
    public static unsafe int Wait(ulong deadline, params ReadOnlySpan<KernelHandle> handles)
    {
        if (handles.Length is 0 or > 4)
            throw new ArgumentException("A wait takes one to four objects.", nameof(handles));
        var values = stackalloc ulong[4];
        for (var i = 0; i < handles.Length; i++)
            values[i] = handles[i].Value;
        uint index = 0;
        CapabilityException.ThrowOnError("OBJECT_WAIT", Libwitos.ObjectWait(values, (uint)handles.Length, deadline, &index));
        return (int)index;
    }

    #endregion
}
