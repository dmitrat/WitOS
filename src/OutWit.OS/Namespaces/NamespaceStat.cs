using System;

namespace OutWit.OS.Namespaces;

/// <summary>
/// What a stat reports of an entry (witos/namespace.h's WitNamespaceStat, plan step D5.1).
/// </summary>
/// <param name="Kind">WIT_NAMESPACE_KIND_FILE, _DIRECTORY, _LINK or _OTHER.</param>
/// <param name="Mode">The permission bits (07777).</param>
/// <param name="Size">The bytes of a file or a link.</param>
/// <param name="Id">The entry's number in its volume.</param>
/// <param name="Links">The hard links.</param>
/// <param name="Volume">The volume's index in the mount list.</param>
/// <param name="UserId">The owner's user id.</param>
/// <param name="GroupId">The owner's group id.</param>
/// <param name="BlockSize">The volume's allocation unit.</param>
/// <param name="Blocks">The 512-byte blocks the entry takes.</param>
/// <param name="Accessed">When it was last read.</param>
/// <param name="Modified">When its data were last written.</param>
/// <param name="Changed">When the entry last changed.</param>
public sealed record NamespaceStat(uint Kind, uint Mode, long Size, long Id, uint Links, uint Volume, uint UserId, uint GroupId, uint BlockSize,
    long Blocks, DateTimeOffset Accessed, DateTimeOffset Modified, DateTimeOffset Changed);
