using System.Buffers.Binary;
using System.Text;

namespace WitOS.Dev.Kernel;

/// <summary>
/// Writes a FAT boot disk that holds the UEFI loader, the kernel and an optional boot package.
/// </summary>
internal static class FatImage
{
    #region Constants

    // A standard 32 MiB FAT16 superfloppy. QEMU/UEFI open it read-only.
    private const int SECTOR_SIZE = 512;

    private const int TOTAL_SECTORS = 65536;

    private const int SECTORS_PER_CLUSTER = 4;

    private const int FAT_SECTORS = 64;

    private const int ROOT_ENTRIES = 512;

    private const int ROOT_SECTORS = ROOT_ENTRIES * 32 / SECTOR_SIZE;

    private const int ROOT_START = 1 + 2 * FAT_SECTORS;

    private const int DATA_START = ROOT_START + ROOT_SECTORS;

    private const int CLUSTER_SIZE = SECTORS_PER_CLUSTER * SECTOR_SIZE;

    private const int CLUSTER_COUNT = (TOTAL_SECTORS - DATA_START) / SECTORS_PER_CLUSTER;

    #endregion

    #region Functions

    /// <summary>
    /// Writes a FAT16 boot disk with the loader at EFI/BOOT/&lt;bootName&gt; and the boot package beside it.
    /// </summary>
    /// <param name="destination">Disk image path.</param>
    /// <param name="executable">UEFI loader bytes.</param>
    /// <param name="package">Boot package, or an empty package when null.</param>
    /// <param name="bootName">Removable-media boot file name: an 8.3 name such as BOOTX64.EFI or BOOTAA64.EFI, or a longer
    /// one such as BOOTRISCV64.EFI (plan step V1.1), written as a VFAT long name over an 8.3 alias.</param>
    /// <param name="rootTask">The root task's flat image, written as WITROOT.BIN beside the package (K4); none when null.</param>
    public static void Create(string destination, byte[] executable, byte[]? package = null,
        string bootName = "BOOTX64.EFI", byte[]? rootTask = null)
    {
        var parts = bootName.Split('.');
        if (parts.Length != 2 || parts[0].Length is 0 or > 64 || parts[1].Length is 0 or > 3 || bootName.Any(c => c is < ' ' or > '~'))
            throw new ArgumentException("Boot file name must be a name of up to 64 ASCII characters with an extension of up to three.", nameof(bootName));
        var longName = parts[0].Length > 8;
        var shortName = (longName ? parts[0][..6] + "~1" : parts[0].PadRight(8)).ToUpperInvariant() + parts[1].ToUpperInvariant().PadRight(3);
        // The smallest volume the files fit: the 32 MiB layout, then volumes of 128, 256 and 512 MiB whose clusters grow
        // with them (8, 16 and 32 KiB), so that the FAT keeps its 64 sectors and the cluster count its bound (W3.2: a
        // package of up to WIT_PACKAGE_MAX_BYTES, 256 MiB, needs the largest).
        package ??= AssemblyPackage.Create(Array.Empty<(string, ReadOnlyMemory<byte>)>());
        rootTask ??= [];
        var totalSectors = 0;
        var sectorsPerCluster = 0;
        var clusterSize = 0;
        var clusterCount = 0;
        var fileClusters = 0;
        var packageClusters = 0;
        var rootClusters = 0;
        var fits = false;
        foreach (var (sectors, perCluster) in new[] { (TOTAL_SECTORS, SECTORS_PER_CLUSTER), (262144, 16), (524288, 32), (1048576, 64) })
        {
            totalSectors = sectors;
            sectorsPerCluster = perCluster;
            clusterSize = sectorsPerCluster * SECTOR_SIZE;
            clusterCount = (totalSectors - DATA_START) / sectorsPerCluster;
            fileClusters = checked((executable.Length + clusterSize - 1) / clusterSize);
            packageClusters = checked((package.Length + clusterSize - 1) / clusterSize);
            rootClusters = checked((rootTask.Length + clusterSize - 1) / clusterSize);
            if (fileClusters > 0 && packageClusters > 0 && fileClusters + packageClusters + rootClusters + 2 <= clusterCount)
            {
                fits = true;
                break;
            }
        }
        if (!fits)
            throw new InvalidOperationException("EFI image does not fit in the M0 FAT16 volume.");

        using var stream = File.Create(destination);
        stream.SetLength((long)totalSectors * SECTOR_SIZE);
        var boot = new byte[SECTOR_SIZE];
        boot[0] = 0xEB;
        boot[1] = 0x3C;
        boot[2] = 0x90;
        PutText(boot, 3, "WITOS   ");
        Put16(boot, 11, SECTOR_SIZE);
        boot[13] = (byte)sectorsPerCluster;
        Put16(boot, 14, 1);
        boot[16] = 2;
        Put16(boot, 17, ROOT_ENTRIES);
        boot[21] = 0xF8;
        Put16(boot, 22, FAT_SECTORS);
        Put16(boot, 24, 32);
        Put16(boot, 26, 64);
        Put32(boot, 32, totalSectors);
        boot[36] = 0x80;
        boot[38] = 0x29;
        Put32(boot, 39, 0x5749544F);
        PutText(boot, 43, "WITOS BOOT ");
        PutText(boot, 54, "FAT16   ");
        boot[510] = 0x55;
        boot[511] = 0xAA;
        stream.Write(boot);

        var fat = new byte[FAT_SECTORS * SECTOR_SIZE];
        Put16(fat, 0, 0xFFF8);
        Put16(fat, 2, 0xFFFF);
        Put16(fat, 4, 0xFFFF); // EFI directory, cluster 2.
        Put16(fat, 6, 0xFFFF); // BOOT directory, cluster 3.
        for (var i = 0; i < fileClusters; ++i)
            Put16(fat, (i + 4) * 2, i == fileClusters - 1 ? 0xFFFF : i + 5);
        var packageFirst = 4 + fileClusters;
        for (var i = 0; i < packageClusters; ++i)
            Put16(fat, (packageFirst + i) * 2, i == packageClusters - 1 ? 0xFFFF : packageFirst + i + 1);
        var rootFirst = packageFirst + packageClusters;
        for (var i = 0; i < rootClusters; ++i)
            Put16(fat, (rootFirst + i) * 2, i == rootClusters - 1 ? 0xFFFF : rootFirst + i + 1);
        stream.Position = SECTOR_SIZE;
        stream.Write(fat);
        stream.Write(fat);

        var root = new byte[ROOT_SECTORS * SECTOR_SIZE];
        Entry(root, 0, "EFI        ", 0x10, 2, 0);
        Entry(root, 1, "WITOS   PAK", 0x20, packageFirst, package.Length);
        if (rootClusters > 0)
            Entry(root, 2, "WITROOT BIN", 0x20, rootFirst, rootTask.Length);
        stream.Position = ROOT_START * SECTOR_SIZE;
        stream.Write(root);

        var efi = new byte[clusterSize];
        Entry(efi, 0, ".          ", 0x10, 2, 0);
        Entry(efi, 1, "..         ", 0x10, 0, 0);
        Entry(efi, 2, "BOOT       ", 0x10, 3, 0);
        stream.Position = DATA_START * SECTOR_SIZE;
        stream.Write(efi);

        var directory = new byte[clusterSize];
        Entry(directory, 0, ".          ", 0x10, 3, 0);
        Entry(directory, 1, "..         ", 0x10, 2, 0);
        var bootEntry = longName ? LongNameEntries(directory, 2, bootName, shortName) : 2;
        Entry(directory, bootEntry, shortName, 0x20, 4, executable.Length);
        stream.Write(directory);
        stream.Write(executable);
        stream.Position = (long)DATA_START * SECTOR_SIZE + (long)(packageFirst - 2) * clusterSize;
        stream.Write(package);
        if (rootClusters > 0)
        {
            stream.Position = (long)DATA_START * SECTOR_SIZE + (long)(rootFirst - 2) * clusterSize;
            stream.Write(rootTask);
        }
    }

    #endregion

    #region Tools

    private static void Entry(byte[] buffer, int index, string name, byte attributes, int cluster, int length)
    {
        var offset = index * 32;
        PutText(buffer, offset, name);
        buffer[offset + 11] = attributes;
        // Fixed 2026-01-01 timestamps make packaging reproducible.
        Put16(buffer, offset + 16, (46 << 9) | (1 << 5) | 1);
        Put16(buffer, offset + 18, (46 << 9) | (1 << 5) | 1);
        Put16(buffer, offset + 24, (46 << 9) | (1 << 5) | 1);
        Put16(buffer, offset + 26, cluster);
        Put32(buffer, offset + 28, length);
    }

    // A name longer than 8.3 (BOOTRISCV64.EFI, the UEFI removable-media name of RISC-V) is a VFAT long name: its entries
    // precede the short entry, the last part of the name first, 13 UCS-2 characters each with the name's end marked by
    // one zero character and the rest filled with 0xFFFF, each entry flagged 0x0F and carrying the checksum of the short
    // alias it belongs to. Returns the index of the short entry that follows them.
    private static int LongNameEntries(byte[] buffer, int index, string name, string shortName)
    {
        var checksum = 0;
        foreach (var value in Encoding.ASCII.GetBytes(shortName))
            checksum = ((((checksum & 1) << 7) | (checksum >> 1)) + value) & 0xFF;
        int[] slots = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];
        var count = (name.Length + 12) / 13;
        for (var sequence = count; sequence >= 1; --sequence)
        {
            var offset = index++ * 32;
            buffer[offset] = (byte)(sequence | (sequence == count ? 0x40 : 0));
            buffer[offset + 11] = 0x0F;
            buffer[offset + 13] = (byte)checksum;
            for (var i = 0; i < 13; ++i)
            {
                var position = (sequence - 1) * 13 + i;
                Put16(buffer, offset + slots[i], position < name.Length ? name[position] : position == name.Length ? 0 : 0xFFFF);
            }
        }
        return index;
    }

    private static void Put16(byte[] data, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), checked((ushort)value));

    private static void Put32(byte[] data, int offset, int value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), checked((uint)value));

    private static void PutText(byte[] data, int offset, string value) => Encoding.ASCII.GetBytes(value).CopyTo(data, offset);

    #endregion
}
