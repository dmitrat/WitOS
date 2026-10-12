using System;

namespace OutWit.OS.Namespace.Server;

/// <summary>
/// A session's window: the client's memory both map, through which paths, data and listings travel.
/// </summary>
internal interface INamespaceWindow
{
    #region Functions

    /// <summary>
    /// A copy of bytes of the window.
    /// </summary>
    /// <param name="offset">The first byte.</param>
    /// <param name="count">The bytes.</param>
    /// <returns>The copy.</returns>
    byte[] Read(int offset, int count);

    /// <summary>
    /// Writes bytes into the window.
    /// </summary>
    /// <param name="offset">The first byte.</param>
    /// <param name="bytes">The bytes.</param>
    void Write(int offset, ReadOnlySpan<byte> bytes);

    #endregion

    #region Properties

    /// <summary>
    /// The window's bytes.
    /// </summary>
    int Length { get; }

    #endregion
}
