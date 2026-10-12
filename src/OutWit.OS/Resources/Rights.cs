using System;

namespace OutWit.OS.Resources;

/// <summary>
/// The rights of a handle (ABI-1's WIT_RIGHT_* values): what its holder may do with the object it names.
/// </summary>
[Flags]
public enum Rights : ulong
{
    /// <summary>No right.</summary>
    None = 0,

    /// <summary>Write the object.</summary>
    Write = 1,

    /// <summary>Wait for the object.</summary>
    Wait = 4,

    /// <summary>Signal the object.</summary>
    Signal = 8,

    /// <summary>Query the object.</summary>
    Query = 16,

    /// <summary>Send on a channel endpoint.</summary>
    Send = 512,

    /// <summary>Receive on a channel endpoint.</summary>
    Receive = 1024,

    /// <summary>Duplicate the handle.</summary>
    Duplicate = 2048,

    /// <summary>Move the handle over a channel.</summary>
    Transfer = 4096,

    /// <summary>Map a memory object.</summary>
    Map = 8192,

    /// <summary>Map a memory object executable.</summary>
    Execute = 16384,

    /// <summary>Acquire devices of the device table.</summary>
    Acquire = 32768,

    /// <summary>Use a device: its memory, its interrupts and DMA.</summary>
    Bind = 65536
}
