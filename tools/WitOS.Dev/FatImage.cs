using System.Buffers.Binary;
using System.Text;

namespace WitOS.Dev;

internal static class FatImage
{
    // A standard 32 MiB FAT16 superfloppy. QEMU/UEFI open it read-only.
    private const int SectorSize = 512;
    private const int TotalSectors = 65536;
    private const int SectorsPerCluster = 4;
    private const int FatSectors = 64;
    private const int RootEntries = 512;
    private const int RootSectors = RootEntries * 32 / SectorSize;
    private const int RootStart = 1 + 2 * FatSectors;
    private const int DataStart = RootStart + RootSectors;
    private const int ClusterSize = SectorsPerCluster * SectorSize;
    private const int ClusterCount = (TotalSectors - DataStart) / SectorsPerCluster;

    public static void Create(string destination, byte[] executable, byte[]? package = null)
    {
        // Preserve the existing 32 MiB layout. Assembly packages use a 128 MiB
        // FAT16 volume with 8 KiB clusters, retaining the bounded FAT capacity.
        package ??= AssemblyPackage.Create(Array.Empty<(string, ReadOnlyMemory<byte>)>());
        var large = (long)executable.Length + package.Length + ClusterSize > (ClusterCount - 2) * ClusterSize;
        var totalSectors = large ? 262144 : TotalSectors;
        var sectorsPerCluster = large ? 16 : SectorsPerCluster;
        var clusterSize = sectorsPerCluster * SectorSize;
        var clusterCount = (totalSectors - DataStart) / sectorsPerCluster;
        var fileClusters = checked((executable.Length + clusterSize - 1) / clusterSize);
        var packageClusters = checked((package.Length + clusterSize - 1) / clusterSize);
        if (fileClusters == 0 || packageClusters == 0 || fileClusters + packageClusters + 2 > clusterCount)
            throw new InvalidOperationException("EFI image does not fit in the M0 FAT16 volume.");

        using var stream = File.Create(destination);
        stream.SetLength((long)totalSectors * SectorSize);
        var boot = new byte[SectorSize];
        boot[0] = 0xEB;
        boot[1] = 0x3C;
        boot[2] = 0x90;
        PutText(boot, 3, "WITOS   ");
        Put16(boot, 11, SectorSize);
        boot[13] = (byte)sectorsPerCluster;
        Put16(boot, 14, 1);
        boot[16] = 2;
        Put16(boot, 17, RootEntries);
        boot[21] = 0xF8;
        Put16(boot, 22, FatSectors);
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

        var fat = new byte[FatSectors * SectorSize];
        Put16(fat, 0, 0xFFF8);
        Put16(fat, 2, 0xFFFF);
        Put16(fat, 4, 0xFFFF); // EFI directory, cluster 2.
        Put16(fat, 6, 0xFFFF); // BOOT directory, cluster 3.
        for (var i = 0; i < fileClusters; ++i)
            Put16(fat, (i + 4) * 2, i == fileClusters - 1 ? 0xFFFF : i + 5);
        var packageFirst = 4 + fileClusters;
        for (var i = 0; i < packageClusters; ++i)
            Put16(fat, (packageFirst + i) * 2, i == packageClusters - 1 ? 0xFFFF : packageFirst + i + 1);
        stream.Position = SectorSize;
        stream.Write(fat);
        stream.Write(fat);

        var root = new byte[RootSectors * SectorSize];
        Entry(root, 0, "EFI        ", 0x10, 2, 0);
        Entry(root, 1, "WITOS   PAK", 0x20, packageFirst, package.Length);
        stream.Position = RootStart * SectorSize;
        stream.Write(root);

        var efi = new byte[clusterSize];
        Entry(efi, 0, ".          ", 0x10, 2, 0);
        Entry(efi, 1, "..         ", 0x10, 0, 0);
        Entry(efi, 2, "BOOT       ", 0x10, 3, 0);
        stream.Position = DataStart * SectorSize;
        stream.Write(efi);

        var directory = new byte[clusterSize];
        Entry(directory, 0, ".          ", 0x10, 3, 0);
        Entry(directory, 1, "..         ", 0x10, 2, 0);
        Entry(directory, 2, "BOOTX64 EFI", 0x20, 4, executable.Length);
        stream.Write(directory);
        stream.Write(executable);
        stream.Position = (long)DataStart * SectorSize + (long)(packageFirst - 2) * clusterSize;
        stream.Write(package);
    }

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

    private static void Put16(byte[] data, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), checked((ushort)value));
    private static void Put32(byte[] data, int offset, int value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), checked((uint)value));
    private static void PutText(byte[] data, int offset, string value) => Encoding.ASCII.GetBytes(value).CopyTo(data, offset);
}
