using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace WitOS.Dev.Pe;

internal static class NativeObject
{
    #region Functions

    // Standard AMD64 COFF only. Import objects and BigObj are rejected explicitly.
    public static CoffObjectInfo Inspect(string path, bool collectReferences = false, bool includeDefinedReferences = false)
    {
        var data = File.ReadAllBytes(path);
        void Range(long offset, long length)
        {
            if (offset < 0 || length < 0 || offset > data.Length || length > data.Length - offset)
                throw new InvalidDataException("COFF range exceeds the object file.");
        }
        ushort U16(int offset)
        { Range(offset, 2); return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2)); }
        uint U32(int offset)
        { Range(offset, 4); return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)); }
        Range(0, 20);
        if (U16(0) != 0x8664 || U16(16) != 0)
            throw new InvalidDataException("Expected ordinary AMD64 COFF.");
        var sectionCount = U16(2);
        var symbolOffset = checked((int)U32(8));
        var symbolCount = checked((int)U32(12));
        if (sectionCount == 0 || symbolCount == 0)
            throw new InvalidDataException("Empty COFF section/symbol table.");
        Range(20, sectionCount * 40L);
        Range(symbolOffset, symbolCount * 18L);
        var strings = checked(symbolOffset + symbolCount * 18);
        var stringSize = checked((int)U32(strings));
        if (stringSize < 4)
            throw new InvalidDataException("Invalid COFF string table.");
        Range(strings, stringSize);
        string LongName(uint offset)
        {
            if (offset < 4 || offset >= stringSize)
                throw new InvalidDataException("Invalid COFF name offset.");
            var start = checked(strings + (int)offset);
            var end = Array.IndexOf(data, (byte)0, start, strings + stringSize - start);
            if (end < 0)
                throw new InvalidDataException("Unterminated COFF name.");
            return Encoding.UTF8.GetString(data, start, end - start);
        }
        string ShortName(int offset)
        {
            Range(offset, 8);
            var bytes = data.AsSpan(offset, 8);
            var end = bytes.IndexOf((byte)0);
            return Encoding.ASCII.GetString(end < 0 ? bytes : bytes[..end]);
        }

        var symbolNames = new string?[symbolCount];
        var owners = new Dictionary<int, List<(uint Offset, string Name)>>();
        var references = collectReferences ? new List<CoffExternalReference>() : null;
        var undefined = new HashSet<string>(StringComparer.Ordinal);
        var defined = new HashSet<string>(StringComparer.Ordinal);
        var exports = new HashSet<string>(StringComparer.Ordinal);
        uint? requiredCpuFeatures = null;
        for (var i = 0; i < symbolCount;)
        {
            var at = checked(symbolOffset + i * 18);
            var name = U32(at) == 0 ? LongName(U32(at + 4)) : ShortName(at);
            var section = unchecked((short)U16(at + 12));
            var storage = data[at + 16];
            var auxiliaries = data[at + 17];
            if (section > sectionCount || auxiliaries >= symbolCount - i)
                throw new InvalidDataException("Invalid COFF symbol/auxiliary count.");
            symbolNames[i] = name;
            if (storage == 2 && section > 0)
                defined.Add(name);
            if (collectReferences && storage == 2 && section > 0)
            {
                if (!owners.TryGetValue(section, out var symbols))
                    owners.Add(section, symbols = []);
                symbols.Add((U32(at + 8), name));
            }
            if ((storage == 2 || storage == 105) && section == 0 && U32(at + 8) == 0)
                undefined.Add(name);
            if (storage == 2 && section > 0 && name.StartsWith("witos_target_", StringComparison.Ordinal))
                exports.Add(name);
            if (name == "g_requiredCpuFeatures" && section > 0)
            {
                var header = 20 + (section - 1) * 40;
                var value = U32(at + 8);
                if (value + 4UL > U32(header + 16))
                    throw new InvalidDataException("CPU feature word is not file-backed.");
                requiredCpuFeatures = U32(checked((int)(U32(header + 20) + value)));
            }
            i += 1 + auxiliaries;
        }

        foreach (var symbols in owners.Values)
            symbols.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        var sections = new List<CoffSectionInfo>();
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < sectionCount; ++i)
        {
            var at = 20 + i * 40;
            var name = ShortName(at);
            if (name.StartsWith('/'))
            {
                if (!uint.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
                    throw new InvalidDataException("Unsupported COFF long section name.");
                name = LongName(offset);
            }
            var rawSize = U32(at + 16);
            var rawOffset = U32(at + 20);
            if (rawOffset != 0)
                Range(rawOffset, rawSize);
            var flags = U32(at + 36);
            var count = (int)U16(at + 32);
            var relocationOffset = checked((int)U32(at + 24));
            var first = 0;
            if ((flags & 0x01000000) != 0)
            {
                if (count != 65535)
                    throw new InvalidDataException("Invalid COFF relocation overflow marker.");
                count = checked((int)U32(relocationOffset));
                if (count <= 65535)
                    throw new InvalidDataException("Invalid COFF relocation overflow count.");
                first = 1;
            }
            Range(relocationOffset, count * 10L);
            for (var r = first; r < count; ++r)
            {
                var rel = checked(relocationOffset + r * 10);
                var symbol = U32(rel + 4);
                var kind = U16(rel + 8);
                if (symbol >= symbolCount || symbolNames[symbol] is null)
                    throw new InvalidDataException("COFF relocation targets a missing/auxiliary symbol.");
                if (references is not null && (undefined.Contains(symbolNames[symbol]!) || (includeDefinedReferences && defined.Contains(symbolNames[symbol]!))))
                {
                    var offset = U32(rel);
                    if (offset >= rawSize)
                        throw new InvalidDataException("External relocation lies outside its section.");
                    string? owner = null;
                    if (owners.TryGetValue(i + 1, out var symbols))
                        foreach (var candidate in symbols)
                        {
                            if (candidate.Offset > offset)
                                break;
                            owner = candidate.Name;
                        }
                    references.Add(new(symbolNames[symbol]!, name, offset, owner, kind));
                }
                var label = kind switch
                {
                    1 => "ADDR64",
                    2 => "ADDR32",
                    3 => "ADDR32NB",
                    4 => "REL32",
                    5 => "REL32_1",
                    6 => "REL32_2",
                    7 => "REL32_3",
                    8 => "REL32_4",
                    9 => "REL32_5",
                    10 => "SECTION",
                    11 => "SECREL",
                    _ => $"0x{kind:X4}"
                };
                kinds[label] = kinds.GetValueOrDefault(label) + 1;
            }
            sections.Add(new CoffSectionInfo(name, rawSize, flags, count - first));
        }
        return new(sectionCount, symbolCount, sections.ToArray(), kinds,
            exports.Order(StringComparer.Ordinal).ToArray(), undefined.Order(StringComparer.Ordinal).ToArray(),
            requiredCpuFeatures, references?.ToArray());
    }

    public static void VerifyArchive(string archivePath, string objectPath)
    {
        var archive = File.ReadAllBytes(archivePath);
        var obj = File.ReadAllBytes(objectPath);
        if (archive.Length < 8 || Encoding.ASCII.GetString(archive, 0, 8) != "!<arch>\n")
            throw new InvalidDataException("NativeLib=Static did not produce a COFF archive.");
        var offset = 8;
        var matches = 0;
        while (offset < archive.Length)
        {
            if (archive.Length - offset < 60 || archive[offset + 58] != (byte)'`' || archive[offset + 59] != 10 ||
                !int.TryParse(Encoding.ASCII.GetString(archive, offset + 48, 10).Trim(),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
                length < 0 || length > archive.Length - offset - 60)
                throw new InvalidDataException("Invalid native archive member.");
            if (archive.AsSpan(offset + 60, length).SequenceEqual(obj))
                ++matches;
            offset = checked(offset + 60 + length + (length & 1));
        }
        if (offset != archive.Length || matches != 1)
            throw new InvalidDataException("Static archive must contain the exact generated object once.");
    }

    #endregion
}
