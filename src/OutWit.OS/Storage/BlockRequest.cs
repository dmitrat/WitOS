namespace OutWit.OS.Storage;

/// <summary>
/// A block protocol request (witos/block.h's WitBlockRequest).
/// </summary>
/// <param name="Operation">ATTACH, INFO, READ, WRITE or FLUSH.</param>
/// <param name="Flags">DURABLE for a write that is durable when it completes.</param>
/// <param name="Offset">Bytes of the device.</param>
/// <param name="Length">Bytes.</param>
/// <param name="Window">The offset of the bytes in the window.</param>
public sealed record BlockRequest(uint Operation, uint Flags = 0, long Offset = 0, long Length = 0, long Window = 0);
