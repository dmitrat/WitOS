using System.Buffers.Binary;
using System.Text;

namespace WitOS.Dev;

// Private bring-up storage format. Packaging preserves file bytes; it does not
// compile assemblies or define runtime/framework binding policy.
internal static class AssemblyPackage
{
    internal const int HeaderBytes = 32, EntryBytes = 32, MaximumFiles = 1024;
    internal const int MaximumNameBytes = 1024, MaximumBytes = 128 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Create(IEnumerable<(string Name, ReadOnlyMemory<byte> Bytes)> files)
    {
        var entries = new List<(byte[] Name, ReadOnlyMemory<byte> Bytes)>();
        foreach (var file in files)
        {
            if (entries.Count == MaximumFiles) throw new InvalidDataException("Assembly package file quota exceeded.");
            if (string.IsNullOrEmpty(file.Name) || file.Name.Any(c => c < 32 || c == 127 || c == '\\' || c == ':') ||
                file.Name.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
                throw new InvalidDataException("Package names must be canonical relative paths.");
            byte[] name;
            try { name = Utf8.GetBytes(file.Name); }
            catch (EncoderFallbackException error) { throw new InvalidDataException("Invalid UTF-8 package name.", error); }
            if (name.Length > MaximumNameBytes) throw new InvalidDataException("Package name exceeds the byte quota.");
            entries.Add((name, file.Bytes));
        }
        entries.Sort((a, b) => a.Name.AsSpan().SequenceCompareTo(b.Name));
        for (var i = 1; i < entries.Count; ++i)
            if (entries[i - 1].Name.AsSpan().SequenceEqual(entries[i].Name))
                throw new InvalidDataException("Duplicate package name.");
        var names = entries.Select(entry => Utf8.GetString(entry.Name)).ToHashSet(StringComparer.Ordinal);
        foreach (var name in names)
            for (var slash = name.IndexOf('/'); slash >= 0; slash = name.IndexOf('/', slash + 1))
                if (names.Contains(name[..slash])) throw new InvalidDataException("A package file cannot also be a directory.");
        long namesEnd = HeaderBytes + entries.Count * EntryBytes;
        foreach (var entry in entries) namesEnd = checked(namesEnd + entry.Name.Length);
        long size = Align(namesEnd);
        foreach (var entry in entries)
        {
            size = Align(checked(size + entry.Bytes.Length));
            if (size > MaximumBytes) throw new InvalidDataException("Assembly package byte quota exceeded.");
        }
        var output = new byte[checked((int)size)];
        "WITPAK01"u8.CopyTo(output);
        Put32(output, 8, 1); Put32(output, 12, HeaderBytes);
        Put32(output, 16, entries.Count); Put32(output, 20, EntryBytes); Put64(output, 24, size);
        var nameAt = HeaderBytes + entries.Count * EntryBytes;
        var dataAt = checked((int)Align(namesEnd));
        for (var i = 0; i < entries.Count; ++i)
        {
            var (name, data) = entries[i]; var at = HeaderBytes + i * EntryBytes;
            Put32(output, at, nameAt); Put32(output, at + 4, name.Length);
            Put64(output, at + 8, dataAt); Put64(output, at + 16, data.Length);
            // Flags/reserved words and alignment padding remain zero.
            name.CopyTo(output, nameAt); data.Span.CopyTo(output.AsSpan(dataAt));
            nameAt += name.Length; dataAt = checked((int)Align((long)dataAt + data.Length));
        }
        return output;
    }
    private static long Align(long value) => checked(value + 7) & ~7L;
    private static void Put32(byte[] output, int offset, int value) => BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset), checked((uint)value));
    private static void Put64(byte[] output, int offset, long value) => BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset), checked((ulong)value));
}
