using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace WitOS.Dev;

internal sealed record NativeImport(string Library, string[] Symbols);
internal sealed record NativeImageInfo(string Machine, string Subsystem, bool HasClrHeader, int DelayImportDirectorySize, NativeImport[] DirectImports);

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
            throw new InvalidDataException("The NativeAOT experiment expects an x64 PE32+ image.");

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

        var imports = new List<NativeImport>();
        if (header.ImportTableDirectory.Size != 0)
        {
            var descriptor = checked((uint)header.ImportTableDirectory.RelativeVirtualAddress);
            var terminated = false;
            var descriptorCount = Math.Min(header.ImportTableDirectory.Size / 20, 512);
            for (var index = 0; index < descriptorCount; ++index)
            {
                var lookup = U32(descriptor);
                var name = U32(checked(descriptor + 12));
                var firstThunk = U32(checked(descriptor + 16));
                if (lookup == 0 && name == 0 && firstThunk == 0)
                {
                    terminated = true;
                    break;
                }
                if (name == 0 || firstThunk == 0) throw new InvalidDataException("Incomplete PE import descriptor.");
                if (lookup == 0) lookup = firstThunk;
                var symbols = new List<string>();
                var thunkTerminated = false;
                for (uint i = 0; i < 16384; ++i)
                {
                    var thunk = U64(checked(lookup + i * 8));
                    if (thunk == 0) { thunkTerminated = true; break; }
                    symbols.Add((thunk & (1UL << 63)) != 0
                        ? $"ordinal:{thunk & 0xFFFF}"
                        : CString(checked((uint)thunk + 2)));
                }
                if (!thunkTerminated) throw new InvalidDataException("Unterminated PE import table.");
                imports.Add(new NativeImport(CString(name), symbols.Order(StringComparer.Ordinal).ToArray()));
                descriptor = checked(descriptor + 20);
            }
            if (!terminated) throw new InvalidDataException("Unterminated PE import descriptors.");
        }

        return new NativeImageInfo(headers.CoffHeader.Machine.ToString(), header.Subsystem.ToString(),
            headers.CorHeader is not null, header.DelayImportTableDirectory.Size,
            imports.OrderBy(value => value.Library, StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
