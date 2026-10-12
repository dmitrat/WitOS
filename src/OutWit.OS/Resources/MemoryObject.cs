using OutWit.OS.Native;

namespace OutWit.OS.Resources;

/// <summary>
/// Anonymous memory objects (RFC 0011 §7.2): pages the process owns, which it may map, pin for a device or move.
/// </summary>
public static class MemoryObject
{
    #region Constants

    /// <summary>
    /// The size of a page.
    /// </summary>
    public const int PAGE = 4096;

    #endregion

    #region Functions

    /// <summary>
    /// A new object of zeroed pages charged to the process.
    /// </summary>
    /// <param name="bytes">A multiple of the page size, at most 64 pages.</param>
    /// <returns>The object, with MAP, WRITE, QUERY, DUPLICATE and TRANSFER.</returns>
    /// <exception cref="CapabilityException">The size is not whole pages or the quota is spent.</exception>
    public static unsafe KernelHandle Create(int bytes)
    {
        ulong memoryObject = 0;
        CapabilityException.ThrowOnError("MEMORY_OBJECT_CREATE", Libwitos.MemoryObjectCreate((ulong)bytes, &memoryObject));
        return new KernelHandle(memoryObject);
    }

    #endregion
}
