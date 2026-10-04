using System.Globalization;
using System.Text.RegularExpressions;

namespace WitOS.Dev.NativeAot.Acceptance;

/// <summary>
/// Names the code addresses of a guest runtime fatal report (D1) from the linker map of the runtime image, so a
/// failed CI log shows the faulting function and its callers.
/// </summary>
internal static class RuntimeFatalSymbols
{
    #region Fields

    private static readonly Regex PREFERRED = new(@"Preferred load address is ([0-9a-fA-F]+)");

    private static readonly Regex SYMBOL =
        new(@"^\s*0001:[0-9a-fA-F]{8}\s+(?<name>\S+)\s+(?<address>[0-9a-fA-F]{16})\s+(?:f\s+)?(?<object>\S.*)$");

    private static readonly Regex IMAGE = new(@"^Runtime fatal image base/size/rip RVA: 0x([0-9A-F]+) 0x([0-9A-F]+) 0x([0-9A-F]+)\r?$",
        RegexOptions.Multiline);

    private static readonly Regex STACK = new(@"^Runtime fatal stack RVAs:(?<rvas>( 0x[0-9A-F]+)*)\r?$", RegexOptions.Multiline);

    #endregion

    #region Functions

    /// <summary>
    /// Describes the fatal reports in a serial log: the faulting RVA and the return addresses found on its stack.
    /// </summary>
    /// <param name="serial">Serial log of the boot.</param>
    /// <param name="map">Linker map of the runtime image.</param>
    /// <returns>One line per address; empty when the log holds no fatal report.</returns>
    public static string[] Describe(string serial, string map)
    {
        var images = IMAGE.Matches(serial);
        var stacks = STACK.Matches(serial);
        if (images.Count == 0)
        {
            return [];
        }
        var (preferred, symbols) = ReadMap(map);
        var lines = new List<string>();
        for (var i = 0; i < images.Count; ++i)
        {
            lines.Add("Runtime fatal symbol rip: " + Name(preferred, symbols, Hex(images[i].Groups[3].Value)));
            if (i < stacks.Count)
            {
                foreach (var rva in stacks[i].Groups["rvas"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    lines.Add("Runtime fatal symbol stack: " + Name(preferred, symbols, Hex(rva[2..])));
                }
            }
        }
        return [.. lines];
    }

    #endregion

    #region Tools

    private static (ulong Preferred, (ulong Address, string Name, string Object)[] Symbols) ReadMap(string map)
    {
        var preferred = PREFERRED.Match(map);
        if (!preferred.Success)
        {
            throw new InvalidDataException("Runtime map has no preferred load address.");
        }
        var symbols = map.Split('\n')
            .Select(line => SYMBOL.Match(line.TrimEnd('\r')))
            .Where(match => match.Success)
            .Select(match => (Hex(match.Groups["address"].Value), match.Groups["name"].Value,
                match.Groups["object"].Value.Trim()))
            .OrderBy(symbol => symbol.Item1)
            .ToArray();
        return (Hex(preferred.Groups[1].Value), symbols);
    }

    private static string Name(ulong preferred, (ulong Address, string Name, string Object)[] symbols, ulong rva)
    {
        var address = preferred + rva;
        var index = Array.FindLastIndex(symbols, symbol => symbol.Address <= address);
        return index < 0
            ? $"0x{rva:X} (no symbol)"
            : $"0x{rva:X} {symbols[index].Name}+0x{address - symbols[index].Address:X} ({symbols[index].Object})";
    }

    private static ulong Hex(string text) => ulong.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    #endregion
}
