using System.Collections.Generic;
using System.IO;

namespace OutWit.OS.Hardware.I8042;

/// <summary>
/// The PC's i8042 keyboard controller (plan step H5.2, ADR 0031), taken as the firmware left it: no reset, since the
/// firmware's keyboard works and VMware drops every key once a guest disables its scanning or its port. Its output
/// buffer is flushed; its configuration gets the keyboard's interrupt, the translation to scan code set 1 and the
/// keyboard's clock, while the auxiliary device's interrupt and clock stay off; the keyboard's port is enabled and its
/// scanning asked for again. Each byte the keyboard delivers afterwards becomes a key event through
/// <see cref="ScanCodeSet1"/>; a byte of the auxiliary device is read and left. A status of 0xFF is no controller.
/// </summary>
public sealed class I8042Controller
{
    #region Constants

    /// <summary>
    /// The status register's bit of a byte waiting in the output buffer.
    /// </summary>
    public const byte STATUS_OUTPUT_FULL = 0x01;

    /// <summary>
    /// The status register's bit of a byte the controller has not taken from the input buffer.
    /// </summary>
    public const byte STATUS_INPUT_FULL = 0x02;

    /// <summary>
    /// The status register's bit of an output byte from the auxiliary device.
    /// </summary>
    public const byte STATUS_AUXILIARY = 0x20;

    /// <summary>
    /// The configuration byte's keyboard interrupt.
    /// </summary>
    public const byte CONFIGURATION_KEYBOARD_INTERRUPT = 0x01;

    /// <summary>
    /// The configuration byte's auxiliary device interrupt.
    /// </summary>
    public const byte CONFIGURATION_AUXILIARY_INTERRUPT = 0x02;

    /// <summary>
    /// The configuration byte's keyboard clock disable.
    /// </summary>
    public const byte CONFIGURATION_KEYBOARD_CLOCK_OFF = 0x10;

    /// <summary>
    /// The configuration byte's auxiliary device clock disable.
    /// </summary>
    public const byte CONFIGURATION_AUXILIARY_CLOCK_OFF = 0x20;

    /// <summary>
    /// The configuration byte's translation of the keyboard's codes to set 1.
    /// </summary>
    public const byte CONFIGURATION_TRANSLATION = 0x40;

    /// <summary>
    /// The command that reads the configuration byte.
    /// </summary>
    public const byte COMMAND_READ_CONFIGURATION = 0x20;

    /// <summary>
    /// The command that writes the configuration byte.
    /// </summary>
    public const byte COMMAND_WRITE_CONFIGURATION = 0x60;

    /// <summary>
    /// The command that enables the keyboard's port.
    /// </summary>
    public const byte COMMAND_ENABLE_KEYBOARD = 0xAE;

    /// <summary>
    /// The keyboard's command that enables its scanning.
    /// </summary>
    public const byte KEYBOARD_ENABLE_SCANNING = 0xF4;

    /// <summary>
    /// The keyboard's acknowledgement of a command.
    /// </summary>
    public const byte KEYBOARD_ACKNOWLEDGE = 0xFA;

    private const byte ABSENT = 0xFF;

    private const int POLL_LIMIT = 100000;

    private const int FLUSH_LIMIT = 32;

    #endregion

    #region Fields

    private readonly II8042Ports m_ports;

    private readonly ScanCodeSet1 m_codes = new();

    #endregion

    #region Constructors

    private I8042Controller(II8042Ports ports)
    {
        m_ports = ports;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Takes the controller and its keyboard.
    /// </summary>
    /// <param name="ports">The controller's ports.</param>
    /// <returns>The controller, its keyboard's interrupt enabled.</returns>
    /// <exception cref="IOException">No controller answers, or it does not take or answer a command.</exception>
    public static I8042Controller Open(II8042Ports ports)
    {
        var controller = new I8042Controller(ports);
        if (ports.ReadStatus() == ABSENT)
            throw new IOException("No i8042 answers: its status reads 0xFF.");
        controller.Flushed = controller.Flush();
        controller.Command(COMMAND_READ_CONFIGURATION);
        controller.FirmwareConfiguration = controller.ReadAnswer("the configuration byte");
        var configuration = (byte)((controller.FirmwareConfiguration | CONFIGURATION_KEYBOARD_INTERRUPT | CONFIGURATION_TRANSLATION |
            CONFIGURATION_AUXILIARY_CLOCK_OFF) & ~(CONFIGURATION_AUXILIARY_INTERRUPT | CONFIGURATION_KEYBOARD_CLOCK_OFF));
        controller.Command(COMMAND_WRITE_CONFIGURATION);
        controller.Data(configuration);
        controller.Configuration = configuration;
        controller.Command(COMMAND_ENABLE_KEYBOARD);
        controller.Data(KEYBOARD_ENABLE_SCANNING);
        controller.ScanningAcknowledged = controller.AwaitAcknowledgement();
        return controller;
    }

    /// <summary>
    /// Reads every byte waiting in the output buffer, as the keyboard's interrupt asks.
    /// </summary>
    /// <returns>The key events of the keyboard's bytes.</returns>
    public IReadOnlyList<(ushort Code, int Value)> Service()
    {
        var events = new List<(ushort Code, int Value)>();
        for (var i = 0; i < FLUSH_LIMIT; ++i)
        {
            var status = m_ports.ReadStatus();
            if ((status & STATUS_OUTPUT_FULL) == 0)
                break;
            var value = m_ports.ReadData();
            if ((status & STATUS_AUXILIARY) != 0)
                continue; // the auxiliary device's: its interrupt is off, and the console has no pointer
            if (m_codes.Take(value) is { } key)
                events.Add(key);
        }
        return events;
    }

    // Reads and leaves what the output buffer holds; the count of bytes left.
    private int Flush()
    {
        var count = 0;
        while (count < FLUSH_LIMIT && (m_ports.ReadStatus() & STATUS_OUTPUT_FULL) != 0)
        {
            m_ports.ReadData();
            ++count;
        }
        return count;
    }

    private void AwaitInputEmpty(string what)
    {
        for (var i = 0; i < POLL_LIMIT; ++i)
        {
            if ((m_ports.ReadStatus() & STATUS_INPUT_FULL) == 0)
                return;
        }
        throw new IOException($"The i8042 does not take {what}.");
    }

    private void Command(byte command)
    {
        AwaitInputEmpty($"the command 0x{command:X2}");
        m_ports.WriteCommand(command);
    }

    private void Data(byte value)
    {
        AwaitInputEmpty($"the byte 0x{value:X2}");
        m_ports.WriteData(value);
    }

    private byte ReadAnswer(string what)
    {
        for (var i = 0; i < POLL_LIMIT; ++i)
        {
            if ((m_ports.ReadStatus() & STATUS_OUTPUT_FULL) != 0)
                return m_ports.ReadData();
        }
        throw new IOException($"The i8042 does not answer with {what}.");
    }

    // The keyboard's acknowledgement of its command; a key typed meanwhile is taken as a key.
    private bool AwaitAcknowledgement()
    {
        for (var i = 0; i < POLL_LIMIT; ++i)
        {
            var status = m_ports.ReadStatus();
            if ((status & STATUS_OUTPUT_FULL) == 0)
                continue;
            var value = m_ports.ReadData();
            if ((status & STATUS_AUXILIARY) == 0 && value == KEYBOARD_ACKNOWLEDGE)
                return true;
        }
        return false;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The configuration byte as the firmware left it.
    /// </summary>
    public byte FirmwareConfiguration { get; private set; }

    /// <summary>
    /// The configuration byte the controller works with.
    /// </summary>
    public byte Configuration { get; private set; }

    /// <summary>
    /// The bytes the output buffer held when the controller was taken.
    /// </summary>
    public int Flushed { get; private set; }

    /// <summary>
    /// Whether the keyboard acknowledged the request to scan.
    /// </summary>
    public bool ScanningAcknowledged { get; private set; }

    #endregion
}
