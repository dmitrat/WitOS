using System.IO;

namespace OutWit.OS.Resources;

/// <summary>
/// A call of the system layer's capability API that failed, with the errno value libwitos returned.
/// </summary>
public sealed class CapabilityException : IOException
{
    #region Constructors

    /// <summary>
    /// Creates the exception of a failed call.
    /// </summary>
    /// <param name="operation">What was asked of the system layer.</param>
    /// <param name="error">The errno value it answered.</param>
    public CapabilityException(string operation, int error)
        : base($"{operation} failed with errno {error}.")
    {
        Error = error;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Throws for a nonzero errno value.
    /// </summary>
    /// <param name="operation">What was asked of the system layer.</param>
    /// <param name="error">The errno value libwitos returned.</param>
    /// <exception cref="CapabilityException">The value is not zero.</exception>
    internal static void ThrowOnError(string operation, int error)
    {
        if (error != 0)
            throw new CapabilityException(operation, error);
    }

    #endregion

    #region Properties

    /// <summary>
    /// The errno value of the failed call.
    /// </summary>
    public int Error { get; }

    #endregion
}
