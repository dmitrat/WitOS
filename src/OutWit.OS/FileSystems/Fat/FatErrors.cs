using System;
using System.Threading.Tasks;
using OutWit.Common.Fat.Exceptions;
using OutWit.Common.Fat.Model;

namespace OutWit.OS.FileSystems.Fat;

/// <summary>
/// The library's failures as the filesystem contract's: a FatException by its kind, a write to a read-only device as
/// ReadOnly, an invalid name or a path above the root as InvalidName.
/// </summary>
internal static class FatErrors
{
    #region Functions

    /// <summary>
    /// Runs an operation of the library, translating its failures.
    /// </summary>
    /// <typeparam name="T">The operation's result.</typeparam>
    /// <param name="operation">The operation.</param>
    /// <returns>Its result.</returns>
    /// <exception cref="FileSystemException">The operation failed.</exception>
    public static async ValueTask<T> RunAsync<T>(Func<ValueTask<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (Translate(exception) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// Runs an operation of the library that returns nothing, translating its failures.
    /// </summary>
    /// <param name="operation">The operation.</param>
    /// <returns>When it completed.</returns>
    /// <exception cref="FileSystemException">The operation failed.</exception>
    public static async ValueTask RunAsync(Func<ValueTask> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (Translate(exception) is { } translated)
        {
            throw translated;
        }
    }

    // The contract's failure for one of the library's; null for what passes unchanged (cancellation, disposal).
    private static FileSystemException? Translate(Exception exception) => exception switch
    {
        FatException fat => new FileSystemException(Error(fat.Kind), fat.Message, fat),
        NotSupportedException => new FileSystemException(FileSystemError.ReadOnly, exception.Message, exception),
        ArgumentException and not ArgumentNullException => new FileSystemException(FileSystemError.InvalidName, exception.Message, exception),
        _ => null
    };

    private static FileSystemError Error(FatErrorKind kind) => kind switch
    {
        FatErrorKind.NotFound => FileSystemError.NotFound,
        FatErrorKind.NotADirectory => FileSystemError.NotADirectory,
        FatErrorKind.NotAFile => FileSystemError.NotAFile,
        FatErrorKind.AlreadyExists => FileSystemError.AlreadyExists,
        FatErrorKind.NotEmpty => FileSystemError.NotEmpty,
        FatErrorKind.NoSpace => FileSystemError.NoSpace,
        FatErrorKind.AccessDenied => FileSystemError.AccessDenied,
        FatErrorKind.InUse => FileSystemError.InUse,
        FatErrorKind.TooLarge => FileSystemError.TooLarge,
        FatErrorKind.NotRecognized or FatErrorKind.Unsupported or FatErrorKind.SectorSizeMismatch => FileSystemError.Unsupported,
        _ => FileSystemError.Corrupt
    };

    #endregion
}
