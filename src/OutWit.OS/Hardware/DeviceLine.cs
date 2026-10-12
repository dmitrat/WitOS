namespace OutWit.OS.Hardware;

/// <summary>
/// An interrupt line a device can raise (witos/device.h).
/// </summary>
/// <param name="Kind">LEVEL for a shared, level-triggered line (PCI INTx).</param>
/// <param name="Line">The platform's line number: a PIC input on q35, a GIC INTID on virt.</param>
public sealed record DeviceLine(uint Kind, uint Line)
{
    #region Constants

    /// <summary>
    /// A shared, level-triggered line.
    /// </summary>
    public const uint LEVEL = 1;

    #endregion
}
