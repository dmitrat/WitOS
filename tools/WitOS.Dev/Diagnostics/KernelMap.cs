using System.Globalization;
using System.Text.RegularExpressions;

namespace WitOS.Dev.Diagnostics;

/// <summary>
/// The kernel's link map (lld-link's /map, WitOS.map beside each scenario's image, plan step P3): every symbol the
/// linker records, public and static, with its address at the preferred base and the object it comes from.
/// </summary>
internal sealed partial class KernelMap
{
    #region Fields

    private readonly List<(string Name, string Object, ulong Rva)> m_symbols;

    #endregion

    #region Constructors

    private KernelMap(List<(string Name, string Object, ulong Rva)> symbols)
    {
        m_symbols = symbols;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Reads a map.
    /// </summary>
    /// <param name="path">The map.</param>
    /// <returns>Its symbols.</returns>
    /// <exception cref="InvalidDataException">The map names no preferred load address.</exception>
    public static KernelMap Read(string path)
    {
        var text = File.ReadAllText(path);
        var preferred = PreferredBase().Match(text);
        if (!preferred.Success)
            throw new InvalidDataException($"{path} names no preferred load address.");
        var imageBase = ulong.Parse(preferred.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var symbols = new List<(string, string, ulong)>();
        foreach (Match line in SymbolLine().Matches(text))
        {
            var address = ulong.Parse(line.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (address >= imageBase)
                symbols.Add((line.Groups[1].Value, line.Groups[3].Value, address - imageBase));
        }
        return new KernelMap(symbols);
    }

    /// <summary>
    /// A symbol's address relative to the image's base.
    /// </summary>
    /// <param name="name">The symbol.</param>
    /// <param name="objectName">The object's name, such as "user" for kernel-common.user.obj, where a static name
    /// repeats; null for any.</param>
    /// <returns>Its relative address.</returns>
    /// <exception cref="KeyNotFoundException">No symbol or several have the name.</exception>
    public ulong Rva(string name, string? objectName = null)
    {
        var found = m_symbols.Where(symbol => symbol.Name == name &&
            (objectName is null || symbol.Object.EndsWith("." + objectName + ".obj", StringComparison.Ordinal))).ToList();
        return found.Count == 1
            ? found[0].Rva
            : throw new KeyNotFoundException($"{found.Count} symbols {name}{(objectName is null ? "" : " of " + objectName)} in the map.");
    }

    [GeneratedRegex(@"Preferred load address is ([0-9a-fA-F]+)")]
    private static partial Regex PreferredBase();

    [GeneratedRegex(@"^\s*[0-9a-fA-F]{4}:[0-9a-fA-F]+\s+(\S+)\s+([0-9a-fA-F]{16})\s+(\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex SymbolLine();

    #endregion
}
