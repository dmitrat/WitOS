using System.Buffers.Binary;
using System.Text;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Tests.Kernel;

/// <summary>
/// The FAT boot disk's directory entries: a boot file name longer than 8.3 (BOOTRISCV64.EFI, the UEFI removable-media
/// name of RISC-V, plan step V1.1) is written as a VFAT long name over an 8.3 alias, which the firmware's FAT driver
/// reads; an 8.3 name stays one short entry.
/// </summary>
[TestFixture]
public sealed class FatImageTests
{
    #region Constants

    // The BOOT directory is cluster 3 of a 32 MiB volume: the data area starts after the boot sector, two FATs of 64
    // sectors and 32 root sectors, and a cluster holds four sectors.
    private const int BOOT_DIRECTORY = (1 + 2 * 64 + 32) * 512 + 2048;

    #endregion

    #region Functions

    [Test]
    public void LongBootNameIsAVfatLongNameTest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"witos-fat-{Environment.ProcessId}.img");
        FatImage.Create(path, new byte[4096], bootName: "BOOTRISCV64.EFI");
        var directory = ReadDirectory(path);
        File.Delete(path);

        // Two long-name entries (15 characters, 13 to an entry), the last part first and flagged 0x40, then the alias.
        Assert.That(directory[2 * 32], Is.EqualTo(0x42), "The first entry is the last part of the name, flagged as last");
        Assert.That(directory[2 * 32 + 11], Is.EqualTo(0x0F));
        Assert.That(directory[3 * 32], Is.EqualTo(0x01));
        Assert.That(directory[3 * 32 + 11], Is.EqualTo(0x0F));
        Assert.That(Encoding.ASCII.GetString(directory, 4 * 32, 11), Is.EqualTo("BOOTRI~1EFI"));
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(4 * 32 + 26)), Is.EqualTo(4), "The loader's first cluster");
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(4 * 32 + 28)), Is.EqualTo(4096), "The loader's size");

        // The name's characters in order, then one zero character, then 0xFFFF; every long entry carries the alias's
        // checksum and no cluster.
        var checksum = 0;
        foreach (var value in Encoding.ASCII.GetBytes("BOOTRI~1EFI"))
            checksum = ((((checksum & 1) << 7) | (checksum >> 1)) + value) & 0xFF;
        int[] slots = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];
        var characters = new List<ushort>();
        foreach (var entry in new[] { 3, 2 })
        {
            Assert.That(directory[entry * 32 + 13], Is.EqualTo(checksum), "The alias's checksum");
            Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(entry * 32 + 26)), Is.EqualTo(0));
            characters.AddRange(slots.Select(slot => BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(entry * 32 + slot))));
        }
        Assert.That(characters.Take(15).Select(c => (char)c), Is.EqualTo("BOOTRISCV64.EFI"));
        Assert.That(characters[15], Is.EqualTo(0));
        Assert.That(characters.Skip(16).All(c => c == 0xFFFF), Is.True);
    }

    [Test]
    public void ShortBootNameStaysOneEntryTest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"witos-fat-short-{Environment.ProcessId}.img");
        FatImage.Create(path, new byte[4096], bootName: "BOOTAA64.EFI");
        var directory = ReadDirectory(path);
        File.Delete(path);
        Assert.That(Encoding.ASCII.GetString(directory, 2 * 32, 11), Is.EqualTo("BOOTAA64EFI"));
        Assert.That(directory[2 * 32 + 11], Is.EqualTo(0x20));
        Assert.That(directory[3 * 32], Is.EqualTo(0), "No entry follows the loader's");
    }

    #endregion

    #region Tools

    private static byte[] ReadDirectory(string path)
    {
        using var stream = File.OpenRead(path);
        stream.Position = BOOT_DIRECTORY;
        var directory = new byte[2048];
        stream.ReadExactly(directory);
        return directory;
    }

    #endregion
}
