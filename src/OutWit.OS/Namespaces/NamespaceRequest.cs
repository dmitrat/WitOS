namespace OutWit.OS.Namespaces;

/// <summary>
/// A request on a namespace session (witos/namespace.h's WitNamespaceRequest).
/// </summary>
/// <param name="Operation">The operation.</param>
/// <param name="Flags">OPEN's access and creation flags; REMOVE's DIRECTORY.</param>
/// <param name="Handle">A file or a directory the session opened.</param>
/// <param name="Offset">A file's byte, or a listing's first entry.</param>
/// <param name="Length">The bytes the operation reads, writes or carries in the window.</param>
/// <param name="Mode">OPEN with CREATE: the permission bits of a new file.</param>
public sealed record NamespaceRequest(uint Operation, uint Flags = 0, long Handle = 0, long Offset = 0, long Length = 0, uint Mode = 0);
