namespace WitOS.Dev.Runtime;

/// <summary>
/// The console marks of WitOS (plan step C1.4, @Docs/WitOS-Console-Logos.md): the art and the palette the .NET install
/// carries under /etc/witos/logo, which an interactive session's profile renders; plain text, byte-identical on every
/// ISA, never printed by the kernel, the root task or a service.
/// </summary>
internal static class ConsoleLogo
{
    #region Constants

    /// <summary>
    /// Where the art lives in the repository.
    /// </summary>
    public const string SOURCE = "build/console/logo";

    /// <summary>
    /// Where the art lives in the image.
    /// </summary>
    public const string DIRECTORY = "etc/witos/logo";

    /// <summary>
    /// The profile that renders a mark for an interactive PowerShell session, placed beside pwsh.dll as $PSHOME/profile.ps1.
    /// </summary>
    public const string PROFILE = "build/console/profile.ps1";

    /// <summary>
    /// The art files: the emblem (32 by 7), the wordmark (42 by 6), the ASCII fallback (33 by 4) and the palette.
    /// </summary>
    public static readonly string[] FILES = ["emblem.txt", "wordmark.txt", "ascii.txt", "palette.json"];

    #endregion

    #region Functions

    /// <summary>
    /// The art for the install.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Package paths under <see cref="DIRECTORY"/> and the files to place there.</returns>
    public static IReadOnlyList<(string Name, string Source)> PackageFiles(string root) =>
        [.. FILES.Select(file => ($"{DIRECTORY}/{file}", Path.Combine(root, SOURCE, file)))];

    #endregion
}
