using System;
using System.Buffers.Binary;

namespace OutWit.OS.Hardware.Usb;

/// <summary>
/// A Transfer Request Block (xHCI 1.2 §4.11, §6.4): sixteen bytes — a 64-bit parameter, a status word and a control word
/// whose bit 0 is the cycle bit and whose bits 15–10 are the TRB's type. Commands, transfers and events are all TRBs.
/// </summary>
/// <param name="Parameter">The first eight bytes: an address, a setup packet or an event's TRB pointer.</param>
/// <param name="Status">The status word: a length, or an event's completion code in bits 31–24.</param>
/// <param name="Control">The control word.</param>
public readonly record struct XhciTrb(ulong Parameter, uint Status, uint Control)
{
    #region Constants

    /// <summary>
    /// Bytes of a TRB.
    /// </summary>
    public const int BYTES = 16;

    /// <summary>
    /// The Normal TRB of a transfer ring.
    /// </summary>
    public const uint NORMAL = 1;

    /// <summary>
    /// The Setup Stage TRB of a control transfer.
    /// </summary>
    public const uint SETUP = 2;

    /// <summary>
    /// The Data Stage TRB of a control transfer.
    /// </summary>
    public const uint DATA = 3;

    /// <summary>
    /// The Status Stage TRB of a control transfer.
    /// </summary>
    public const uint STATUS = 4;

    /// <summary>
    /// The Link TRB, which closes a ring segment.
    /// </summary>
    public const uint LINK = 6;

    /// <summary>
    /// The Enable Slot command.
    /// </summary>
    public const uint ENABLE_SLOT = 9;

    /// <summary>
    /// The Disable Slot command.
    /// </summary>
    public const uint DISABLE_SLOT = 10;

    /// <summary>
    /// The Address Device command.
    /// </summary>
    public const uint ADDRESS_DEVICE = 11;

    /// <summary>
    /// The Configure Endpoint command.
    /// </summary>
    public const uint CONFIGURE_ENDPOINT = 12;

    /// <summary>
    /// The Evaluate Context command.
    /// </summary>
    public const uint EVALUATE_CONTEXT = 13;

    /// <summary>
    /// The Transfer Event.
    /// </summary>
    public const uint TRANSFER_EVENT = 32;

    /// <summary>
    /// The Command Completion Event.
    /// </summary>
    public const uint COMMAND_COMPLETION = 33;

    /// <summary>
    /// The Port Status Change Event.
    /// </summary>
    public const uint PORT_STATUS_CHANGE = 34;

    /// <summary>
    /// The completion code of success.
    /// </summary>
    public const uint SUCCESS = 1;

    /// <summary>
    /// The completion code of a transfer that moved fewer bytes than its TRB named.
    /// </summary>
    public const uint SHORT_PACKET = 13;

    /// <summary>
    /// The control word's cycle bit.
    /// </summary>
    public const uint CYCLE = 1;

    /// <summary>
    /// A Link TRB's Toggle Cycle bit.
    /// </summary>
    public const uint TOGGLE_CYCLE = 1 << 1;

    /// <summary>
    /// Interrupt on Short Packet.
    /// </summary>
    public const uint INTERRUPT_SHORT = 1 << 2;

    /// <summary>
    /// Interrupt On Completion.
    /// </summary>
    public const uint INTERRUPT_ON_COMPLETION = 1 << 5;

    /// <summary>
    /// A Setup Stage TRB's Immediate Data bit: the setup packet is the parameter itself.
    /// </summary>
    public const uint IMMEDIATE_DATA = 1 << 6;

    /// <summary>
    /// A Data or Status Stage TRB's direction bit: IN.
    /// </summary>
    public const uint DIRECTION_IN = 1 << 16;

    #endregion

    #region Functions

    /// <summary>
    /// Reads a TRB.
    /// </summary>
    /// <param name="bytes">At least sixteen bytes.</param>
    /// <returns>The TRB.</returns>
    public static XhciTrb Read(ReadOnlySpan<byte> bytes) => new(BinaryPrimitives.ReadUInt64LittleEndian(bytes),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));

    /// <summary>
    /// Writes the TRB's parameter and status, leaving its control word as it was: the control word, which carries the
    /// cycle bit, goes last.
    /// </summary>
    /// <param name="bytes">At least sixteen bytes.</param>
    public void WriteBody(Span<byte> bytes)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, Parameter);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], Status);
    }

    /// <summary>
    /// A TRB of a type with a control word's other bits.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <param name="parameter">The parameter.</param>
    /// <param name="status">The status word.</param>
    /// <param name="flags">The control word's bits beside the type and the cycle.</param>
    /// <returns>The TRB, its cycle bit clear.</returns>
    public static XhciTrb Of(uint type, ulong parameter = 0, uint status = 0, uint flags = 0) => new(parameter, status, type << 10 | flags);

    #endregion

    #region Properties

    /// <summary>
    /// The TRB's type.
    /// </summary>
    public uint Type => Control >> 10 & 0x3F;

    /// <summary>
    /// The cycle bit.
    /// </summary>
    public bool Cycle => (Control & CYCLE) != 0;

    /// <summary>
    /// An event's completion code.
    /// </summary>
    public uint CompletionCode => Status >> 24;

    /// <summary>
    /// A Transfer Event's residual: the bytes the TRB named that were not moved.
    /// </summary>
    public int Residual => (int)(Status & 0xFFFFFF);

    /// <summary>
    /// An event's or a command's slot.
    /// </summary>
    public int Slot => (int)(Control >> 24);

    /// <summary>
    /// A Transfer Event's endpoint, as its device context index.
    /// </summary>
    public int Endpoint => (int)(Control >> 16 & 0x1F);

    /// <summary>
    /// A Port Status Change Event's port, numbered from 1.
    /// </summary>
    public int Port => (int)(Parameter >> 24 & 0xFF);

    #endregion
}
