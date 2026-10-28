namespace OutWit.OS.FileSystems;

/// <summary>
/// A volume's space (plan step E1, ADR 0032 decision 4): what statfs and DriveInfo report.
/// </summary>
/// <param name="TotalBytes">The bytes the volume's files may take in all.</param>
/// <param name="FreeBytes">The bytes still free for them.</param>
public sealed record FileSystemSpace(long TotalBytes, long FreeBytes);
