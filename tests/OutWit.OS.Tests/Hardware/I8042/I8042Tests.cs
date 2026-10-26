using System;
using System.Collections.Generic;
using System.IO;
using OutWit.OS.Hardware.I8042;

namespace OutWit.OS.Tests.Hardware.I8042;

/// <summary>
/// The PS/2 keyboard of plan step H5.2 on the host: set 1's codes as evdev's key events — the main block, the enhanced
/// keys behind 0xE0 without their fake shifts, Pause, typematic repeats, releases of keys never pressed and the
/// controller's answers — and the controller taken as the firmware left it, against a model that answers as QEMU's and
/// VMware's i8042 do.
/// </summary>
[TestFixture]
public sealed class I8042Tests
{
    #region Constants

    private const ushort KEY_A = 30;
    private const ushort KEY_LEFTSHIFT = 42;
    private const ushort KEY_UP = 103;
    private const ushort KEY_RIGHTCTRL = 97;
    private const ushort KEY_F12 = 88;
    private const ushort KEY_PAUSE = 119;

    #endregion

    #region Tests

    [Test]
    public void MainBlockCodesAreEvdevCodesTest()
    {
        var codes = new ScanCodeSet1();
        Assert.That(codes.Take(0x1E), Is.EqualTo(((ushort)KEY_A, ScanCodeSet1.PRESSED)));
        Assert.That(codes.Take(0x9E), Is.EqualTo(((ushort)KEY_A, ScanCodeSet1.RELEASED)));
        Assert.That(codes.Take(0x01), Is.EqualTo(((ushort)1, ScanCodeSet1.PRESSED)), "Esc");
        Assert.That(codes.Take(0x58), Is.EqualTo(((ushort)KEY_F12, ScanCodeSet1.PRESSED)));
    }

    [Test]
    public void EnhancedKeysFollowTheirPrefixTest()
    {
        var codes = new ScanCodeSet1();
        Assert.That(codes.Take(0xE0), Is.Null);
        Assert.That(codes.Take(0x48), Is.EqualTo(((ushort)KEY_UP, ScanCodeSet1.PRESSED)));
        Assert.That(codes.Take(0xE0), Is.Null);
        Assert.That(codes.Take(0xC8), Is.EqualTo(((ushort)KEY_UP, ScanCodeSet1.RELEASED)));
        Assert.That(codes.Take(0xE0), Is.Null);
        Assert.That(codes.Take(0x1D), Is.EqualTo(((ushort)KEY_RIGHTCTRL, ScanCodeSet1.PRESSED)));
        Assert.That(codes.Take(0x48), Is.EqualTo(((ushort)72, ScanCodeSet1.PRESSED)), "without its prefix, the keypad's 8");
    }

    [Test]
    public void FakeShiftsAndAnswersGiveNothingTest()
    {
        var codes = new ScanCodeSet1();
        // Print Screen as a keyboard sends it: a fake shift around the key.
        Assert.That(codes.Take(0xE0), Is.Null);
        Assert.That(codes.Take(0x2A), Is.Null, "the fake shift");
        Assert.That(codes.Take(0xE0), Is.Null);
        Assert.That(codes.Take(0x37), Is.EqualTo(((ushort)99, ScanCodeSet1.PRESSED)), "KEY_SYSRQ");
        foreach (var answer in new byte[] { 0xFA, 0xFE, 0xEE, 0x00, 0xFF })
            Assert.That(codes.Take(answer), Is.Null, $"0x{answer:X2}");
        Assert.That(codes.Take(0xAA), Is.Null, "the release of a shift never pressed, as a keyboard's self-test answer reads");
    }

    [Test]
    public void PauseIsAPressAndAReleaseTest()
    {
        var codes = new ScanCodeSet1();
        Assert.That(codes.Take(0xE1), Is.EqualTo(((ushort)KEY_PAUSE, ScanCodeSet1.PRESSED)));
        foreach (var rest in new byte[] { 0x1D, 0x45, 0xE1, 0x9D })
            Assert.That(codes.Take(rest), Is.Null);
        Assert.That(codes.Take(0xC5), Is.EqualTo(((ushort)KEY_PAUSE, ScanCodeSet1.RELEASED)), "the sequence's last byte");
        Assert.That(codes.Take(0x1E), Is.EqualTo(((ushort)KEY_A, ScanCodeSet1.PRESSED)), "the next key after the sequence");
    }

    [Test]
    public void HeldKeyRepeatsAndReleasesOnceTest()
    {
        var codes = new ScanCodeSet1();
        Assert.That(codes.Take(0x2A), Is.EqualTo(((ushort)KEY_LEFTSHIFT, ScanCodeSet1.PRESSED)));
        Assert.That(codes.Take(0x2A), Is.EqualTo(((ushort)KEY_LEFTSHIFT, ScanCodeSet1.REPEATED)));
        Assert.That(codes.ReleaseAll(), Is.EqualTo(new[] { ((ushort)KEY_LEFTSHIFT, ScanCodeSet1.RELEASED) }));
        Assert.That(codes.Take(0xAA), Is.Null, "released already");
    }

    [Test]
    public void ControllerIsTakenAsTheFirmwareLeftItTest()
    {
        // OVMF's on q35, as the guest measured it: both interrupts, the system flag, the auxiliary clock off, translation.
        var model = new ControllerModel(configuration: 0x67);
        model.Output.Enqueue(0x1C); // a byte the firmware left
        var controller = I8042Controller.Open(model);
        Assert.That(controller.Flushed, Is.EqualTo(1));
        Assert.That(controller.FirmwareConfiguration, Is.EqualTo(0x67));
        Assert.That(controller.Configuration, Is.EqualTo(0x65), "the auxiliary device's interrupt off");
        Assert.That(model.Configuration, Is.EqualTo(controller.Configuration));
        Assert.That(model.Commands, Is.EqualTo(new byte[]
        {
            I8042Controller.COMMAND_READ_CONFIGURATION, I8042Controller.COMMAND_WRITE_CONFIGURATION, I8042Controller.COMMAND_ENABLE_KEYBOARD
        }), "no reset, no disabled port");
        Assert.That(model.KeyboardBytes, Is.EqualTo(new[] { I8042Controller.KEYBOARD_ENABLE_SCANNING }));
        Assert.That(controller.ScanningAcknowledged, Is.True);
    }

    [Test]
    public void ControllerTurnsTranslationOnTest()
    {
        var model = new ControllerModel(configuration: 0x10); // translation off, the keyboard's clock off
        var controller = I8042Controller.Open(model);
        Assert.That(controller.Configuration & I8042Controller.CONFIGURATION_TRANSLATION, Is.Not.Zero);
        Assert.That(controller.Configuration & I8042Controller.CONFIGURATION_KEYBOARD_CLOCK_OFF, Is.Zero);
        Assert.That(controller.Configuration & I8042Controller.CONFIGURATION_KEYBOARD_INTERRUPT, Is.Not.Zero);
        Assert.That(controller.Configuration & I8042Controller.CONFIGURATION_AUXILIARY_CLOCK_OFF, Is.Not.Zero);
    }

    [Test]
    public void ServiceReadsKeysAndLeavesAuxiliaryBytesTest()
    {
        var model = new ControllerModel(configuration: 0x45);
        var controller = I8042Controller.Open(model);
        model.Output.Enqueue(0x1E);
        model.Auxiliary.Enqueue(0x08); // a mouse's byte between
        model.Output.Enqueue(0x9E);
        Assert.That(controller.Service(), Is.EqualTo(new[] { ((ushort)KEY_A, ScanCodeSet1.PRESSED), ((ushort)KEY_A, ScanCodeSet1.RELEASED) }));
        Assert.That(model.Output, Is.Empty);
        Assert.That(model.Auxiliary, Is.Empty);
    }

    [Test]
    public void AbsentControllerIsRefusedTest()
    {
        Assert.Throws<IOException>(() => I8042Controller.Open(new ControllerModel(configuration: 0x45) { Absent = true }));
    }

    #endregion

    #region Model

    // An i8042 as QEMU's and VMware's answer: its configuration byte through 0x20 and 0x60, the keyboard's port enabled by
    // 0xAE, a byte for the keyboard acknowledged with 0xFA, and the keyboard's and the auxiliary device's bytes in turn.
    private sealed class ControllerModel(byte configuration) : II8042Ports
    {
        private byte? m_pending;

        public byte Configuration { get; private set; } = configuration;

        public Queue<byte> Output { get; } = new();

        public Queue<byte> Auxiliary { get; } = new();

        public List<byte> Commands { get; } = [];

        public List<byte> KeyboardBytes { get; } = [];

        public bool Absent { get; init; }

        public byte ReadStatus()
        {
            if (Absent)
                return 0xFF;
            if (Output.Count > 0)
                return I8042Controller.STATUS_OUTPUT_FULL;
            return Auxiliary.Count > 0 ? (byte)(I8042Controller.STATUS_OUTPUT_FULL | I8042Controller.STATUS_AUXILIARY) : (byte)0;
        }

        public byte ReadData() => Output.Count > 0 ? Output.Dequeue() : Auxiliary.Count > 0 ? Auxiliary.Dequeue() : (byte)0;

        public void WriteCommand(byte command)
        {
            Commands.Add(command);
            if (command == I8042Controller.COMMAND_READ_CONFIGURATION)
                Output.Enqueue(Configuration);
            m_pending = command;
        }

        public void WriteData(byte value)
        {
            if (m_pending == I8042Controller.COMMAND_WRITE_CONFIGURATION)
            {
                Configuration = value;
                m_pending = null;
                return;
            }
            KeyboardBytes.Add(value);
            Output.Enqueue(I8042Controller.KEYBOARD_ACKNOWLEDGE);
        }
    }

    #endregion
}
