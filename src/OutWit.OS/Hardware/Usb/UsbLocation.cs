using System;
using System.Text;

namespace OutWit.OS.Hardware.Usb;

/// <summary>
/// Where a device sits in the tree of hubs below a root hub port (USB 2.0 §11.14, xHCI 1.2 §4.5.2 and §6.2.2, plan step
/// H2.2): the root port, the route string of the hubs' ports below it — four bits a tier, the first tier lowest, so at
/// most five tiers of ports up to 15 — and the Transaction Translator through which a full- or low-speed device behind a
/// high-speed hub is reached: the slot of the nearest high-speed hub above it and that hub's port. The driver keeps a
/// hub of several Transaction Translators on its single one (alternate setting 0), so the Slot Context's MTT stays clear.
/// </summary>
/// <param name="RootPort">The root hub port, from 1.</param>
/// <param name="Route">The route string.</param>
/// <param name="Depth">The tiers of hubs between the root hub and the device: 0 on a root port.</param>
/// <param name="Speed">The device's speed, as PORTSC and a Slot Context name it.</param>
/// <param name="TtSlot">The slot of the high-speed hub whose Transaction Translator serves the device, zero for none.</param>
/// <param name="TtPort">That hub's port the device's branch hangs on, zero for none.</param>
public sealed record UsbLocation(int RootPort, uint Route, int Depth, int Speed, int TtSlot, int TtPort)
{
    #region Functions

    /// <summary>
    /// The location of a device on a root hub port, which reaches a full- or low-speed device without a hub's
    /// Transaction Translator.
    /// </summary>
    /// <param name="port">The root port, from 1.</param>
    /// <param name="speed">The device's speed.</param>
    /// <returns>The location.</returns>
    public static UsbLocation Root(int port, int speed) => new(port, 0, 0, speed, 0, 0);

    /// <summary>
    /// The location of a device on a hub's port: the hub's route with the port as the next tier, and the Transaction
    /// Translator of the hub itself when it runs at high speed and the device does not, else the one the hub is reached
    /// through.
    /// </summary>
    /// <param name="hub">The hub's location.</param>
    /// <param name="hubSlot">The hub's slot.</param>
    /// <param name="port">The hub's port, from 1.</param>
    /// <param name="speed">The device's speed, as the hub's port status reports it.</param>
    /// <returns>The location.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The port lies beyond what a route string names.</exception>
    public static UsbLocation Below(UsbLocation hub, int hubSlot, int port, int speed)
    {
        if (hub.Depth >= UsbHub.TIERS)
            throw new ArgumentOutOfRangeException(nameof(hub), $"A hub at tier {hub.Depth} has no tier left in the route string.");
        if (port < 1 || port > UsbHub.ROUTABLE_PORTS)
            throw new ArgumentOutOfRangeException(nameof(port), $"Port {port} lies beyond the route string's {UsbHub.ROUTABLE_PORTS}.");
        var translated = speed is XhciContexts.FULL_SPEED or XhciContexts.LOW_SPEED;
        var (ttSlot, ttPort) = !translated ? (0, 0) : hub.Speed == XhciContexts.HIGH_SPEED ? (hubSlot, port) : (hub.TtSlot, hub.TtPort);
        return new UsbLocation(hub.RootPort, hub.Route | (uint)port << 4 * hub.Depth, hub.Depth + 1, speed, ttSlot, ttPort);
    }

    #endregion

    #region Properties

    /// <summary>
    /// The location as QEMU's and Linux's paths write it: the root port, then each hub's port, dot-separated.
    /// </summary>
    public string Path
    {
        get
        {
            var path = new StringBuilder().Append(RootPort);
            for (var tier = 0; tier < Depth; tier++)
                path.Append('.').Append(Route >> 4 * tier & 0xF);
            return path.ToString();
        }
    }

    #endregion
}
