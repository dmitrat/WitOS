using System;
using System.Collections.Generic;
using System.Linq;
using OutWit.OS.DeviceManager.Uhi;
using OutWit.OS.Display;
using OutWit.OS.Hardware;
using OutWit.OS.Resources;

namespace OutWit.OS.DeviceManager;

/// <summary>
/// The display's probe (plan step C2.1, as the storage and terminal probes): the device manager acquires the firmware's
/// framebuffer, maps its memory window by window through DEVICE_MEMORY (ABI-1 1.3) and draws the test pattern into it
/// with aligned 32-bit stores, then releases the device; the runtime-display scenario takes QEMU's screendump once the
/// probe reported and checks the pattern by the same function. What the probe drew stays on the display: the
/// framebuffer is memory, and nobody clears it.
/// </summary>
internal static class DisplayProbe
{
    #region Constants

    /// <summary>
    /// The start of the probe's line when the pattern is drawn, after which the scenario's runner takes the screendump.
    /// </summary>
    public const string DRAWN = "[DEVICES] display probe: ";

    private const string PREFIX = "[DEVICES] ";

    private const int PAGE = MemoryObject.PAGE;

    #endregion

    #region Functions

    /// <summary>
    /// Runs the probe on the framebuffer of the records, when there is one.
    /// </summary>
    /// <param name="table">The device table, with ACQUIRE.</param>
    /// <param name="records">The device records.</param>
    public static void Run(KernelHandle table, IReadOnlyList<DeviceRecord> records)
    {
        var record = records.FirstOrDefault(record => record.Class == FirmwareTranslator.FRAME_BUFFER);
        if (record is null)
        {
            Console.WriteLine($"{PREFIX}display probe: no framebuffer");
            return;
        }
        try
        {
            var geometry = FramebufferGeometry.FromDescriptor(record.Descriptor);
            var region = record.Descriptor.Regions[0];
            using var device = Device.Acquire(table, record.Descriptor.Index);
            var windows = 0;
            for (long page = 0; page * PAGE < geometry.Bytes; page += Device.WINDOW_PAGES)
            {
                using var memory = Device.MemoryAt(device, 0, page);
                var length = (int)Math.Min((long)Device.WINDOW_PAGES * PAGE, (long)region.Size - page * PAGE);
                using var window = MmioRegion.Map(memory, length);
                Draw(window, page * PAGE, length, geometry);
                windows++;
            }
            Console.WriteLine($"{DRAWN}{geometry.Width} by {geometry.Height}, stride {geometry.Stride}, {geometry.FormatName}, " +
                $"{geometry.Bytes} bytes in {windows} windows of {Device.WINDOW_PAGES} pages: test pattern drawn");
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException)
        {
            Console.WriteLine($"{PREFIX}display probe failed: {exception.Message}");
        }
    }

    // The pixels whose words lie in a window of the framebuffer, one aligned 32-bit store each: the mapping is device
    // memory, where a wider or unaligned store is not allowed on every ISA.
    private static void Draw(MmioRegion window, long offset, int length, FramebufferGeometry geometry)
    {
        var first = offset / FramebufferGeometry.PIXEL_BYTES;
        var count = length / FramebufferGeometry.PIXEL_BYTES;
        for (var i = 0L; i < count; i++)
        {
            var pixel = first + i;
            var row = (int)(pixel / geometry.Stride);
            var column = (int)(pixel % geometry.Stride);
            if (row >= geometry.Height)
                return;
            var color = column < geometry.Width ? TestPattern.Pixel(column, row, geometry.Width, geometry.Height) : ((byte)0, (byte)0, (byte)0);
            window.Write32((int)(i * FramebufferGeometry.PIXEL_BYTES), TestPattern.Encode(color, geometry.Format));
        }
    }

    #endregion
}
