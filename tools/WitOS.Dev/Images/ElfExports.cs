using System.Buffers.Binary;
using System.Text;

namespace WitOS.Dev.Images;

/// <summary>
/// The names a 64-bit little-endian ELF shared object exports (plan step N3.3): the defined global and weak symbols of its
/// dynamic symbol table whose visibility lets another module bind to them.
/// </summary>
internal static class ElfExports
{
    #region Constants

    private const int SHT_DYNSYM = 11;
    private const int SYMBOL_SIZE = 24;
    private const int SECTION_HEADER_SIZE = 64;
    private const byte BIND_GLOBAL = 1;
    private const byte BIND_WEAK = 2;
    private const byte VISIBILITY_DEFAULT = 0;
    private const byte VISIBILITY_PROTECTED = 3;

    #endregion

    #region Functions

    /// <summary>
    /// Reads the names an ELF shared object exports.
    /// </summary>
    /// <param name="path">The shared object.</param>
    /// <returns>The names, sorted ordinally and without repeats.</returns>
    /// <exception cref="InvalidDataException">The file is no 64-bit little-endian ELF object with a dynamic symbol
    /// table.</exception>
    public static IReadOnlyList<string> Read(string path)
    {
        var image = File.ReadAllBytes(path);
        if (image.Length < 64 || image[0] != 0x7F || image[1] != (byte)'E' || image[2] != (byte)'L' || image[3] != (byte)'F' ||
            image[4] != 2 || image[5] != 1)
            throw new InvalidDataException($"{path} is no 64-bit little-endian ELF object.");
        var sections = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x28)));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x3C));
        for (var index = 0; index < count; ++index)
        {
            var header = image.AsSpan(sections + index * SECTION_HEADER_SIZE, SECTION_HEADER_SIZE);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != SHT_DYNSYM)
                continue;
            var symbols = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(header[0x18..]));
            var size = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(header[0x20..]));
            var link = BinaryPrimitives.ReadUInt32LittleEndian(header[0x28..]);
            var strings = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(sections + (int)link * SECTION_HEADER_SIZE + 0x18)));
            var names = new SortedSet<string>(StringComparer.Ordinal);
            for (var offset = symbols + SYMBOL_SIZE; offset < symbols + size; offset += SYMBOL_SIZE)
            {
                var symbol = image.AsSpan(offset, SYMBOL_SIZE);
                var bind = (byte)(symbol[4] >> 4);
                var visibility = (byte)(symbol[5] & 3);
                var section = BinaryPrimitives.ReadUInt16LittleEndian(symbol[6..]);
                if (section == 0 || (bind != BIND_GLOBAL && bind != BIND_WEAK) ||
                    (visibility != VISIBILITY_DEFAULT && visibility != VISIBILITY_PROTECTED))
                    continue;
                var name = strings + (int)BinaryPrimitives.ReadUInt32LittleEndian(symbol);
                var end = Array.IndexOf(image, (byte)0, name);
                names.Add(Encoding.ASCII.GetString(image, name, end - name));
            }
            return [.. names];
        }
        throw new InvalidDataException($"{path} has no dynamic symbol table.");
    }

    #endregion
}
