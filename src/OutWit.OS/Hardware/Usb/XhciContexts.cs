using System;
using System.Buffers.Binary;
using System.Numerics;

namespace OutWit.OS.Hardware.Usb;

/// <summary>
/// The contexts a driver gives the controller (xHCI 1.2 §6.2): an Input Context is the Input Control Context, then the
/// Slot Context, then the Endpoint Contexts by their Device Context Index from 1; each context is 32 bytes, or 64 where
/// the controller's HCCPARAMS1 sets CSZ. The controller copies what the Input Control Context adds into the device's
/// Output Context.
/// </summary>
public static class XhciContexts
{
    #region Constants

    /// <summary>
    /// The Endpoint Context's type of a control endpoint.
    /// </summary>
    public const int CONTROL = 4;

    /// <summary>
    /// The Endpoint Context's type of an interrupt IN endpoint.
    /// </summary>
    public const int INTERRUPT_IN = 7;

    /// <summary>
    /// The Endpoint Context's type of a bulk OUT endpoint (E3).
    /// </summary>
    public const int BULK_OUT = 2;

    /// <summary>
    /// The Endpoint Context's type of a bulk IN endpoint (E3).
    /// </summary>
    public const int BULK_IN = 6;

    /// <summary>
    /// The contexts of an Input Context: the control, the slot and 31 endpoints.
    /// </summary>
    public const int INPUT_CONTEXTS = 33;

    /// <summary>
    /// The contexts of a device's Output Context: the slot and 31 endpoints.
    /// </summary>
    public const int DEVICE_CONTEXTS = 32;

    /// <summary>
    /// PORTSC's speed of a full-speed device.
    /// </summary>
    public const int FULL_SPEED = 1;

    /// <summary>
    /// PORTSC's speed of a low-speed device.
    /// </summary>
    public const int LOW_SPEED = 2;

    /// <summary>
    /// PORTSC's speed of a high-speed device.
    /// </summary>
    public const int HIGH_SPEED = 3;

    /// <summary>
    /// PORTSC's speed of a SuperSpeed device.
    /// </summary>
    public const int SUPER_SPEED = 4;

    /// <summary>
    /// An Endpoint Context's state of an endpoint that takes transfers.
    /// </summary>
    public const int RUNNING = 1;

    /// <summary>
    /// An Endpoint Context's state after a STALL, a babble or a transaction error: Reset Endpoint takes it to Stopped.
    /// </summary>
    public const int HALTED = 2;

    /// <summary>
    /// An Endpoint Context's state in which Set TR Dequeue Pointer may move its ring.
    /// </summary>
    public const int STOPPED = 3;

    /// <summary>
    /// The Slot Context's Hub flag.
    /// </summary>
    private const uint HUB = 1 << 26;

    #endregion

    #region Functions

    /// <summary>
    /// Writes the Input Control Context: the contexts to drop and to add, bit n for Device Context Index n (bit 0 the slot).
    /// </summary>
    /// <param name="input">The Input Context.</param>
    /// <param name="contextBytes">32 or 64.</param>
    /// <param name="drop">The drop flags.</param>
    /// <param name="add">The add flags.</param>
    public static void WriteControl(Span<byte> input, int contextBytes, uint drop, uint add)
    {
        var control = input[..contextBytes];
        control.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(control, drop);
        BinaryPrimitives.WriteUInt32LittleEndian(control[4..], add);
    }

    /// <summary>
    /// Writes the Slot Context of a device (xHCI 1.2 §6.2.2): its route string, speed and context entries; its root hub
    /// port and, for a hub, its ports; the Transaction Translator it is reached through and, for a high-speed hub, its own
    /// TT Think Time. The Hub fields take effect through Address Device or Configure Endpoint with the slot's add flag.
    /// </summary>
    /// <param name="input">The Input Context.</param>
    /// <param name="contextBytes">32 or 64.</param>
    /// <param name="location">Where the device sits.</param>
    /// <param name="contextEntries">The index of the last valid Endpoint Context.</param>
    /// <param name="hub">The device's hub descriptor when it is a hub, otherwise null.</param>
    public static void WriteSlot(Span<byte> input, int contextBytes, UsbLocation location, int contextEntries, UsbHubDescriptor? hub = null)
    {
        var slot = input.Slice(contextBytes, contextBytes);
        slot.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(slot, location.Route & 0xFFFFF | (uint)location.Speed << 20 | (hub is null ? 0 : HUB) |
            (uint)contextEntries << 27);
        BinaryPrimitives.WriteUInt32LittleEndian(slot[4..], (uint)location.RootPort << 16 | (uint)(hub?.Ports ?? 0) << 24);
        var thinkTime = hub is not null && location.Speed == HIGH_SPEED ? hub.ThinkTime : 0;
        BinaryPrimitives.WriteUInt32LittleEndian(slot[8..], (uint)(location.TtSlot | location.TtPort << 8 | thinkTime << 16));
    }

    /// <summary>
    /// Reads an Endpoint Context's state from a device's Output Context (xHCI 1.2 §6.2.3): <see cref="RUNNING"/>,
    /// <see cref="HALTED"/>, <see cref="STOPPED"/> or another.
    /// </summary>
    /// <param name="output">The Output Context.</param>
    /// <param name="contextBytes">32 or 64.</param>
    /// <param name="index">The endpoint's Device Context Index.</param>
    /// <returns>The state.</returns>
    public static int EndpointState(ReadOnlySpan<byte> output, int contextBytes, int index) =>
        (int)(BinaryPrimitives.ReadUInt32LittleEndian(output[(contextBytes * index)..]) & 7);

    /// <summary>
    /// Writes an Endpoint Context.
    /// </summary>
    /// <param name="input">The Input Context.</param>
    /// <param name="contextBytes">32 or 64.</param>
    /// <param name="index">The endpoint's Device Context Index, 1 for the default control endpoint.</param>
    /// <param name="type">Its type: <see cref="CONTROL"/>, <see cref="INTERRUPT_IN"/>, <see cref="BULK_IN"/> or <see cref="BULK_OUT"/>.</param>
    /// <param name="maxPacket">Its packet's bytes.</param>
    /// <param name="interval">The exponent of its service interval in 125 µs units, see <see cref="Interval"/>.</param>
    /// <param name="maxBurst">The packets of a burst less one.</param>
    /// <param name="dequeue">The physical address of its transfer ring.</param>
    /// <param name="averageTrb">The average bytes of its TRBs.</param>
    public static void WriteEndpoint(Span<byte> input, int contextBytes, int index, int type, int maxPacket, int interval, int maxBurst,
        ulong dequeue, int averageTrb)
    {
        var endpoint = input.Slice(contextBytes * (1 + index), contextBytes);
        endpoint.Clear();
        var maxEsitPayload = type is CONTROL or BULK_IN or BULK_OUT ? 0 : maxPacket * (maxBurst + 1); // periodic endpoints' alone
        BinaryPrimitives.WriteUInt32LittleEndian(endpoint, (uint)interval << 16);
        BinaryPrimitives.WriteUInt32LittleEndian(endpoint[4..], (uint)(3 << 1 | type << 3 | maxBurst << 8 | maxPacket << 16));
        BinaryPrimitives.WriteUInt64LittleEndian(endpoint[8..], dequeue | 1); // the Dequeue Cycle State of a new ring
        BinaryPrimitives.WriteUInt32LittleEndian(endpoint[16..], (uint)(averageTrb | maxEsitPayload << 16));
    }

    /// <summary>
    /// The Endpoint Context's interval of an interrupt endpoint (xHCI 1.2 §6.2.3.6): bInterval counts frames of 1 ms at
    /// full and low speed, where the context takes the largest power of two of 125 µs units within it (3 to 10), and is
    /// already an exponent at high speed and SuperSpeed, less one (0 to 15).
    /// </summary>
    /// <param name="speed">The device's speed.</param>
    /// <param name="interval">The endpoint descriptor's bInterval.</param>
    /// <returns>The exponent.</returns>
    public static int Interval(int speed, int interval) => speed is FULL_SPEED or LOW_SPEED
        ? Math.Clamp(BitOperations.Log2((uint)Math.Max(interval, 1) * 8), 3, 10)
        : Math.Clamp(interval - 1, 0, 15);

    /// <summary>
    /// The default control endpoint's packet bytes before the device descriptor names them: 8 at full and low speed,
    /// 64 at high speed, 512 at SuperSpeed.
    /// </summary>
    /// <param name="speed">The port's speed.</param>
    /// <returns>The bytes.</returns>
    public static int DefaultMaxPacket(int speed) => speed switch
    {
        HIGH_SPEED => 64,
        >= SUPER_SPEED => 512,
        _ => 8,
    };

    /// <summary>
    /// An endpoint's Device Context Index: twice its number, plus one for IN.
    /// </summary>
    /// <param name="endpoint">The endpoint.</param>
    /// <returns>The index.</returns>
    public static int Index(UsbEndpoint endpoint) => endpoint.Number * 2 + (endpoint.IsIn ? 1 : 0);

    #endregion
}
