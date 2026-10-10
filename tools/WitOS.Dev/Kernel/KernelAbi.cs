using System.Text.RegularExpressions;

namespace WitOS.Dev.Kernel;

/// <summary>
/// Reads ABI facts from the kernel headers, the single source of truth for
/// versions and call numbers. Documentation and guest output are checked against it.
/// </summary>
internal static partial class KernelAbi
{
    #region Constants

    private const string USER_ABI_HEADER = "src/Kernel/include/witos/user_abi.h";

    private const string BOOT_HEADER = "src/Kernel/include/witos/boot.h";

    #endregion

    #region Functions

    /// <summary>
    /// Reads the user ABI version from the shared header as major.minor (ABI-1 1.0 since plan step K8.4d).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>WIT_ABI_VERSION_MAJOR and WIT_ABI_VERSION_MINOR, such as 1.0.</returns>
    public static string UserVersion(string root) =>
        $"{ReadDefine(root, USER_ABI_HEADER, "WIT_ABI_VERSION_MAJOR")}.{ReadDefine(root, USER_ABI_HEADER, "WIT_ABI_VERSION_MINOR")}";

    /// <summary>
    /// Reads the boot ABI version from the boot header.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>WIT_BOOT_VERSION.</returns>
    public static int BootVersion(string root) => ReadDefine(root, BOOT_HEADER, "WIT_BOOT_VERSION");

    /// <summary>
    /// Builds the version line that every successful boot must print.
    /// </summary>
    /// <remarks>
    /// The first kernel output line; BootValidation requires it in every successful boot.
    /// </remarks>
    /// <param name="root">Repository root.</param>
    /// <returns>Banner text.</returns>
    public static string Banner(string root) => $"WitOS user ABI {UserVersion(root)}, boot ABI v{BootVersion(root)}";

    /// <summary>
    /// Reads every user ABI call number from the shared header.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Call numbers by name.</returns>
    public static IReadOnlyDictionary<string, int> Calls(string root)
    {
        var calls = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in CallDefine().Matches(File.ReadAllText(Path.Combine(root, USER_ABI_HEADER))))
        {
            calls.Add(match.Groups[1].Value, int.Parse(match.Groups[2].Value));
        }
        return calls;
    }

    /// <summary>
    /// Reads the retired call numbers from the shared header: a comment "N was retired at step S" or "N-M were retired
    /// at step S" retires each number of the range for good (RFC 0011 section 10.1).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Retired call numbers in ascending order.</returns>
    public static IReadOnlyList<int> RetiredCalls(string root)
    {
        var retired = new SortedSet<int>();
        foreach (Match match in RetiredComment().Matches(File.ReadAllText(Path.Combine(root, USER_ABI_HEADER))))
        {
            var first = int.Parse(match.Groups[1].Value);
            var last = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : first;
            for (var number = first; number <= last; ++number)
            {
                retired.Add(number);
            }
        }
        return [.. retired];
    }

    #endregion

    #region Tools

    private static int ReadDefine(string root, string header, string name)
    {
        var text = File.ReadAllText(Path.Combine(root, header));
        var matches = Regex.Matches(text, $@"^#define {name} (\d+)U\r?$", RegexOptions.Multiline);
        if (matches.Count != 1)
        {
            throw new InvalidDataException($"{header} must define {name} exactly once.");
        }
        return int.Parse(matches[0].Groups[1].Value);
    }

    [GeneratedRegex(@"^#define (WIT_CALL_[A-Z0-9_]+) (\d+)U\r?$", RegexOptions.Multiline)]
    private static partial Regex CallDefine();

    [GeneratedRegex(@"/\*\s*(\d+)(?:-(\d+))?\s+(?:was|were)\s+retired at step")]
    private static partial Regex RetiredComment();

    #endregion
}
