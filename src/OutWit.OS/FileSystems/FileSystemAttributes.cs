using System;

namespace OutWit.OS.FileSystems;

/// <summary>
/// The attributes of an entry that more than one format records; a format without one never reports it.
/// </summary>
[Flags]
public enum FileSystemAttributes
{
    /// <summary>No attribute.</summary>
    None = 0,

    /// <summary>Writes are refused.</summary>
    ReadOnly = 1,

    /// <summary>Listings may leave it out.</summary>
    Hidden = 2,

    /// <summary>The system's own.</summary>
    System = 4,

    /// <summary>Changed since the last backup.</summary>
    Archive = 8
}
