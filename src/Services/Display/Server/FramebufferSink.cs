using System;
using System.Collections.Generic;
using OutWit.OS.Display;
using OutWit.OS.Hardware;
using OutWit.OS.Resources;

namespace OutWit.OS.DisplayService.Server;

/// <summary>
/// The framebuffer as the renderer's sink (plan step C2.2): the device's memory region mapped window by window through
/// DEVICE_MEMORY (ABI-1 1.3, at most 64 pages an object), every window kept mapped for the service's life, each pixel
/// one aligned 32-bit store into the window that holds it.
/// </summary>
internal sealed class FramebufferSink : IFrameSink, IDisposable
{
    #region Constants

    private const long WINDOW_BYTES = (long)Device.WINDOW_PAGES * MemoryObject.PAGE;

    #endregion

    #region Fields

    private readonly List<KernelHandle> m_objects;
    private readonly List<MmioRegion> m_windows;

    #endregion

    #region Constructors

    private FramebufferSink(List<KernelHandle> objects, List<MmioRegion> windows)
    {
        m_objects = objects;
        m_windows = windows;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Maps the framebuffer's windows.
    /// </summary>
    /// <param name="device">The framebuffer device, held by the service.</param>
    /// <param name="geometry">The framebuffer's geometry.</param>
    /// <param name="regionBytes">The memory region's bytes, at least the geometry's.</param>
    /// <returns>The sink.</returns>
    /// <exception cref="CapabilityException">A window could not be had.</exception>
    /// <exception cref="ArgumentException">The region is smaller than the geometry needs.</exception>
    public static FramebufferSink Map(KernelHandle device, FramebufferGeometry geometry, long regionBytes)
    {
        if (regionBytes < geometry.Bytes)
            throw new ArgumentException($"A region of {regionBytes} bytes for a framebuffer of {geometry.Bytes}.", nameof(regionBytes));
        var objects = new List<KernelHandle>();
        var windows = new List<MmioRegion>();
        try
        {
            for (var offset = 0L; offset < geometry.Bytes; offset += WINDOW_BYTES)
            {
                var memory = Device.MemoryAt(device, 0, offset / MemoryObject.PAGE);
                objects.Add(memory);
                windows.Add(MmioRegion.Map(memory, (int)Math.Min(WINDOW_BYTES, regionBytes - offset)));
            }
            return new FramebufferSink(objects, windows);
        }
        catch
        {
            foreach (var window in windows)
                window.Dispose();
            foreach (var memory in objects)
                memory.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void Write32(long offset, uint value)
    {
        var window = (int)(offset / WINDOW_BYTES);
        m_windows[window].Write32((int)(offset - window * WINDOW_BYTES), value);
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var window in m_windows)
            window.Dispose();
        foreach (var memory in m_objects)
            memory.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The windows mapped.
    /// </summary>
    public int Windows => m_windows.Count;

    #endregion
}
