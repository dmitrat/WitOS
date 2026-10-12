namespace OutWit.OS.Hardware;

/// <summary>
/// A region a device exposes (witos/device.h): its physical base, its size and how it is reached.
/// </summary>
/// <param name="Base">The physical base.</param>
/// <param name="Size">The bytes; a multiple of the page size for memory regions.</param>
/// <param name="Flags">MEMORY, PORT, PREFETCHABLE and CONFIG.</param>
public sealed record DeviceRegion(ulong Base, ulong Size, uint Flags)
{
    #region Constants

    /// <summary>
    /// Memory-mapped, mappable through DEVICE_MEMORY.
    /// </summary>
    public const uint MEMORY = 1;

    /// <summary>
    /// Port I/O: described, never mappable.
    /// </summary>
    public const uint PORT = 2;

    /// <summary>
    /// Prefetchable memory.
    /// </summary>
    public const uint PREFETCHABLE = 4;

    /// <summary>
    /// The function's configuration space (its ECAM page).
    /// </summary>
    public const uint CONFIG = 8;

    #endregion

    #region Properties

    /// <summary>
    /// Whether DEVICE_MEMORY can map the region.
    /// </summary>
    public bool IsMemory => (Flags & MEMORY) != 0;

    #endregion
}
