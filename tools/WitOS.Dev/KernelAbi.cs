using System.Text.RegularExpressions;

namespace WitOS.Dev;

// Reads ABI facts from the kernel headers, the single source of truth for
// versions and call numbers. Documentation and guest output are checked against it.
internal static partial class KernelAbi
{
    private const string UserAbiHeader = "src/Kernel/include/witos/user_abi.h";
    private const string BootHeader = "src/Kernel/include/witos/boot.h";

    public static int UserVersion(string root) => ReadDefine(root, UserAbiHeader, "WIT_ABI_VERSION");

    public static int BootVersion(string root) => ReadDefine(root, BootHeader, "WIT_BOOT_VERSION");

    // The first kernel output line; BootAsync requires it in every successful boot.
    public static string Banner(string root) => $"WitOS user ABI v{UserVersion(root)}, boot ABI v{BootVersion(root)}";

    public static IReadOnlyDictionary<string, int> Calls(string root)
    {
        var calls = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in CallDefine().Matches(File.ReadAllText(Path.Combine(root, UserAbiHeader))))
        {
            calls.Add(match.Groups[1].Value, int.Parse(match.Groups[2].Value));
        }
        return calls;
    }

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
}
