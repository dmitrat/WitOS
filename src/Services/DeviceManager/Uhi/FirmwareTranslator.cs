using System.Globalization;
using OutWit.OS.Display;
using OutWit.OS.Hardware;

namespace OutWit.OS.DeviceManager.Uhi;

/// <summary>
/// The translation of a firmware-bus descriptor into a UHI device record (plan step C2.1): a device the firmware set
/// up and the boot contract described, which the platform published beside the bus's functions. The one kind is the
/// framebuffer, whose contract is the display's memory with its geometry (RFC 0009 §26: a simple framebuffer).
/// </summary>
internal static class FirmwareTranslator
{
    #region Constants

    /// <summary>
    /// The UHI class of the firmware's framebuffer: linear memory with a geometry, which a display service draws into.
    /// </summary>
    public const string FRAME_BUFFER = "FrameBuffer";

    /// <summary>
    /// The provider of a firmware device.
    /// </summary>
    public const string PROVIDER = "Firmware";

    /// <summary>
    /// The identity of the framebuffer: one per boot.
    /// </summary>
    public const string FRAME_BUFFER_ID = "firmware/framebuffer";

    #endregion

    #region Functions

    /// <summary>
    /// The record of a firmware-bus descriptor.
    /// </summary>
    /// <param name="descriptor">A descriptor whose bus is the firmware's.</param>
    /// <returns>The device record.</returns>
    public static DeviceRecord Translate(DeviceDescriptor descriptor)
    {
        var kind = descriptor.Identity.Count > 0 ? descriptor.Identity[0] : 0;
        return kind == FramebufferGeometry.KIND
            ? new DeviceRecord(FRAME_BUFFER_ID, FRAME_BUFFER, PROVIDER, descriptor)
            : new DeviceRecord(string.Create(CultureInfo.InvariantCulture, $"firmware/{kind}"),
                string.Create(CultureInfo.InvariantCulture, $"FirmwareDevice{kind}"), PROVIDER, descriptor);
    }

    #endregion
}
