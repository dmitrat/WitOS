using System.Globalization;
using OutWit.OS.Hardware;

namespace OutWit.OS.DeviceManager.Uhi;

/// <summary>
/// The translation of a legacy-bus descriptor into a UHI device record (ABI-1 1.7, plan step H5.2): a device of the
/// board's legacy resources that its firmware tables name. The one kind is the PC's i8042, a keyboard controller.
/// </summary>
internal static class LegacyTranslator
{
    #region Constants

    /// <summary>
    /// The UHI class of the i8042: a keyboard controller, whose driver serves its keyboard on the input protocol.
    /// </summary>
    public const string KEYBOARD_CONTROLLER = "KeyboardController";

    /// <summary>
    /// The provider of a legacy device.
    /// </summary>
    public const string PROVIDER = "Legacy";

    /// <summary>
    /// The identity of the i8042: one per board.
    /// </summary>
    public const string I8042_ID = "legacy/i8042";

    #endregion

    #region Functions

    /// <summary>
    /// The record of a legacy-bus descriptor.
    /// </summary>
    /// <param name="descriptor">A descriptor whose bus is the legacy one.</param>
    /// <returns>The device record.</returns>
    public static DeviceRecord Translate(DeviceDescriptor descriptor)
    {
        var kind = descriptor.Identity.Count > 0 ? descriptor.Identity[0] : 0;
        return kind == DeviceDescriptor.LEGACY_I8042
            ? new DeviceRecord(I8042_ID, KEYBOARD_CONTROLLER, PROVIDER, descriptor)
            : new DeviceRecord(string.Create(CultureInfo.InvariantCulture, $"legacy/{kind}"),
                string.Create(CultureInfo.InvariantCulture, $"LegacyDevice{kind}"), PROVIDER, descriptor);
    }

    #endregion
}
