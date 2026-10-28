using System.IO;

namespace OutWit.OS.FileSystems.Smb;

/// <summary>
/// The NTSTATUS values an SMB client meets (MS-ERREF 2.3), and what each means to the filesystem contract: each failure a
/// file system shares with the others becomes its <see cref="FileSystemError"/>, so that the namespace service gives a
/// share's failure the same errno a disk's has; any other is an <see cref="IOException"/> naming the status.
/// </summary>
internal static class SmbStatus
{
    #region Constants

    public const uint SUCCESS = 0x00000000;
    public const uint PENDING = 0x00000103;
    public const uint BUFFER_OVERFLOW = 0x80000005;
    public const uint NO_MORE_FILES = 0x80000006;
    public const uint INVALID_PARAMETER = 0xC000000D;
    public const uint NO_SUCH_FILE = 0xC000000F;
    public const uint END_OF_FILE = 0xC0000011;
    public const uint MORE_PROCESSING_REQUIRED = 0xC0000016;
    public const uint ACCESS_DENIED = 0xC0000022;
    public const uint OBJECT_NAME_INVALID = 0xC0000033;
    public const uint OBJECT_NAME_NOT_FOUND = 0xC0000034;
    public const uint OBJECT_NAME_COLLISION = 0xC0000035;
    public const uint OBJECT_PATH_NOT_FOUND = 0xC000003A;
    public const uint SHARING_VIOLATION = 0xC0000043;
    public const uint DELETE_PENDING = 0xC0000056;
    public const uint LOGON_FAILURE = 0xC000006D;
    public const uint PASSWORD_EXPIRED = 0xC0000071;
    public const uint ACCOUNT_DISABLED = 0xC0000072;
    public const uint DISK_FULL = 0xC000007F;
    public const uint MEDIA_WRITE_PROTECTED = 0xC00000A2;
    public const uint FILE_IS_A_DIRECTORY = 0xC00000BA;
    public const uint NOT_SUPPORTED = 0xC00000BB;
    public const uint BAD_NETWORK_NAME = 0xC00000CC;
    public const uint NOT_SAME_DEVICE = 0xC00000D4;
    public const uint DIRECTORY_NOT_EMPTY = 0xC0000101;
    public const uint NOT_A_DIRECTORY = 0xC0000103;
    public const uint NAME_TOO_LONG = 0xC0000106;
    public const uint CANNOT_DELETE = 0xC0000121;
    public const uint FILE_CLOSED = 0xC0000128;
    public const uint PASSWORD_MUST_CHANGE = 0xC0000224;
    public const uint ACCOUNT_LOCKED_OUT = 0xC0000234;
    public const uint USER_SESSION_DELETED = 0xC0000203;
    public const uint NETWORK_SESSION_EXPIRED = 0xC000035C;

    #endregion

    #region Functions

    /// <summary>
    /// The exception a failed request throws: a filesystem error the contract shares, or an I/O error naming the status.
    /// </summary>
    /// <param name="status">The response's status.</param>
    /// <param name="what">What was asked, for the message.</param>
    /// <returns>The exception.</returns>
    public static System.Exception Failure(uint status, string what)
    {
        var message = $"{what}: {Name(status)} (0x{status:X8})";
        FileSystemError? error = status switch
        {
            OBJECT_NAME_NOT_FOUND or OBJECT_PATH_NOT_FOUND or NO_SUCH_FILE or DELETE_PENDING => FileSystemError.NotFound,
            OBJECT_NAME_COLLISION => FileSystemError.AlreadyExists,
            NOT_A_DIRECTORY => FileSystemError.NotADirectory,
            FILE_IS_A_DIRECTORY => FileSystemError.NotAFile,
            DIRECTORY_NOT_EMPTY => FileSystemError.NotEmpty,
            DISK_FULL => FileSystemError.NoSpace,
            ACCESS_DENIED or CANNOT_DELETE => FileSystemError.AccessDenied,
            MEDIA_WRITE_PROTECTED => FileSystemError.ReadOnly,
            SHARING_VIOLATION => FileSystemError.InUse,
            OBJECT_NAME_INVALID or NAME_TOO_LONG => FileSystemError.InvalidName,
            NOT_SUPPORTED => FileSystemError.Unsupported,
            _ => null
        };
        return error is { } known ? new FileSystemException(known, message) : new SmbException(status, message);
    }

    /// <summary>
    /// A status's name, for a message.
    /// </summary>
    /// <param name="status">The status.</param>
    /// <returns>Its name, or "status" for one the client does not name.</returns>
    public static string Name(uint status) => status switch
    {
        SUCCESS => "STATUS_SUCCESS",
        PENDING => "STATUS_PENDING",
        BUFFER_OVERFLOW => "STATUS_BUFFER_OVERFLOW",
        NO_MORE_FILES => "STATUS_NO_MORE_FILES",
        INVALID_PARAMETER => "STATUS_INVALID_PARAMETER",
        NO_SUCH_FILE => "STATUS_NO_SUCH_FILE",
        END_OF_FILE => "STATUS_END_OF_FILE",
        MORE_PROCESSING_REQUIRED => "STATUS_MORE_PROCESSING_REQUIRED",
        ACCESS_DENIED => "STATUS_ACCESS_DENIED",
        OBJECT_NAME_INVALID => "STATUS_OBJECT_NAME_INVALID",
        OBJECT_NAME_NOT_FOUND => "STATUS_OBJECT_NAME_NOT_FOUND",
        OBJECT_NAME_COLLISION => "STATUS_OBJECT_NAME_COLLISION",
        OBJECT_PATH_NOT_FOUND => "STATUS_OBJECT_PATH_NOT_FOUND",
        SHARING_VIOLATION => "STATUS_SHARING_VIOLATION",
        DELETE_PENDING => "STATUS_DELETE_PENDING",
        LOGON_FAILURE => "STATUS_LOGON_FAILURE",
        PASSWORD_EXPIRED => "STATUS_PASSWORD_EXPIRED",
        ACCOUNT_DISABLED => "STATUS_ACCOUNT_DISABLED",
        DISK_FULL => "STATUS_DISK_FULL",
        MEDIA_WRITE_PROTECTED => "STATUS_MEDIA_WRITE_PROTECTED",
        FILE_IS_A_DIRECTORY => "STATUS_FILE_IS_A_DIRECTORY",
        NOT_SUPPORTED => "STATUS_NOT_SUPPORTED",
        BAD_NETWORK_NAME => "STATUS_BAD_NETWORK_NAME",
        NOT_SAME_DEVICE => "STATUS_NOT_SAME_DEVICE",
        DIRECTORY_NOT_EMPTY => "STATUS_DIRECTORY_NOT_EMPTY",
        NOT_A_DIRECTORY => "STATUS_NOT_A_DIRECTORY",
        NAME_TOO_LONG => "STATUS_NAME_TOO_LONG",
        CANNOT_DELETE => "STATUS_CANNOT_DELETE",
        FILE_CLOSED => "STATUS_FILE_CLOSED",
        PASSWORD_MUST_CHANGE => "STATUS_PASSWORD_MUST_CHANGE",
        ACCOUNT_LOCKED_OUT => "STATUS_ACCOUNT_LOCKED_OUT",
        USER_SESSION_DELETED => "STATUS_USER_SESSION_DELETED",
        NETWORK_SESSION_EXPIRED => "STATUS_NETWORK_SESSION_EXPIRED",
        _ => "status"
    };

    #endregion
}

/// <summary>
/// A request the server failed with a status the filesystem contract has no error for, or a session or protocol
/// failure: an I/O error, which the namespace service gives a client as EIO.
/// </summary>
/// <param name="status">The NTSTATUS, or 0 for a failure of the protocol itself.</param>
/// <param name="message">What failed.</param>
internal sealed class SmbException(uint status, string message) : IOException(message)
{
    #region Properties

    /// <summary>
    /// The NTSTATUS the server answered, or 0.
    /// </summary>
    public uint Status { get; } = status;

    #endregion
}
