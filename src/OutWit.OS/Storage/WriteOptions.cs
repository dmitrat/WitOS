using System;

namespace OutWit.OS.Storage;

/// <summary>
/// How a write is to complete (storage document §47).
/// </summary>
[Flags]
public enum WriteOptions
{
    /// <summary>The write completes when the resource accepted the bytes.</summary>
    None = 0,

    /// <summary>The write completes when the bytes are durable (FUA, or a flush where the device has no FUA).</summary>
    Durable = 1
}
