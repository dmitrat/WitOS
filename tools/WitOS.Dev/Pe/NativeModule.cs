using System.Buffers.Binary;
using System.Reflection.PortableExecutable;

namespace WitOS.Dev.Pe;

internal static class NativeModule
{
    #region Functions

    public static NativeModuleInfo Inspect(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var headers = pe.PEHeaders;
        var header = headers.PEHeader ?? throw new InvalidDataException("Missing PE image header.");
        if (headers.CoffHeader.Machine != Machine.Amd64 || header.Magic != PEMagic.PE32Plus ||
            headers.CorHeader is not null || header.SizeOfImage <= 0)
            throw new InvalidDataException("Expected a native x64 PE32+ module.");
        byte[] Read(uint rva, int size)
        {
            if (size < 0 || rva >= header.SizeOfImage || (ulong)rva + (uint)size > (uint)header.SizeOfImage)
                throw new InvalidDataException("PE metadata range exceeds the image.");
            return pe.GetSectionData(checked((int)rva)).GetContent(0, size).ToArray();
        }
        uint Va(ulong address)
        {
            if (address < header.ImageBase || address - header.ImageBase >= (ulong)header.SizeOfImage)
                throw new InvalidDataException("PE metadata VA exceeds the image.");
            return checked((uint)(address - header.ImageBase));
        }
        bool Executable(uint rva) => headers.SectionHeaders.Any(s => rva >= s.VirtualAddress &&
            (ulong)rva < (ulong)s.VirtualAddress + (uint)s.VirtualSize &&
            s.SectionCharacteristics.HasFlag(SectionCharacteristics.MemExecute));

        var sections = headers.SectionHeaders.Select(s => new NativeSection(s.Name, s.VirtualAddress,
            s.VirtualSize, s.SizeOfRawData,
            (s.SectionCharacteristics.HasFlag(SectionCharacteristics.MemRead) ? "R" : "") +
            (s.SectionCharacteristics.HasFlag(SectionCharacteristics.MemWrite) ? "W" : "") +
            (s.SectionCharacteristics.HasFlag(SectionCharacteristics.MemExecute) ? "X" : ""))).ToArray();
        NativeTlsInfo? tls = null;
        var directory = header.ThreadLocalStorageTableDirectory;
        if (directory.Size != 0)
        {
            if (directory.Size < 40)
                throw new InvalidDataException("Truncated PE TLS directory.");
            var bytes = Read(checked((uint)directory.RelativeVirtualAddress), 40);
            var start = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0, 8));
            var end = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8, 8));
            var index = Va(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16, 8)));
            var callbacks = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(24, 8));
            var zero = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32, 4));
            if (end < start)
                throw new InvalidDataException("Invalid TLS template extent.");
            if (end != start)
                _ = Read(Va(start), checked((int)(end - start)));
            var entries = new List<uint>();
            if (callbacks != 0)
            {
                var callbackRva = Va(callbacks);
                var terminated = false;
                for (uint i = 0; i < 64; ++i)
                {
                    var target = BinaryPrimitives.ReadUInt64LittleEndian(Read(checked(callbackRva + i * 8), 8));
                    if (target == 0)
                    { terminated = true; break; }
                    var rva = Va(target);
                    if (!Executable(rva))
                        throw new InvalidDataException("TLS callback is not executable.");
                    entries.Add(rva);
                }
                if (!terminated)
                    throw new InvalidDataException("Unterminated TLS callback array.");
            }
            tls = new(end - start, zero, index, entries.ToArray());
        }
        var relocations = new Dictionary<string, int>(StringComparer.Ordinal);
        directory = header.BaseRelocationTableDirectory;
        if (directory.Size != 0)
        {
            var bytes = Read(checked((uint)directory.RelativeVirtualAddress), directory.Size);
            for (var offset = 0; offset < bytes.Length;)
            {
                if (bytes.Length - offset < 8)
                    throw new InvalidDataException("Truncated base relocation block.");
                var page = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
                var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4)));
                if (size < 8 || (size & 1) != 0 || size > bytes.Length - offset)
                    throw new InvalidDataException("Invalid base relocation block length.");
                for (var position = offset + 8; position < offset + size; position += 2)
                {
                    var item = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position, 2));
                    var kind = item >> 12;
                    if (kind == 0)
                        continue;
                    if ((ulong)page + (uint)(item & 4095) >= (uint)header.SizeOfImage)
                        throw new InvalidDataException("Base relocation target exceeds image.");
                    var name = kind == 10 ? "DIR64" : $"type:{kind}";
                    relocations[name] = relocations.GetValueOrDefault(name) + 1;
                }
                offset += size;
            }
        }
        directory = header.ExceptionTableDirectory;
        if (directory.Size % 12 != 0)
            throw new InvalidDataException("Invalid x64 runtime function table.");
        if (directory.Size != 0)
            _ = Read(checked((uint)directory.RelativeVirtualAddress), directory.Size);
        return new(header.ImageBase, header.SizeOfImage, header.AddressOfEntryPoint,
            directory.Size / 12, sections, tls, relocations);
    }

    #endregion
}
