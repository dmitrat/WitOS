using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace WitOS.Apps;

/// <summary>
/// The global.json beside the layer (plan step C2.5): the roots of the .NET SDKs the layer's current versions provide, so
/// that dotnet and MSBuildLocator find the SDK a base application brings. hostfxr reads the file from the working
/// directory upwards and appends sdk to each path, relative to the file, so a root is a version's directory that holds
/// sdk/&lt;version&gt;/; sessions start in /data, where the file lies beside apps. The layer owns the file: it is written
/// whenever a version is put in use and removed once no current version holds an SDK.
/// </summary>
public static class SdkRoots
{
    #region Constants

    /// <summary>
    /// The file's name, in the directory above the layer's root.
    /// </summary>
    public const string FILE = "global.json";

    /// <summary>
    /// The directory a version provides an SDK through.
    /// </summary>
    public const string SDK = "sdk";

    // The same text on every host: the guest reads it, and a test on Windows compares it.
    private static readonly JsonSerializerOptions OPTIONS = new() { WriteIndented = true, NewLine = "\n" };

    #endregion

    #region Functions

    /// <summary>
    /// The file's text for the roots, each relative to the file.
    /// </summary>
    /// <param name="roots">The roots, in the applications' order.</param>
    /// <returns>The text.</returns>
    public static string GlobalJson(IEnumerable<string> roots) =>
        JsonSerializer.Serialize(new { sdk = new { paths = roots.ToArray() } }, OPTIONS) + "\n";

    /// <summary>
    /// A root as the file names it: the layer's directory name, the application and the version.
    /// </summary>
    /// <param name="layer">The layer's directory name, apps.</param>
    /// <param name="id">The application's identity.</param>
    /// <param name="version">The version in use.</param>
    /// <returns>The root.</returns>
    public static string Root(string layer, string id, string version) => $"{layer}/{id}/{version}";

    #endregion
}
