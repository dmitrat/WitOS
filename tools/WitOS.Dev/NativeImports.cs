using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace WitOS.Dev;

internal sealed record NativeImport(string Library, string[] Symbols);
internal sealed record NativeImageInfo(string Machine, string Subsystem, bool HasClrHeader, int DelayImportDirectorySize, NativeImport[] DirectImports, NativeImport[] DelayImports);

internal static class NativeImports
{
    public static NativeImageInfo Inspect(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new PEReader(stream);
        var headers = reader.PEHeaders;
        var header = headers.PEHeader ?? throw new InvalidDataException("PE header missing.");
        if (headers.CoffHeader.Machine != Machine.Amd64 || header.Magic != PEMagic.PE32Plus)
            throw new InvalidDataException("The native import inventory expects an x64 PE32+ image.");

        int Offset(uint rva, int length)
        {
            long offset = -1;
            if (rva < header.SizeOfHeaders)
                offset = rva;
            else
            {
                foreach (var section in headers.SectionHeaders)
                {
                    var delta = (long)rva - section.VirtualAddress;
                    if (delta >= 0 && delta + length <= section.SizeOfRawData)
                    {
                        offset = section.PointerToRawData + delta;
                        break;
                    }
                }
            }
            if (offset < 0 || length < 0 || offset + length > bytes.Length)
                throw new InvalidDataException($"Invalid image RVA 0x{rva:X}.");
            return checked((int)offset);
        }

        uint U32(uint rva) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(Offset(rva, 4), 4));
        ulong U64(uint rva) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(Offset(rva, 8), 8));
        string CString(uint rva)
        {
            var result = new List<byte>();
            for (uint i = 0; i < 4096; ++i)
            {
                var value = bytes[Offset(checked(rva + i), 1)];
                if (value == 0) return Encoding.ASCII.GetString(result.ToArray());
                result.Add(value);
            }
            throw new InvalidDataException("Unterminated PE import string.");
        }

        string[] Symbols(uint lookup)
        {
            var symbols = new List<string>();
            for (uint i = 0; i < 16384; ++i)
            {
                var thunk = U64(checked(lookup + i * 8));
                if (thunk == 0) return symbols.Order(StringComparer.Ordinal).ToArray();
                symbols.Add((thunk & (1UL << 63)) != 0
                    ? $"ordinal:{thunk & 0xFFFF}"
                    : CString(checked((uint)thunk + 2)));
            }
            throw new InvalidDataException("Unterminated PE import table.");
        }
        NativeImport[] Descriptors(DirectoryEntry directory, bool delay)
        {
            if (directory.Size == 0) return [];
            var imports = new List<NativeImport>();
            var descriptor = checked((uint)directory.RelativeVirtualAddress);
            var stride = delay ? 32 : 20;
            var count = Math.Min(directory.Size / stride, 512);
            for (var index = 0; index < count; ++index)
            {
                var fields = Enumerable.Range(0, stride / 4).Select(i => U32(checked(descriptor + (uint)i * 4))).ToArray();
                if (fields.All(value => value == 0)) return imports.OrderBy(value => value.Library, StringComparer.OrdinalIgnoreCase).ToArray();
                if (delay && fields[0] != 1) throw new InvalidDataException("Only RVA-based PE32+ delay imports are supported.");
                var name = fields[delay ? 1 : 3];
                var iat = fields[delay ? 3 : 4];
                var lookup = fields[delay ? 4 : 0];
                if (name == 0 || iat == 0) throw new InvalidDataException("Incomplete PE import descriptor.");
                if (lookup == 0) lookup = iat;
                imports.Add(new NativeImport(CString(name), Symbols(lookup)));
                descriptor = checked(descriptor + (uint)stride);
            }
            throw new InvalidDataException("Unterminated PE import descriptors.");
        }
        var imports = Descriptors(header.ImportTableDirectory, false);
        var delayed = Descriptors(header.DelayImportTableDirectory, true);

        return new NativeImageInfo(headers.CoffHeader.Machine.ToString(), header.Subsystem.ToString(),
            headers.CorHeader is not null, header.DelayImportTableDirectory.Size,
            imports, delayed);
    }
}
