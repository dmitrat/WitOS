using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Tests.Kernel;

/// <summary>
/// Boot package writer: canonical wire format, unchanged bytes, canonical names and quotas.
/// </summary>
[TestFixture]
public sealed class AssemblyPackageTests
{
    #region Functions

    [Test]
    public void AssemblyPackageCanonicalBytesTest()
    {
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
        Assert.That(package.AsSpan().SequenceEqual(AssemblyPackage.Create(files.Reverse())), Is.True, "Package depends on input enumeration order.");
        Assert.That(package.AsSpan(0, 8).SequenceEqual("WITPAK01"u8) && BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(8)) == 1 &&
            BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(12)) == 32 && BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(16)) == 4 &&
            BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(20)) == 32 && BinaryPrimitives.ReadUInt64LittleEndian(package.AsSpan(24)) == (ulong)package.Length, Is.True, "Package wire header changed.");
        foreach (var (file, index) in files.Select((file, index) => (file, index)))
        {
            var at = 32 + index * 32;
            var nameAt = (int)BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(at));
            var nameBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(at + 4));
            var dataAt = (int)BinaryPrimitives.ReadUInt64LittleEndian(package.AsSpan(at + 8));
            var dataBytes = (int)BinaryPrimitives.ReadUInt64LittleEndian(package.AsSpan(at + 16));
            Assert.That(Encoding.UTF8.GetString(package, nameAt, nameBytes) == file.Name && (dataAt & 7) == 0 && dataBytes == file.Bytes.Length, Is.True, "Wire index/name/alignment mismatch.");
            // A file of at least a page starts at a page boundary (S5.1), so that a loader maps it without a copy.
            Assert.That(file.Bytes.Length < AssemblyPackage.PAGE_BYTES || (dataAt & (AssemblyPackage.PAGE_BYTES - 1)) == 0, Is.True, "Page-sized file is not page-aligned.");
            Assert.That(SHA256.HashData(package.AsSpan(dataAt, dataBytes)).AsSpan().SequenceEqual(SHA256.HashData(file.Bytes.Span)), Is.True, "Packaged file bytes changed.");
            Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(package.AsSpan(at + 24)) == 0, Is.True, "Reserved index fields are nonzero.");
        }
        foreach (var name in new[] { "", "/app", "app/", "app//file", ".", "..", "app/../file", "app/./file", "C:/file", "app\\file", "a\0b", "\ud800", new string('x', 1025) })
            Reject(new[] { (name, ReadOnlyMemory<byte>.Empty) });
        Reject(new[] { ("same", ReadOnlyMemory<byte>.Empty), ("same", ReadOnlyMemory<byte>.Empty) });
        Reject(Enumerable.Range(0, AssemblyPackage.MAXIMUM_FILES + 1).Select(i => (i.ToString(), ReadOnlyMemory<byte>.Empty)));
        Reject(new[] { (new string('\u03bb', 513), ReadOnlyMemory<byte>.Empty) });
        Reject(new[] { ("a", ReadOnlyMemory<byte>.Empty), ("a.b", ReadOnlyMemory<byte>.Empty), ("a/child", ReadOnlyMemory<byte>.Empty) });
        var megabyte = new byte[1024 * 1024];
        Reject(Enumerable.Range(0, 129).Select(i => (i.ToString(), (ReadOnlyMemory<byte>)megabyte)));
        Assert.That(AssemblyPackage.Create(Enumerable.Range(0, AssemblyPackage.MAXIMUM_FILES).Select(i => (i.ToString(), ReadOnlyMemory<byte>.Empty))).Length > 32, Is.True, "Exact file quota rejected.");
        Assert.That(AssemblyPackage.Create(Array.Empty<(string, ReadOnlyMemory<byte>)>()).Length == 32, Is.True, "Empty package is not canonical.");
    }

    #endregion
}
