using System.IO;

namespace OutWit.OS.DeviceManager;

/// <summary>
/// A session a person works in (plan step C2.7): the package the <c>shell</c> command boots holds /etc/witos/session. Its
/// boot runs none of the probes the gates judge (H5.1): no test writes to the person's volumes, no ARP for QEMU's gateway,
/// no exchange with the runner's servers, no test pattern, no prompt on the terminal. Those probes took 36 of the 44
/// seconds a VMware machine's services needed before the device manager answered ready, where nothing answers QEMU's
/// addresses.
/// </summary>
internal static class Session
{
    #region Constants

    /// <summary>
    /// The package's mark of a session.
    /// </summary>
    public const string MARK = "/etc/witos/session";

    #endregion

    #region Properties

    /// <summary>
    /// Whether this boot is a session.
    /// </summary>
    public static bool Active { get; } = File.Exists(MARK);

    #endregion
}
