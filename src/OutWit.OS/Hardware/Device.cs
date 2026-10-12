using OutWit.OS.Native;
using OutWit.OS.Resources;

namespace OutWit.OS.Hardware;

/// <summary>
/// The authority over devices (RFC 0011 §7.7): a device acquired from the table, and its memory regions.
/// </summary>
public static class Device
{
    #region Functions

    /// <summary>
    /// Acquires the device of a descriptor. The handle moves to another process when it is the process's last reference
    /// to the device (ABI-1 1.2).
    /// </summary>
    /// <param name="table">The device table, with ACQUIRE.</param>
    /// <param name="index">The descriptor's index.</param>
    /// <returns>The device handle, with BIND, QUERY, DUPLICATE and TRANSFER.</returns>
    /// <exception cref="CapabilityException">Another process holds the device, or the table handle has not ACQUIRE.</exception>
    public static unsafe KernelHandle Acquire(KernelHandle table, int index)
    {
        ulong device = 0;
        CapabilityException.ThrowOnError("DEVICE_ACQUIRE", Libwitos.DeviceAcquire(table.Value, (uint)index, &device));
        return new KernelHandle(device);
    }

    /// <summary>
    /// A memory region of a device as a memory object, mapped uncached and never executable.
    /// </summary>
    /// <param name="device">The device, with BIND.</param>
    /// <param name="region">The region's index in the descriptor.</param>
    /// <returns>The region's memory object.</returns>
    /// <exception cref="CapabilityException">The region is a port region, too large, or beyond the descriptor.</exception>
    public static unsafe KernelHandle Memory(KernelHandle device, int region)
    {
        ulong memoryObject = 0;
        CapabilityException.ThrowOnError("DEVICE_MEMORY", Libwitos.DeviceMemory(device.Value, (uint)region, &memoryObject));
        return new KernelHandle(memoryObject);
    }

    #endregion
}
