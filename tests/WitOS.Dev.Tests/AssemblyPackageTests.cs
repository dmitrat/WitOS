using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using WitOS.Dev.Kernel;

internal static class AssemblyPackageTests
{
    internal static Task RunAsync()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidDataException(message); }
        static void Reject(IEnumerable<(string Name, ReadOnlyMemory<byte> Bytes)> files)
        {
            try
            { AssemblyPackage.Create(files); }
            catch (InvalidDataException) { return; }
            throw new InvalidDataException("Invalid package input was accepted.");
        }
        var binary = Enumerable.Range(0, 4099).Select(i => (byte)i).ToArray();
        var files = new (string Name, ReadOnlyMemory<byte> Bytes)[]
        {
            ("app/Probe.dll", binary), ("app/Probe.runtimeconfig.json", "{\"runtimeOptions\":{}}"u8.ToArray()),
            ("empty", Array.Empty<byte>()), ("resources/\u03bb.txt", "payload"u8.ToArray())
        };
        var package = AssemblyPackage.Create(files);
        Require(package.AsSpan().SequenceEqual(AssemblyPackage.Create(files.Reverse())), "Package depends on input enumeration order.");
        Require(package.AsSpan(0, 8).SequenceEqual("WITPAK01"u8) && BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(8)) == 1 &&
            BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(12)) == 32 && BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(16)) == 4 &&
            BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(20)) == 32 && BinaryPrimitives.ReadUInt64LittleEndian(package.AsSpan(24)) == (ulong)package.Length,
            "Package wire header changed.");
        foreach (var (file, index) in files.Select((file, index) => (file, index)))
        {
            var at = 32 + index * 32;
            var nameAt = (int)BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(at));
            var nameBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(at + 4));
            var dataAt = (int)BinaryPrimitives.ReadUInt64LittleEndian(package.AsSpan(at + 8));
            var dataBytes = (int)BinaryPrimitives.ReadUInt64LittleEndian(package.AsSpan(at + 16));
            Require(Encoding.UTF8.GetString(package, nameAt, nameBytes) == file.Name && (dataAt & 7) == 0 && dataBytes == file.Bytes.Length,
                "Wire index/name/alignment mismatch.");
            Require(SHA256.HashData(package.AsSpan(dataAt, dataBytes)).AsSpan().SequenceEqual(SHA256.HashData(file.Bytes.Span)), "Packaged file bytes changed.");
            Require(BinaryPrimitives.ReadUInt64LittleEndian(package.AsSpan(at + 24)) == 0, "Reserved index fields are nonzero.");
        }
        foreach (var name in new[] { "", "/app", "app/", "app//file", ".", "..", "app/../file", "app/./file", "C:/file", "app\\file", "a\0b", "\ud800", new string('x', 1025) })
            Reject(new[] { (name, ReadOnlyMemory<byte>.Empty) });
        Reject(new[] { ("same", ReadOnlyMemory<byte>.Empty), ("same", ReadOnlyMemory<byte>.Empty) });
        Reject(Enumerable.Range(0, AssemblyPackage.MaximumFiles + 1).Select(i => (i.ToString(), ReadOnlyMemory<byte>.Empty)));
        Reject(new[] { (new string('\u03bb', 513), ReadOnlyMemory<byte>.Empty) });
        Reject(new[] { ("a", ReadOnlyMemory<byte>.Empty), ("a.b", ReadOnlyMemory<byte>.Empty), ("a/child", ReadOnlyMemory<byte>.Empty) });
        var megabyte = new byte[1024 * 1024];
        Reject(Enumerable.Range(0, 129).Select(i => (i.ToString(), (ReadOnlyMemory<byte>)megabyte)));
        Require(AssemblyPackage.Create(Enumerable.Range(0, AssemblyPackage.MaximumFiles).Select(i => (i.ToString(), ReadOnlyMemory<byte>.Empty))).Length > 32,
            "Exact file quota rejected.");
        Require(AssemblyPackage.Create(Array.Empty<(string, ReadOnlyMemory<byte>)>()).Length == 32, "Empty package is not canonical.");
        return Task.CompletedTask;
    }
}
