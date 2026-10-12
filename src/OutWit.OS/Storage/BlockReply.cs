namespace OutWit.OS.Storage;

/// <summary>
/// A block protocol reply (witos/block.h's WitBlockReply).
/// </summary>
/// <param name="Operation">The request's operation.</param>
/// <param name="Error">Zero or an errno value.</param>
/// <param name="Length">INFO: the device's bytes.</param>
/// <param name="BlockSize">INFO: its logical block.</param>
/// <param name="Flags">INFO: READ_ONLY and FLUSHES.</param>
/// <param name="MaxTransfer">INFO: the bytes one request may carry.</param>
public sealed record BlockReply(uint Operation, int Error, long Length = 0, int BlockSize = 0, uint Flags = 0, int MaxTransfer = 0);
