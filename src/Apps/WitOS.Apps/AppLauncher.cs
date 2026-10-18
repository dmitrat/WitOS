using System;
using System.IO;
using System.Text;

namespace WitOS.Apps;

/// <summary>
/// A command's launcher (ADR 0027 decision 3): upstream's witos apphost with the application's path in its placeholder,
/// as the SDK's HostWriter writes an apphost and a .NET tool's shim.
/// </summary>
public static class AppLauncher
{
    #region Constants

    // The apphost's placeholder for its application's path: the SHA-256 of "foobar" in UTF-8, which the SDK's HostWriter
    // replaces, followed by zeros to the length of the host's buffer.
    private const string PLACEHOLDER = "c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2";

    private const int MAX_APP_PATH_BYTES = 1024;

    #endregion

    #region Functions

    /// <summary>
    /// A launcher: the apphost with the application's path in its placeholder.
    /// </summary>
    /// <param name="apphost">The apphost's bytes.</param>
    /// <param name="appPath">The assembly's path, relative to the launcher's directory or absolute.</param>
    /// <returns>The launcher's bytes.</returns>
    /// <exception cref="InvalidDataException">The apphost holds no single placeholder, or the path does not fit it.</exception>
    public static byte[] Write(ReadOnlySpan<byte> apphost, string appPath)
    {
        var placeholder = Encoding.UTF8.GetBytes(PLACEHOLDER);
        var at = apphost.IndexOf(placeholder);
        if (at < 0 || apphost[(at + 1)..].IndexOf(placeholder) >= 0)
            throw new InvalidDataException("The apphost holds no single application path placeholder.");
        var path = Encoding.UTF8.GetBytes(appPath);
        if (path.Length == 0 || path.Length >= MAX_APP_PATH_BYTES || path.AsSpan().Contains((byte)0) || at + MAX_APP_PATH_BYTES > apphost.Length)
            throw new InvalidDataException($"The application path {appPath} does not fit the apphost's placeholder.");
        var launcher = apphost.ToArray();
        path.CopyTo(launcher, at);
        Array.Clear(launcher, at + path.Length, placeholder.Length - Math.Min(path.Length, placeholder.Length));
        return launcher;
    }

    /// <summary>
    /// A command's assembly as its launcher names it: relative to the layer's bin directory.
    /// </summary>
    /// <param name="id">The application's identity.</param>
    /// <param name="version">The version.</param>
    /// <param name="assembly">The assembly's path in the version's directory.</param>
    /// <returns>The path the launcher holds.</returns>
    public static string AppPath(string id, string version, string assembly) => $"../{id}/{version}/{assembly}";

    #endregion
}
