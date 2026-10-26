using OutWit.OS.Resources;

namespace OutWit.OS.Hardware.I8042;

/// <summary>
/// The i8042's ports through DEVICE_PORT (ABI-1 1.7, plan step H5.2): region 0 of the legacy bus's descriptor is the
/// data port, region 1 the status and command port, each one byte.
/// </summary>
/// <param name="device">The controller, with BIND.</param>
public sealed class I8042DevicePorts(KernelHandle device) : II8042Ports
{
    #region Constants

    /// <summary>
    /// The descriptor's region of the data port, 0x60.
    /// </summary>
    public const int DATA_REGION = 0;

    /// <summary>
    /// The descriptor's region of the status and command port, 0x64.
    /// </summary>
    public const int STATUS_REGION = 1;

    #endregion

    #region Functions

    /// <inheritdoc/>
    public byte ReadStatus() => Device.PortRead8(device, STATUS_REGION, 0);

    /// <inheritdoc/>
    public byte ReadData() => Device.PortRead8(device, DATA_REGION, 0);

    /// <inheritdoc/>
    public void WriteCommand(byte command) => Device.PortWrite8(device, STATUS_REGION, 0, command);

    /// <inheritdoc/>
    public void WriteData(byte value) => Device.PortWrite8(device, DATA_REGION, 0, value);

    #endregion
}
