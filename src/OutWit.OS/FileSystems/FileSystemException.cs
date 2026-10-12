using System;
using System.IO;

namespace OutWit.OS.FileSystems;

/// <summary>
/// A failure of a filesystem, with what went wrong.
/// </summary>
public sealed class FileSystemException : IOException
{
    #region Constructors

    /// <summary>
    /// Creates the exception.
    /// </summary>
    /// <param name="error">What went wrong.</param>
    /// <param name="message">What went wrong, for a person.</param>
    /// <param name="innerException">The provider's own failure, or null.</param>
    public FileSystemException(FileSystemError error, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    #endregion

    #region Properties

    /// <summary>
    /// What went wrong.
    /// </summary>
    public FileSystemError Error { get; }

    #endregion
}
