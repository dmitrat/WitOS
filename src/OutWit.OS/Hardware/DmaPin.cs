using System;
using System.Collections.Generic;
using OutWit.OS.Native;
using OutWit.OS.Resources;

namespace OutWit.OS.Hardware;

/// <summary>
/// A window of an anonymous memory object pinned for a device's DMA (RFC 0011 §7.7): its pages stay resident and
/// unmoved, at the physical addresses the kernel reported, until the pin ends. Without an IOMMU the holder of the device
/// is the trust boundary: the kernel cannot stop a device programmed with another address.
/// </summary>
public sealed class DmaPin : IDisposable
{
    #region Constants

    private const int CAPACITY = 64;

    #endregion

    #region Constructors

    private DmaPin(KernelHandle handle, IReadOnlyList<(ulong Address, ulong Size)> ranges)
    {
        Handle = handle;
        Ranges = ranges;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Pins a page-aligned window of the object for the device.
    /// </summary>
    /// <param name="device">The device, with BIND.</param>
    /// <param name="memoryObject">An anonymous object the process holds with MAP.</param>
    /// <param name="offset">The page-aligned offset of the window.</param>
    /// <param name="bytes">The window's bytes, whole pages.</param>
    /// <returns>The pin, with the window's physical ranges.</returns>
    /// <exception cref="CapabilityException">The window lies outside the object, or the handles have not the rights.</exception>
    public static unsafe DmaPin Pin(KernelHandle device, KernelHandle memoryObject, int offset, int bytes)
    {
        var values = stackalloc ulong[CAPACITY * 2];
        ulong pin = 0;
        CapabilityException.ThrowOnError("DMA_PIN",
            Libwitos.DmaPin(device.Value, memoryObject.Value, (ulong)offset, (ulong)bytes, values, CAPACITY, &pin));
        var ranges = new List<(ulong, ulong)>();
        for (var i = 0; i < CAPACITY && values[i * 2 + 1] != 0; i++)
            ranges.Add((values[i * 2], values[i * 2 + 1]));
        return new DmaPin(new KernelHandle(pin), ranges);
    }

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
    /// The pin's handle.
    /// </summary>
    public KernelHandle Handle { get; }

    /// <summary>
    /// The window's physical ranges, merged, in the window's order.
    /// </summary>
    public IReadOnlyList<(ulong Address, ulong Size)> Ranges { get; }

    /// <summary>
    /// The physical address of the window's first byte.
    /// </summary>
    public ulong Address => Ranges[0].Address;

    #endregion
}
