using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Kernel;

/// <summary>
/// The Linux arm64 boot path's files (plan step H6): a PE image the pinned clang and lld-link build for ARM64 laid out
/// flat with the protocol's header — the "MZ" NOP, the branch to the entry the link map names, the image's size, the
/// flags and the magic — that stays a PE image whose sections lie where they are loaded; and the initial ramdisk's
/// header and page-aligned parts.
/// </summary>
[TestFixture]
public sealed class LinuxImageTests
{
    #region Functions

    [Test]
    public async Task FlatImageCarriesTheHeaderAndStaysAPeImageTest()
    {
        var root = TestEnvironment.Root;
        if (!File.Exists(Toolchain.Clang(root)) || !File.Exists(Toolchain.LldLink(root)))
            Assert.Ignore("The pinned clang is not extracted: run setup.");
        var output = Path.Combine(Path.GetTempPath(), $"witos-linux-image-{Environment.ProcessId}");
        Directory.CreateDirectory(output);
        try
        {
            var source = Path.Combine(output, "image.c");
            // A pointer in data wants a base relocation; the zeros of a large array lie beyond the file's data.
            await File.WriteAllTextAsync(source, "int value = 7;\nint *const table[] = {&value, 0};\nstatic char zeros[3 * 4096];\n" +
                "void wit_linux_entry(void) { zeros[1] = 1; }\n" +
                "int efi_main(void *image, void *system) { (void)image; (void)system; return *table[0] + zeros[2]; }\n");
            var obj = Path.Combine(output, "image.obj");
            var architecture = KernelArchitecture.Arm64;
            await Processes.RequireSuccessAsync(Toolchain.Clang(root),
                [$"--target={architecture.KernelTriple}", "-c", "-ffreestanding", "-nostdinc", "-O0", source, "-o", obj], root);
            var efi = Path.Combine(output, "image.efi");
            var map = Path.Combine(output, "image.map");
            await Processes.RequireSuccessAsync(Toolchain.LldLink(root),
                ["/nologo", "/subsystem:efi_application", "/entry:efi_main", "/nodefaultlib", "/machine:arm64", "/fixed:no", $"/out:{efi}", $"/map:{map}", obj], root);
            var pe = await File.ReadAllBytesAsync(efi);
            var entry = LinuxImage.EntryRva(await File.ReadAllLinesAsync(map), pe);

            var image = LinuxImage.FromPe(pe, entry);

            using var original = new PEReader(new MemoryStream(pe));
            using var flat = new PEReader(new MemoryStream(image));
            Assert.That(image.Length, Is.EqualTo(original.PEHeaders.PEHeader!.SizeOfImage));
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image), Is.EqualTo(LinuxImage.MZ_NOP));
            Assert.That((image[0], image[1]), Is.EqualTo(((byte)'M', (byte)'Z')), "the NOP's bytes are the DOS signature");
            var branch = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(4));
            Assert.That((branch >> 26, 4 + (branch & 0x03FFFFFF) * 4), Is.EqualTo((5U, entry)), "b to the entry");
            Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(8)), Is.Zero, "text_offset");
            Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(16)), Is.EqualTo((ulong)image.Length), "image_size");
            Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(24)), Is.EqualTo(LinuxImage.FLAGS));
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(56)), Is.EqualTo(LinuxImage.MAGIC));
            Assert.That(BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(60)), Is.EqualTo(BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(60))),
                "the PE header's offset");

            var headers = flat.PEHeaders;
            Assert.That((headers.PEHeader!.FileAlignment, headers.PEHeader.SectionAlignment, headers.PEHeader.SizeOfHeaders),
                Is.EqualTo((0x1000, 0x1000, 0x1000)));
            Assert.That(headers.PEHeader.AddressOfEntryPoint, Is.EqualTo(original.PEHeaders.PEHeader.AddressOfEntryPoint), "UEFI's entry is kept");
            foreach (var section in original.PEHeaders.SectionHeaders)
            {
                var laid = headers.SectionHeaders.Single(other => other.Name == section.Name);
                Assert.That(laid.PointerToRawData, Is.EqualTo(laid.VirtualAddress), $"{section.Name} lies where it is loaded");
                var bytes = Math.Min(section.SizeOfRawData, section.VirtualSize);
                Assert.That(image.AsSpan(section.VirtualAddress, bytes).SequenceEqual(pe.AsSpan(section.PointerToRawData, bytes)), Is.True,
                    $"{section.Name}'s bytes");
            }
            Assert.Throws<InvalidDataException>(() => LinuxImage.FromPe(pe, entry + 2), "an entry that is no instruction");
            Assert.Throws<InvalidDataException>(() => LinuxImage.EntryRva(["no entry here"], pe));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Test]
    public void RamdiskHoldsTheRootTaskAndThePackageAtPagesTest()
    {
        var root = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        var package = Enumerable.Range(0, 100).Select(i => (byte)(i + 1)).ToArray();

        var ramdisk = InitialRamdisk.Create(root, package);

        Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(ramdisk), Is.EqualTo(InitialRamdisk.MAGIC));
        Assert.That(System.Text.Encoding.ASCII.GetString(ramdisk, 0, 7), Is.EqualTo("WITSRD1"));
        Assert.That((BinaryPrimitives.ReadUInt32LittleEndian(ramdisk.AsSpan(8)), BinaryPrimitives.ReadUInt32LittleEndian(ramdisk.AsSpan(12))),
            Is.EqualTo((InitialRamdisk.VERSION, (uint)InitialRamdisk.HEADER_BYTES)));
        var rootOffset = BinaryPrimitives.ReadInt64LittleEndian(ramdisk.AsSpan(16));
        var packageOffset = BinaryPrimitives.ReadInt64LittleEndian(ramdisk.AsSpan(32));
        Assert.That((rootOffset, BinaryPrimitives.ReadInt64LittleEndian(ramdisk.AsSpan(24))), Is.EqualTo((4096L, 5000L)));
        Assert.That((packageOffset, BinaryPrimitives.ReadInt64LittleEndian(ramdisk.AsSpan(40))), Is.EqualTo((12288L, 100L)), "the next page after the root task");
        Assert.That(ramdisk.AsSpan((int)rootOffset, root.Length).SequenceEqual(root), Is.True);
        Assert.That(ramdisk.AsSpan((int)packageOffset, package.Length).SequenceEqual(package), Is.True);
        Assert.That(ramdisk.Length % 4096, Is.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => InitialRamdisk.Create([], package));
    }

    #endregion
}
