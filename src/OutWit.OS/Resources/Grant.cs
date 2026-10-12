using OutWit.OS.Native;

namespace OutWit.OS.Resources;

/// <summary>
/// The capability a process's creator granted it at its start (witos/start.h, plan step D1.1): a service's channel to
/// its creator.
/// </summary>
public static class Grant
{
    #region Functions

    /// <summary>
    /// Takes the granted capability; it is the caller's from then on.
    /// </summary>
    /// <returns>The handle.</returns>
    /// <exception cref="CapabilityException">The process was granted nothing, or the grant was taken already.</exception>
    public static unsafe KernelHandle Take()
    {
        ulong handle = 0;
        CapabilityException.ThrowOnError("the start grant", Libwitos.TakeGrant(&handle));
        return new KernelHandle(handle);
    }

    #endregion
}
