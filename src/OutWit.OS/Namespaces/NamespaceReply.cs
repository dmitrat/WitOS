namespace OutWit.OS.Namespaces;

/// <summary>
/// A reply on a namespace session (witos/namespace.h's WitNamespaceReply).
/// </summary>
/// <param name="Operation">The request's operation.</param>
/// <param name="Error">Zero or an errno value.</param>
/// <param name="Handle">OPEN: the handle the session opened.</param>
/// <param name="Offset">LIST: the next entry, zero at the end.</param>
/// <param name="Length">The bytes the operation put in the window or read.</param>
/// <param name="Stat">The entry's stat, where the operation gives one.</param>
public sealed record NamespaceReply(uint Operation, int Error, long Handle = 0, long Offset = 0, long Length = 0, NamespaceStat? Stat = null);
