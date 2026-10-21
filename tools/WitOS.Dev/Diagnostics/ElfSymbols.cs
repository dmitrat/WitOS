using System.Buffers.Binary;
using System.Text;

namespace WitOS.Dev.Diagnostics;

/// <summary>
/// The symbols of an ELF64 image (plan step P3): its .symtab where it keeps one, its .dynsym otherwise, by value
/// relative to the image's first byte, so that an address in a guest's module names its function.
/// </summary>
internal sealed class ElfSymbols
{
    #region Constants

    private const uint SHT_SYMTAB = 2;

    private const uint SHT_DYNSYM = 11;

    private const int STT_FUNC = 2;

    private const int STT_OBJECT = 1;

    #endregion

    #region Fields

    private readonly List<(ulong Value, ulong Size, string Name)> m_symbols;

    #endregion

    #region Constructors

    private ElfSymbols(List<(ulong Value, ulong Size, string Name)> symbols)
    {
        m_symbols = symbols;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Reads an image's functions and objects.
    /// </summary>
    /// <param name="path">The image.</param>
    /// <returns>Its symbols.</returns>
    /// <exception cref="InvalidDataException">The file is no little-endian ELF64 image.</exception>
    public static ElfSymbols Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 64 || bytes[0] != 0x7F || bytes[1] != 'E' || bytes[2] != 'L' || bytes[3] != 'F' || bytes[4] != 2 || bytes[5] != 1)
            throw new InvalidDataException($"{path} is no little-endian ELF64 image.");
        var sections = (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(40));
        var entry = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(58));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(60));
        Section Header(int index)
        {
            var at = bytes.AsSpan((int)(sections + index * entry));
            return new Section(BinaryPrimitives.ReadUInt32LittleEndian(at[4..]), (long)BinaryPrimitives.ReadUInt64LittleEndian(at[24..]),
                (long)BinaryPrimitives.ReadUInt64LittleEndian(at[32..]), BinaryPrimitives.ReadUInt32LittleEndian(at[40..]),
                (long)BinaryPrimitives.ReadUInt64LittleEndian(at[56..]));
        }
        var all = Enumerable.Range(0, count).Select(Header).ToList();
        var table = all.FirstOrDefault(section => section.Type == SHT_SYMTAB) ?? all.FirstOrDefault(section => section.Type == SHT_DYNSYM);
        var symbols = new List<(ulong, ulong, string)>();
        if (table is not null)
        {
            var strings = all[(int)table.Link];
            for (var at = table.Offset; at + 24 <= table.Offset + table.Size; at += Math.Max(24, table.EntrySize))
            {
                var symbol = bytes.AsSpan((int)at);
                var type = symbol[4] & 0xF;
                var value = BinaryPrimitives.ReadUInt64LittleEndian(symbol[8..]);
                if ((type != STT_FUNC && type != STT_OBJECT) || value == 0)
                    continue;
                var name = strings.Offset + BinaryPrimitives.ReadUInt32LittleEndian(symbol);
                var end = Array.IndexOf(bytes, (byte)0, (int)name);
                symbols.Add((value, BinaryPrimitives.ReadUInt64LittleEndian(symbol[16..]), Encoding.UTF8.GetString(bytes, (int)name, end - (int)name)));
            }
        }
        symbols.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return new ElfSymbols(symbols);
    }

    /// <summary>
    /// A symbol's value.
    /// </summary>
    /// <param name="name">The symbol.</param>
    /// <returns>Its value, or null.</returns>
    public ulong? Value(string name)
    {
        foreach (var symbol in m_symbols)
        {
            if (symbol.Name == name)
                return symbol.Value;
        }
        return null;
    }

    /// <summary>
    /// The function an offset into the image lies in, with the offset from its start.
    /// </summary>
    /// <param name="offset">The offset from the image's first byte.</param>
    /// <returns>"name+0x…", or null when no symbol covers it.</returns>
    public string? Name(ulong offset)
    {
        (ulong Value, ulong Size, string Name)? best = null;
        foreach (var symbol in m_symbols)
        {
            if (symbol.Value > offset)
                break;
            best = symbol;
        }
        if (best is not { } found || (found.Size != 0 && offset >= found.Value + found.Size + 0x1000))
            return null;
        return $"{found.Name}+0x{offset - found.Value:x}";
    }

    #endregion

    #region Nested Types

    private sealed record Section(uint Type, long Offset, long Size, uint Link, long EntrySize);

    #endregion
}
