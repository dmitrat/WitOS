using System;
using System.Buffers.Binary;
using OutWit.OS.Hardware.E1000;

namespace OutWit.OS.Tests.Hardware.E1000;

/// <summary>
/// The e1000e pieces of plan step H4 on the host: legacy receive and transmit descriptors in the 82574's layout, what a
/// written-back receive descriptor says, and the address the receive address registers and the NVM's words hold.
/// </summary>
[TestFixture]
public sealed class E1000Tests
{
    #region Tests

    [Test]
    public void ReceiveDescriptorNamesItsBufferTest()
    {
        var descriptor = new byte[E1000Descriptors.BYTES];
        descriptor.AsSpan().Fill(0xFF);
        E1000Descriptors.WriteReceive(descriptor, 0x1_2345_6800);
        Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(descriptor), Is.EqualTo(0x1_2345_6800UL), "the buffer's address");
        Assert.That(descriptor.AsSpan(8).ToArray(), Is.All.Zero, "the length, the checksum, the status, the errors and the tag cleared");
        Assert.That(E1000Descriptors.ReadReceive(descriptor).Done, Is.False);
        Assert.Throws<ArgumentOutOfRangeException>(() => E1000Descriptors.WriteReceive(new byte[8], 0));
    }

    [Test]
    public void ReceivedFrameIsUsableWholeAndWithoutErrorsTest()
    {
        var descriptor = new byte[E1000Descriptors.BYTES];
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(8), 342);
        descriptor[12] = E1000Descriptors.RECEIVE_DONE | E1000Descriptors.RECEIVE_END;
        var received = E1000Descriptors.ReadReceive(descriptor);
        Assert.That((received.Length, received.Done, received.Usable), Is.EqualTo((342, true, true)));
        descriptor[13] = 0x20; // a TCP checksum error, which concerns an offload the driver does not ask for
        Assert.That(E1000Descriptors.ReadReceive(descriptor).Usable, Is.True);
        descriptor[13] = 0x01; // a CRC error
        Assert.That(E1000Descriptors.ReadReceive(descriptor).Usable, Is.False);
        descriptor[13] = 0;
        descriptor[12] = E1000Descriptors.RECEIVE_DONE; // not the frame's last descriptor
        Assert.That(E1000Descriptors.ReadReceive(descriptor).Usable, Is.False);
    }

    [Test]
    public void TransmitDescriptorAsksForTheCrcAndAReportTest()
    {
        var descriptor = new byte[E1000Descriptors.BYTES];
        descriptor.AsSpan().Fill(0xFF);
        E1000Descriptors.WriteTransmit(descriptor, 0x8000_1000, 42);
        Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(descriptor), Is.EqualTo(0x8000_1000UL));
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(8)), Is.EqualTo((ushort)42), "the length, the adapter padding a short frame");
        Assert.That((descriptor[10], descriptor[11], descriptor[12], descriptor[13]), Is.EqualTo(((byte)0, (byte)0x0B, (byte)0, (byte)0)),
            "no checksum offload; EOP, IFCS and RS; the status cleared");
        Assert.That(E1000Descriptors.TransmitDone(descriptor), Is.False);
        descriptor[12] = E1000Descriptors.TRANSMIT_DONE;
        Assert.That(E1000Descriptors.TransmitDone(descriptor), Is.True);
        Assert.Throws<ArgumentOutOfRangeException>(() => E1000Descriptors.WriteTransmit(descriptor, 0, 0));
    }

    // QEMU's first adapter, 52:54:00:12:34:56, as RAL/RAH hold it after the reset and as the NVM's words 0–2 do.
    [Test]
    public void AddressComesFromTheReceiveAddressOrTheNvmTest()
    {
        Assert.That(E1000Adapter.AddressOf(0x12005452U, 0x8000_5634U), Is.EqualTo(0x5634_1200_5452UL));
        Assert.That(E1000Adapter.AddressOf(0x12005452U, 0x0000_5634U), Is.Zero, "RAH without AV holds no address");
        Assert.That(E1000Adapter.AddressOf((ushort)0x5452, (ushort)0x1200, (ushort)0x5634), Is.EqualTo(0x5634_1200_5452UL));
    }

    #endregion
}
