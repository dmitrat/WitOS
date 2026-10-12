namespace OutWit.OS.FileSystems;

/// <summary>
/// What went wrong in a filesystem, the same for every provider, so that a namespace service maps it to one errno value.
/// </summary>
public enum FileSystemError
{
    /// <summary>Nothing is at the path, or a directory on the way is missing (ENOENT).</summary>
    NotFound,

    /// <summary>The path runs through a file, or names one where a directory is needed (ENOTDIR).</summary>
    NotADirectory,

    /// <summary>The path names a directory where a file is needed (EISDIR).</summary>
    NotAFile,

    /// <summary>Something is at the path already (EEXIST).</summary>
    AlreadyExists,

    /// <summary>The directory has entries (ENOTEMPTY).</summary>
    NotEmpty,

    /// <summary>The volume or a directory is full (ENOSPC).</summary>
    NoSpace,

    /// <summary>The entry is read-only, or the volume is (EACCES, EROFS).</summary>
    AccessDenied,

    /// <summary>The volume refuses every change (EROFS).</summary>
    ReadOnly,

    /// <summary>The entry is open in a conflicting way (EBUSY).</summary>
    InUse,

    /// <summary>The file would exceed what the format holds (EFBIG).</summary>
    TooLarge,

    /// <summary>A name or a path the format cannot hold (EINVAL, ENAMETOOLONG).</summary>
    InvalidName,

    /// <summary>The volume's structures are damaged (EIO).</summary>
    Corrupt,

    /// <summary>The volume is not of the provider's format, or uses what the provider does not support (EINVAL).</summary>
    Unsupported
}
