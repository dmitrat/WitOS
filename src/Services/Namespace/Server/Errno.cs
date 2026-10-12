using System;
using OutWit.OS.FileSystems;

namespace OutWit.OS.Namespace.Server;

/// <summary>
/// The errno values of the namespace protocol: a filesystem's error as the one value POSIX gives it.
/// </summary>
internal static class Errno
{
    #region Constants

    /// <summary>ENOENT.</summary>
    public const int NOT_FOUND = 2;

    /// <summary>EIO.</summary>
    public const int IO = 5;

    /// <summary>EBADF.</summary>
    public const int BAD_HANDLE = 9;

    /// <summary>EACCES.</summary>
    public const int ACCESS = 13;

    /// <summary>EBUSY.</summary>
    public const int BUSY = 16;

    /// <summary>EEXIST.</summary>
    public const int EXISTS = 17;

    /// <summary>EXDEV.</summary>
    public const int CROSS_DEVICE = 18;

    /// <summary>ENOTDIR.</summary>
    public const int NOT_DIRECTORY = 20;

    /// <summary>EISDIR.</summary>
    public const int IS_DIRECTORY = 21;

    /// <summary>EINVAL.</summary>
    public const int INVALID = 22;

    /// <summary>EFBIG.</summary>
    public const int TOO_LARGE = 27;

    /// <summary>ENOSPC.</summary>
    public const int NO_SPACE = 28;

    /// <summary>EROFS.</summary>
    public const int READ_ONLY = 30;

    /// <summary>ENAMETOOLONG.</summary>
    public const int NAME_TOO_LONG = 36;

    /// <summary>ENOSYS.</summary>
    public const int NOT_IMPLEMENTED = 38;

    /// <summary>ENOTEMPTY.</summary>
    public const int NOT_EMPTY = 39;

    /// <summary>EPROTO.</summary>
    public const int PROTOCOL = 71;

    /// <summary>EOPNOTSUPP.</summary>
    public const int NOT_SUPPORTED = 95;

    #endregion

    #region Functions

    /// <summary>
    /// The errno value of a failure.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <returns>Its errno value.</returns>
    public static int Of(Exception exception) => exception switch
    {
        FileSystemException failure => failure.Error switch
        {
            FileSystemError.NotFound => NOT_FOUND,
            FileSystemError.NotADirectory => NOT_DIRECTORY,
            FileSystemError.NotAFile => IS_DIRECTORY,
            FileSystemError.AlreadyExists => EXISTS,
            FileSystemError.NotEmpty => NOT_EMPTY,
            FileSystemError.NoSpace => NO_SPACE,
            FileSystemError.AccessDenied => ACCESS,
            FileSystemError.ReadOnly => READ_ONLY,
            FileSystemError.InUse => BUSY,
            FileSystemError.TooLarge => TOO_LARGE,
            FileSystemError.NotALink or FileSystemError.InvalidName => INVALID,
            FileSystemError.Unsupported => NOT_SUPPORTED,
            _ => IO
        },
        ArgumentException => INVALID,
        NotSupportedException => NOT_SUPPORTED,
        _ => IO
    };

    #endregion
}
