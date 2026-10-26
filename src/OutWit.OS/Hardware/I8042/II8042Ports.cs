namespace OutWit.OS.Hardware.I8042;

/// <summary>
/// The i8042's two ports (plan step H5.2): the data port at 0x60 and the status and command port at 0x64, which a driver
/// reaches through DEVICE_PORT and a host test through a model of the controller.
/// </summary>
public interface II8042Ports
{
    /// <summary>
    /// Reads the status register.
    /// </summary>
    /// <returns>The status byte.</returns>
    byte ReadStatus();

    /// <summary>
    /// Reads the output buffer: a byte of a device or an answer of the controller.
    /// </summary>
    /// <returns>The byte.</returns>
    byte ReadData();

    /// <summary>
    /// Writes a command of the controller.
    /// </summary>
    /// <param name="command">The command byte.</param>
    void WriteCommand(byte command);

    /// <summary>
    /// Writes the input buffer: a command's argument, or a byte for the keyboard.
    /// </summary>
    /// <param name="value">The byte.</param>
    void WriteData(byte value);
}
