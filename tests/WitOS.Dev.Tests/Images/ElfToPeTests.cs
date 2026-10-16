using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using WitOS.Dev.Host;
using WitOS.Dev.Images;
using WitOS.Dev.Kernel;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Images;

/// <summary>
/// The ELF to PE32+ conversion of the RISC-V kernel image (plan step V1.1): a position-independent riscv64 ELF the
/// pinned clang and lld build with the kernel's linker script becomes a PE32+ the loader's own header check accepts,
/// with its relative relocations as base relocations.
/// </summary>
[TestFixture]
public sealed class ElfToPeTests
{
    #region Functions

    [Test]
    public async Task ConvertsAPositionIndependentImageTest()
    {
        var root = TestEnvironment.Root;
        Toolchain.RequireClang(root);
        var output = Path.Combine(Path.GetTempPath(), $"witos-elftope-{Environment.ProcessId}");
        Directory.CreateDirectory(output);
        var source = Path.Combine(output, "image.c");
        // A pointer in data wants a relative relocation; the entry returns through it.
        await File.WriteAllTextAsync(source, "int value = 7;\nint *const table[] = {&value, 0};\nconst char text[] = \"WitOS\";\n" +
            "int efi_main(void *image, void *system) { (void)image; (void)system; return *table[0] + text[0]; }\n");
        var obj = Path.Combine(output, "image.o");
        var architecture = KernelArchitecture.Riscv64;
        await Processes.RequireSuccessAsync(Toolchain.Clang(root),
            [$"--target={architecture.KernelTriple}", .. architecture.KernelCompileOptions, "-c", "-ffreestanding", "-nostdinc", "-O0", source, "-o", obj], root);
        var elf = Path.Combine(output, "image.elf");
        await Processes.RequireSuccessAsync(Toolchain.Lld(root),
        [
            "-pie", "--no-dynamic-linker", "-z", "separate-loadable-segments", "-z", "max-page-size=4096", "-z", "norelro", "--build-id=none",
            "-T", Path.Combine(root, architecture.LinkerScript!), "-e", "efi_main", "-o", elf, obj
        ], root);

        var pe = ElfToPe.Convert(await File.ReadAllBytesAsync(elf), architecture.ElfMachine, (ushort)architecture.Machine);
        using var reader = new PEReader(new MemoryStream(pe));
        var headers = reader.PEHeaders;
        Assert.That(headers.CoffHeader.Machine, Is.EqualTo(architecture.Machine));
        Assert.That((int)headers.PEHeader!.Subsystem, Is.EqualTo(10), "An EFI application");
        Assert.That(headers.PEHeader.Magic, Is.EqualTo(PEMagic.PE32Plus));
        Assert.That(headers.PEHeader.SectionAlignment, Is.EqualTo(0x1000));
        Assert.That(headers.PEHeader.SizeOfHeaders, Is.EqualTo(0x1000));
        Assert.That(headers.PEHeader.ImportTableDirectory.Size, Is.EqualTo(0));
        Assert.That(headers.SectionHeaders.Length, Is.GreaterThanOrEqualTo(3), "Code, data and the relocations at least");
        var code = headers.SectionHeaders.Single(section => (section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0);
        Assert.That(headers.PEHeader.AddressOfEntryPoint, Is.InRange(code.VirtualAddress, code.VirtualAddress + code.VirtualSize - 1));
        Assert.That(headers.SectionHeaders.All(section => (section.SectionCharacteristics & SectionCharacteristics.MemWrite) == 0 ||
            (section.SectionCharacteristics & SectionCharacteristics.MemExecute) == 0), Is.True, "No section is writable and executable");
        Assert.That(headers.SectionHeaders.All(section => section.VirtualAddress % 0x1000 == 0 && section.PointerToRawData % 0x1000 == 0), Is.True);

        // The relocation table holds the pointer's fixup, and the pointer's place holds its addend.
        var relocations = headers.PEHeader.BaseRelocationTableDirectory;
        Assert.That(relocations.Size, Is.GreaterThanOrEqualTo(8));
        var reloc = headers.SectionHeaders.Single(section => section.Name == ".reloc");
        var block = pe.AsMemory(reloc.PointerToRawData, relocations.Size);
        var page = BinaryPrimitives.ReadUInt32LittleEndian(block.Span);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(block.Span[4..]);
        var entries = Enumerable.Range(0, (int)(size - 8) / 2).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(block.Span[(8 + i * 2)..]))
            .Where(entry => entry >> 12 == 10).Select(entry => page + (uint)(entry & 0xFFF)).ToList();
        Assert.That(entries, Is.Not.Empty, "The table's pointer to value is a 64-bit base relocation");
        var data = headers.SectionHeaders.Single(section => section.Name == ".data");
        var pointer = entries.Single(rva => rva >= data.VirtualAddress && rva < data.VirtualAddress + data.VirtualSize);
        var addend = BinaryPrimitives.ReadUInt64LittleEndian(pe.AsSpan(data.PointerToRawData + (int)(pointer - data.VirtualAddress)));
        Assert.That(addend, Is.InRange(data.VirtualAddress, data.VirtualAddress + data.VirtualSize - 1), "The pointer's addend is value's RVA");
        Directory.Delete(output, recursive: true);
    }

    #endregion
}
