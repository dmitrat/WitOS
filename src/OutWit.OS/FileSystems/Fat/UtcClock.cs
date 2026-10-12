using System;

namespace OutWit.OS.FileSystems.Fat;

/// <summary>
/// The system's time with UTC as the local zone. FAT records local times without a zone; WitOS's FAT volumes record
/// UTC, on the host as in the guest, so that a time reads the same wherever the volume is mounted.
/// </summary>
internal sealed class UtcClock : TimeProvider
{
    #region Constants

    /// <summary>
    /// The one clock.
    /// </summary>
    public static readonly UtcClock INSTANCE = new();

    #endregion

    #region Properties

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    #endregion
}
