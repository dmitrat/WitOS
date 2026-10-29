using System;
using System.Collections.Generic;

namespace OutWit.OS.Hardware.Usb;

/// <summary>
/// What the USB core (<see cref="UsbBus"/>, plan step H2.2) asks of a host controller: its root hub's ports, a device
/// addressed where it sits, control transfers on its default endpoint, interrupt IN endpoints and their transfers, a
/// halted endpoint taken back, a device let go. A device is its slot. Completions reach the core from the controller's
/// events through whoever serves them, as <see cref="UsbBus.Completed"/> and <see cref="UsbBus.RootPortChanged"/>.
/// </summary>
public interface IUsbHost
{
    /// <summary>
    /// The root hub's ports, by number.
    /// </summary>
    IReadOnlyList<UsbRootPort> RootPorts { get; }

    /// <summary>
    /// A root port's state.
    /// </summary>
    /// <param name="port">The port, from 1.</param>
    /// <returns>The state.</returns>
    UsbRootPortState ReadRootPort(int port);

    /// <summary>
    /// Clears a root port's change bits, so that its next change is reported again.
    /// </summary>
    /// <param name="port">The port, from 1.</param>
    void AcknowledgeRootPort(int port);

    /// <summary>
    /// Readies the device on a root port: a USB 2 port reset and its recovery, a USB 3 port's link awaited.
    /// </summary>
    /// <param name="port">The port, from 1.</param>
    /// <returns>The port's state after it.</returns>
    /// <exception cref="System.IO.IOException">The port did not end its reset.</exception>
    UsbRootPortState ResetRootPort(int port);

    /// <summary>
    /// Addresses the device at a location, whose port is enabled: a slot, its default control endpoint for the speed,
    /// the device's address, its descriptor's bMaxPacketSize0 taken.
    /// </summary>
    /// <param name="location">Where the device sits.</param>
    /// <returns>Its slot.</returns>
    /// <exception cref="System.IO.IOException">A command failed; nothing is left enabled.</exception>
    int Address(UsbLocation location);

    /// <summary>
    /// The packet bytes of a device's default control endpoint.
    /// </summary>
    /// <param name="slot">The device.</param>
    /// <returns>The bytes.</returns>
    int MaxPacket0(int slot);

    /// <summary>
    /// A control transfer on a device's default endpoint. A transfer that halts the endpoint — a STALL, a babble, a
    /// transaction error — leaves the endpoint taking transfers again before it throws.
    /// </summary>
    /// <param name="slot">The device.</param>
    /// <param name="setup">The request.</param>
    /// <returns>For an IN request, wLength bytes as the data stage left them (zeros beyond a short one); none otherwise.</returns>
    /// <exception cref="System.IO.IOException">The transfer failed or timed out.</exception>
    byte[] Control(int slot, UsbSetup setup);

    /// <summary>
    /// Configures an interrupt IN endpoint of a device with a ring and a buffer of its own.
    /// </summary>
    /// <param name="slot">The device.</param>
    /// <param name="endpoint">The endpoint.</param>
    /// <returns>Its Device Context Index, which its completions name.</returns>
    /// <exception cref="System.IO.IOException">The command failed.</exception>
    int ConfigureInterruptIn(int slot, UsbEndpoint endpoint);

    /// <summary>
    /// Tells the controller that a device is a hub: its ports, and its TT Think Time at high speed.
    /// </summary>
    /// <param name="slot">The hub.</param>
    /// <param name="hub">Its hub descriptor.</param>
    /// <exception cref="System.IO.IOException">The command failed.</exception>
    void ConfigureHub(int slot, UsbHubDescriptor hub);

    /// <summary>
    /// Queues a transfer on an interrupt IN endpoint into its buffer; its completion names the slot and the endpoint.
    /// </summary>
    /// <param name="slot">The device.</param>
    /// <param name="index">The endpoint's Device Context Index.</param>
    /// <param name="length">The bytes asked for, at most <see cref="UsbBus.REPORT_BUFFER_BYTES"/>.</param>
    void Receive(int slot, int index, int length);

    /// <summary>
    /// An interrupt IN endpoint's buffer, as its last completed transfer left it.
    /// </summary>
    /// <param name="slot">The device.</param>
    /// <param name="index">The endpoint's Device Context Index.</param>
    /// <returns>The buffer's bytes.</returns>
    ReadOnlySpan<byte> Received(int slot, int index);

    /// <summary>
    /// Configures a mass storage interface's bulk IN and OUT endpoints (E3), each with a ring and a page of its own.
    /// </summary>
    /// <param name="slot">The device.</param>
    /// <param name="input">The bulk IN endpoint.</param>
    /// <param name="output">The bulk OUT endpoint.</param>
    /// <returns>Their Device Context Indexes.</returns>
    /// <exception cref="System.IO.IOException">The command failed.</exception>
    (int In, int Out) ConfigureBulk(int slot, UsbEndpoint input, UsbEndpoint output);

    /// <summary>
    /// One bulk transfer through physical segments, waited for (E3): done, perhaps short; stalled, the endpoint taking
    /// transfers again on the controller's side while the device's halt is the caller's to clear; or failed.
    /// </summary>
    /// <param name="slot">The device.</param>
    /// <param name="index">The endpoint's Device Context Index.</param>
    /// <param name="segments">The physical segments.</param>
    /// <param name="transferred">The bytes moved.</param>
    /// <returns>The transfer's result.</returns>
    BulkResult Bulk(int slot, int index, ReadOnlySpan<(ulong Address, int Length)> segments, out int transferred);

    /// <summary>
    /// A bulk endpoint's page, for the small transfers its transport makes through it.
    /// </summary>
    /// <param name="slot">The device.</param>
    /// <param name="index">The endpoint's Device Context Index.</param>
    /// <param name="physical">The page's physical address.</param>
    /// <returns>The page's bytes.</returns>
    Span<byte> BulkBuffer(int slot, int index, out ulong physical);

    /// <summary>
    /// Takes an endpoint whose transfer failed back to taking transfers: a halted one reset, a running one stopped, its
    /// ring's dequeue moved past every transfer it still holds.
    /// </summary>
    /// <param name="slot">The device.</param>
    /// <param name="index">The endpoint's Device Context Index.</param>
    /// <returns>Whether the endpoint takes transfers again.</returns>
    bool RecoverEndpoint(int slot, int index);

    /// <summary>
    /// Lets a device go: its slot disabled, its structures free for another, its completions not yet taken dropped.
    /// </summary>
    /// <param name="slot">The device.</param>
    /// <exception cref="System.IO.IOException">The command failed; the slot is dropped all the same.</exception>
    void Disable(int slot);

    /// <summary>
    /// Waits, as the USB specification's timings ask.
    /// </summary>
    /// <param name="milliseconds">The time.</param>
    void Delay(int milliseconds);
}

/// <summary>
/// A root hub port.
/// </summary>
/// <param name="Number">The port, from 1.</param>
/// <param name="Major">Its protocol's major revision: 2 or 3.</param>
/// <param name="Minor">Its protocol's minor revision, binary-coded decimal.</param>
public sealed record UsbRootPort(int Number, int Major, int Minor)
{
    /// <summary>
    /// The revision as USB names it: 2.0, 3.1, 3.2.
    /// </summary>
    public string Revision => (Minor & 0xF) != 0 ? $"{Major}.{Minor >> 4}{Minor & 0xF}" : $"{Major}.{Minor >> 4}";
}

/// <summary>
/// A root port's state.
/// </summary>
/// <param name="Connected">A device is connected.</param>
/// <param name="Enabled">The port is enabled.</param>
/// <param name="Speed">The device's speed, as PORTSC names it.</param>
/// <param name="ConnectionChanged">The connection changed since the last acknowledgement.</param>
/// <param name="EnableChanged">The port's enable changed since the last acknowledgement: an error disabled it.</param>
public readonly record struct UsbRootPortState(bool Connected, bool Enabled, int Speed, bool ConnectionChanged, bool EnableChanged);
