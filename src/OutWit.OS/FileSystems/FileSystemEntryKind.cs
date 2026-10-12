namespace OutWit.OS.FileSystems;

/// <summary>
/// What an entry is.
/// </summary>
public enum FileSystemEntryKind
{
    /// <summary>A regular file.</summary>
    File,

    /// <summary>A directory.</summary>
    Directory,

    /// <summary>A symbolic link, whose target ReadLinkAsync gives.</summary>
    SymbolicLink,

    /// <summary>A device node, a pipe or a socket the format records and the provider serves no data of.</summary>
    Other
}
