namespace OutWit.OS.FileSystems;

/// <summary>
/// The owner, permissions and links a POSIX-shaped format records of an entry, which a namespace service reports in
/// stat; recorded data, not authority (storage document §56).
/// </summary>
/// <param name="Mode">The permission bits, with set-user-id, set-group-id and sticky (0o7777).</param>
/// <param name="UserId">The owner's user id.</param>
/// <param name="GroupId">The owner's group id.</param>
/// <param name="Links">The hard links to it.</param>
/// <param name="Id">Its number in the volume, stable while it exists.</param>
public sealed record FileSystemUnixMetadata(int Mode, long UserId, long GroupId, int Links, long Id);
