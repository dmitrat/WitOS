using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OutWit.OS.FileSystems;
using OutWit.OS.FileSystems.Ext4;
using OutWit.OS.Storage;

namespace OutWit.OS.Tests.FileSystems;

/// <summary>
/// The journal under power failure (storage document §49–50, plan step D4.3): a change cut after every one of its
/// writes leaves a volume that, once its journal is replayed — by the provider or by e2fsck — is clean and holds the
/// change whole or not at all.
/// </summary>
[TestFixture]
public sealed class Ext4JournalTests
{
    #region Tests

    [Test]
    public async Task EveryCutRecoversTest()
    {
        var image = Ext4Images.Create("ext4", out var source);
        Ext4Images.Delete(source);
        var content = Pattern(20_000);
        var writes = await WritesOfChangeAsync(image, content);
        Assert.That(writes, Is.GreaterThan(6));
        for (var cut = 0; cut <= writes; cut++)
        {
            var copy = (byte[])image.Clone();
            await ChangeAsync(new CrashingBlockStorage(new MemoryBlockStorage(copy, 512), cut), content, unmount: false);
            await using (var recovered = await Ext4FileSystem.MountAsync(new MemoryBlockStorage(copy, 512)))
            {
                var entry = await recovered.GetEntryAsync("/crash/file.bin");
                if (entry is not null && entry.Length == content.Length)
                {
                    await using var file = await recovered.OpenAsync("/crash/file.bin", FileMode.Open, FileAccess.Read);
                    var bytes = new byte[content.Length];
                    await file.ReadAsync(0, bytes);
                    Assert.That(bytes, Is.EqualTo(content), $"cut after {cut} writes");
                }
            }
            var (exit, report) = Ext4Tools.Check(copy);
            Assert.That(exit, Is.Zero, $"cut after {cut} writes: {report}");
        }
    }

    [Test]
    public async Task E2fsckReplaysTheProvidersJournalTest()
    {
        var image = Ext4Images.Create("ext4", out var source);
        Ext4Images.Delete(source);
        var content = Pattern(20_000);
        var writes = await WritesOfChangeAsync(image, content);
        // Cut every change but the last's checkpoint: its log and commit reached the disk, its blocks did not.
        var replayed = 0;
        for (var cut = writes - 1; cut > 0 && replayed == 0; cut--)
        {
            var copy = (byte[])image.Clone();
            await ChangeAsync(new CrashingBlockStorage(new MemoryBlockStorage(copy, 512), cut), content, unmount: false);
            var (repairExit, repairReport) = Ext4Tools.Repair(copy);
            if (!repairReport.Contains("recovering journal", StringComparison.OrdinalIgnoreCase))
                continue;
            replayed++;
            Assert.That(repairExit, Is.LessThanOrEqualTo(1), repairReport);
            var (exit, report) = Ext4Tools.Check(copy);
            Assert.That(exit, Is.Zero, report);
        }
        Assert.That(replayed, Is.EqualTo(1), "no cut left a journal for e2fsck to replay");
    }

    [Test]
    public async Task E2fsprogsJournalReplaysTest()
    {
        var image = Ext4Images.Create("ext4", out var source);
        Ext4Images.Delete(source);
        var first = Path.Combine(Path.GetTempPath(), $"witos-journal-{Guid.NewGuid():N}");
        var second = first + "-second";
        try
        {
            var a = Pattern(8192);
            var b = Pattern(4096).Select(value => (byte)~value).ToArray();
            await File.WriteAllBytesAsync(first, a);
            await File.WriteAllBytesAsync(second, b);
            var original = image.AsSpan(3001 * 4096, 4096).ToArray();
            // Three transactions with CSUM_V3 checksums: blocks 3000-3001, block 3002, and a revoke of 3001.
            var report = Ext4Tools.Script(image, "jo -c", $"jw -b 3000,3001 {first}", $"jw -b 3002 {second}", "jw -r 3001", "jc");
            Assert.That(image[1024 + 0x60] & 0x4, Is.Not.Zero, $"debugfs left no journal to recover: {report}");

            await using (var volume = await Ext4FileSystem.MountAsync(new MemoryBlockStorage(image, 512)))
                Assert.That(volume.Volume.IsReadOnly, Is.False);

            Assert.That(image.AsSpan(3000 * 4096, 4096).ToArray(), Is.EqualTo(a[..4096]));
            Assert.That(image.AsSpan(3001 * 4096, 4096).ToArray(), Is.EqualTo(original), "a revoked block was replayed");
            Assert.That(image.AsSpan(3002 * 4096, 4096).ToArray(), Is.EqualTo(b));
            var (exit, check) = Ext4Tools.Check(image);
            Assert.That(exit, Is.Zero, check);
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    #endregion

    #region Tools

    // The writes the mount and the change make, counted by a disk that never cuts.
    private static async Task<int> WritesOfChangeAsync(byte[] image, byte[] content)
    {
        var disk = new CrashingBlockStorage(new MemoryBlockStorage((byte[])image.Clone(), 512), int.MaxValue);
        await ChangeAsync(disk, content, unmount: false);
        return disk.Writes;
    }

    // The mount, the directory and the file: the cut falls anywhere in them. What the provider does after the power
    // went does not count: it may find the disk without its own lost writes.
    private static async Task ChangeAsync(CrashingBlockStorage disk, byte[] content, bool unmount)
    {
        try
        {
            var volume = await Ext4FileSystem.MountAsync(disk);
            await volume.CreateDirectoryAsync("/crash");
            await using (var file = await volume.OpenAsync("/crash/file.bin", FileMode.CreateNew, FileAccess.Write))
                await file.WriteAsync(0, content);
            if (unmount)
                await volume.DisposeAsync();
        }
        catch (FileSystemException)
        {
        }
    }

    private static byte[] Pattern(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 13 + 1))];

    #endregion
}
