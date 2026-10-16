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
    /// The pages of a device's memory object: what one DEVICE_MEMORY object covers at most.
    /// </summary>
    public const int WINDOW_PAGES = 64;

    /// <summary>
    /// A memory region of a device as a memory object, mapped uncached and never executable: its first 64 pages.
    /// </summary>
    /// <param name="device">The device, with BIND.</param>
    /// <param name="region">The region's index in the descriptor.</param>
    /// <returns>The region's memory object.</returns>
    /// <exception cref="CapabilityException">The region is a port region or beyond the descriptor.</exception>
    public static KernelHandle Memory(KernelHandle device, int region) => MemoryAt(device, region, 0);

    /// <summary>
    /// A window of a device's memory region as a memory object (ABI-1 1.3, plan step C2.1): at most
    /// <see cref="WINDOW_PAGES"/> pages from the page named, so that a region of any size, a framebuffer, is reached
    /// window by window.
    /// </summary>
    /// <param name="device">The device, with BIND.</param>
    /// <param name="region">The region's index in the descriptor.</param>
    /// <param name="window">The region's page the object starts at.</param>
    /// <returns>The window's memory object.</returns>
    /// <exception cref="CapabilityException">The region is a port region, the window lies beyond it, or the region is
    /// beyond the descriptor.</exception>
    public static unsafe KernelHandle MemoryAt(KernelHandle device, int region, long window)
    {
        ulong memoryObject = 0;
        CapabilityException.ThrowOnError("DEVICE_MEMORY", Libwitos.DeviceMemoryAt(device.Value, (uint)region, (ulong)window, &memoryObject));
        return new KernelHandle(memoryObject);
    }

    #endregion
}
