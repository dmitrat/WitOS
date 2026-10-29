using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OutWit.OS.FileSystems;
using OutWit.OS.FileSystems.Fat;
using OutWit.OS.Namespace.Server;
using OutWit.OS.Namespace.Volumes;
using OutWit.OS.Namespaces;
using OutWit.OS.Storage;

namespace OutWit.OS.Tests.Namespace;

/// <summary>
/// The catalog of volumes (plan step E1, ADR 0032): mount points below /media and the longest one taking a path, aliases
/// made from labels and kept unique, the catalog's records through VOLUMES and STATFS, /media's listing, and the mount
/// policy over a GUID disk, an MBR disk and a disk without a table.
/// </summary>
[TestFixture]
public sealed class VolumeCatalogTests
{
    #region Constants

    private const long DISK_BYTES = 8L << 20;

    #endregion

    #region Tests

    [Test]
    public async Task MediaPointsTakeTheLongestPrefixTest()
    {
        var mounts = new MountTable();
        var media = mounts.Add(MountTable.MEDIA, new MediaDirectory(mounts));
        var stick = mounts.Add("/media/STICK", await FormatAsync("STICK"));

        Assert.That(mounts.Resolve("/media/STICK/dir/a.txt"), Is.EqualTo(((MountTable.Mount, string)?)(stick, "/dir/a.txt")));
        Assert.That(mounts.Resolve("/media/STICK"), Is.EqualTo(((MountTable.Mount, string)?)(stick, "/")));
        Assert.That(mounts.Resolve("/media/OTHER"), Is.EqualTo(((MountTable.Mount, string)?)(media, "/OTHER")), "/media's own, which holds no such name");
        Assert.Throws<ArgumentException>(() => mounts.Add("/media/a/b", new MediaDirectory(mounts)), "one name below /media");
        Assert.Throws<ArgumentException>(() => mounts.Add("/data/x", new MediaDirectory(mounts)), "below /media alone");
        Assert.Throws<ArgumentException>(() => mounts.Add("/media/STICK", new MediaDirectory(mounts)), "taken");

        Assert.That(mounts.Remove("/media/STICK"), Is.SameAs(stick));
        Assert.That(mounts.Resolve("/media/STICK"), Is.EqualTo(((MountTable.Mount, string)?)(media, "/STICK")));
        Assert.That(mounts.Add("/media/NEXT", await FormatAsync("NEXT")).Index, Is.GreaterThan(stick.Index), "an index is never reused");
    }

    [Test]
    public async Task AliasesComeFromLabelsAndStayUniqueTest()
    {
        var mounts = new MountTable();
        var catalog = mounts.Catalog;

        var system = catalog.Attach(await FormatAsync("WITOS"), NamespaceMessages.ORIGIN_INTERNAL, "Fat", "1111-1111", true);
        var first = catalog.Attach(await FormatAsync("USB DISK"), NamespaceMessages.ORIGIN_REMOVABLE, null, "2222-2222", true);
        var second = catalog.Attach(await FormatAsync("USB DISK"), NamespaceMessages.ORIGIN_REMOVABLE, null, "2222-2222", true);
        var data = catalog.Attach(await FormatAsync("data"), NamespaceMessages.ORIGIN_INTERNAL, null, null, false);
        var unnamed = catalog.Attach(await FormatAsync(null), NamespaceMessages.ORIGIN_INTERNAL, null, null, false);

        Assert.That((system.Alias, system.Point), Is.EqualTo(("Fat", "/fat")));
        Assert.That((first.Alias, first.Point), Is.EqualTo(("USB-DISK", "/media/USB-DISK")));
        Assert.That((second.Alias, second.Collision), Is.EqualTo(("USB-DISK-2", true)), "a copy of the same identity: never one volume");
        Assert.That(data.Alias, Is.EqualTo("DATA-2"), "the system's aliases are no label's, whatever the case (FAT keeps a label in capitals)");
        Assert.That(unnamed.Alias, Is.EqualTo(VolumeCatalog.UNLABELLED));

        Assert.That(catalog.Detach("USB-DISK-2"), Is.SameAs(second));
        Assert.That(catalog.Detach("USB-DISK"), Is.SameAs(first));
        var again = catalog.Attach(await FormatAsync("RENAMED"), NamespaceMessages.ORIGIN_REMOVABLE, null, "2222-2222", true);
        Assert.That(again.Alias, Is.EqualTo("USB-DISK-2"), "a volume that comes back in the boot takes its alias again while it is free");
        Assert.That(mounts.Resolve("/media/USB-DISK-2")!.Value.Mount, Is.SameAs(again.Mount));

        Assert.That(VolumeCatalog.Stem("My Stick!"), Is.EqualTo("My-Stick"));
        Assert.That(VolumeCatalog.Stem("  "), Is.EqualTo(VolumeCatalog.UNLABELLED));
        Assert.That(VolumeCatalog.Stem(new string('a', 40)), Has.Length.EqualTo(31));
    }

    [Test]
    public async Task VolumesAndStatfsReportTheCatalogTest()
    {
        var mounts = new MountTable();
        mounts.Catalog.Attach(await FormatAsync("WITOS"), NamespaceMessages.ORIGIN_INTERNAL, "Fat", "1111-1111", true);
        mounts.Add(MountTable.MEDIA, new MediaDirectory(mounts));
        var stick = mounts.Catalog.Attach(await FormatAsync("STICK"), NamespaceMessages.ORIGIN_REMOVABLE, null, "3333-3333", true);
        var window = new ByteWindow(8192);
        var session = new NamespaceSession(mounts, window);

        var listed = await session.HandleAsync(new NamespaceRequest(NamespaceMessages.VOLUMES));
        Assert.That(listed.Error, Is.Zero);
        var records = Records(window, listed.Length);
        Assert.That(records.Select(volume => (volume.Alias, volume.Point, volume.Origin)),
            Is.EqualTo(new[] { ("Fat", "/fat", NamespaceMessages.ORIGIN_INTERNAL), ("STICK", "/media/STICK", NamespaceMessages.ORIGIN_REMOVABLE) }),
            "the volumes, not /media itself");
        Assert.That(records.All(volume => (volume.Flags & NamespaceMessages.VOLUME_SPACE) == 0 && volume.TotalBytes == 0), Is.True, "space only when asked");
        Assert.That(records[1], Is.EqualTo(stick.Record(null)));
        Assert.That(records[1].Flags & NamespaceMessages.VOLUME_WEAK_IDENTITY, Is.Not.Zero);

        var spaced = Records(window, (await session.HandleAsync(new NamespaceRequest(NamespaceMessages.VOLUMES, NamespaceMessages.SPACE))).Length);
        Assert.That(spaced.All(volume => (volume.Flags & NamespaceMessages.VOLUME_SPACE) != 0 && volume.TotalBytes > 0 && volume.FreeBytes > 0), Is.True);

        Assert.That((await Path(session, window, NamespaceMessages.STATFS, "/media/STICK/no/such/file")).Error, Is.EqualTo(Errno.NOT_FOUND),
            "a path that is not there, as Linux's statfs answers");
        var byPath = await Path(session, window, NamespaceMessages.STATFS, "/media/STICK");
        Assert.That(byPath.Error, Is.Zero);
        var volume = NamespaceVolume.Read(window.Read(0, (int)byPath.Length));
        Assert.That((volume.Alias, volume.Format, volume.Identity, volume.Label), Is.EqualTo(("STICK", stick.Mount.Volume.Volume.Format, "3333-3333", "STICK")));
        Assert.That(volume.Flags & NamespaceMessages.VOLUME_SPACE, Is.Not.Zero);

        var opened = await Path(session, window, NamespaceMessages.OPEN, "/fat/a.txt", NamespaceMessages.WRITE_ACCESS | NamespaceMessages.CREATE);
        var byHandle = await session.HandleAsync(new NamespaceRequest(NamespaceMessages.STATFS, Handle: opened.Handle));
        Assert.That(NamespaceVolume.Read(window.Read(0, (int)byHandle.Length)).Alias, Is.EqualTo("Fat"));
        Assert.That((await session.HandleAsync(new NamespaceRequest(NamespaceMessages.STATFS, Handle: 99))).Error, Is.EqualTo(Errno.BAD_HANDLE));

        var directory = await Path(session, window, NamespaceMessages.OPEN, "/media", NamespaceMessages.READ_ACCESS | NamespaceMessages.DIRECTORY);
        Assert.That(directory.Error, Is.Zero);
        var page = await session.HandleAsync(new NamespaceRequest(NamespaceMessages.LIST, Handle: directory.Handle));
        Assert.That(Names(window, page.Length), Is.EqualTo(new[] { "STICK" }), "/media lists the volumes below it");
        Assert.That((await Path(session, window, NamespaceMessages.MKDIR, "/media/new")).Error, Is.EqualTo(Errno.Of(new FileSystemException(FileSystemError.ReadOnly, ""))));
        var stat = await Path(session, window, NamespaceMessages.STAT, "/media/STICK");
        Assert.That(stat.Stat!.Kind, Is.EqualTo(NamespaceMessages.KIND_DIRECTORY));
    }

    [Test]
    public async Task PolicyMountsEveryDiskItReadsTest()
    {
        var mounts = new MountTable();
        mounts.Add(MountTable.MEDIA, new MediaDirectory(mounts));

        // A GUID disk: its EFI system partition at /boot, a partition of another name below /media by its label.
        var guid = new MemoryBlockStorage(DISK_BYTES);
        var table = await GptPartitionTable.WriteAsync(guid, Guid.NewGuid(),
        [
            new GptPartition(0, GptPartition.EFI_SYSTEM, Guid.NewGuid(), 2048, 6143, 0, "EFI System"),
            new GptPartition(1, Guid.Parse("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"), Guid.NewGuid(), 6144, 12287, 0, "Photos")
        ]);
        foreach (var (partition, label) in table.Partitions.Zip(new[] { "EFI", "PHOTOS" }))
            await (await FatFileSystem.FormatAsync(partition.Open(guid), label)).DisposeAsync();
        await VolumeMounter.MountAsync(mounts, guid, "test/guid");

        // An MBR disk, as Windows formats a USB drive, and a FAT disk without a table once /boot is taken.
        var mbr = new MemoryBlockStorage(DISK_BYTES);
        var stick = (await MbrPartitionTable.WriteAsync(mbr, 0x1234, [new MbrPartition(1, 0x0C, 2048, 8192, false)])).Partitions[0];
        await (await FatFileSystem.FormatAsync(stick.Open(mbr), "STICK")).DisposeAsync();
        await VolumeMounter.MountAsync(mounts, mbr, "test/mbr");
        var floppy = new MemoryBlockStorage(DISK_BYTES);
        await (await FatFileSystem.FormatAsync(floppy, "FLOPPY")).DisposeAsync();
        await VolumeMounter.MountAsync(mounts, floppy, "test/floppy");

        var volumes = mounts.Catalog.Volumes;
        Assert.That(volumes.Select(volume => volume.Point), Is.EqualTo(new[] { "/boot", "/media/PHOTOS", "/media/STICK", "/media/FLOPPY" }));
        Assert.That(volumes[0].Mount.Volume.Volume.IsReadOnly, Is.True, "the firmware boots from the EFI system partition");
        Assert.That((volumes[1].Identity, volumes[1].WeakIdentity), Is.EqualTo((table.Partitions[1].Id.ToString(), false)),
            "a GUID partition's GUID before a FAT serial number");
        Assert.That(volumes[2].WeakIdentity, Is.True, "an MBR partition's FAT volume: its serial number alone");
    }

    #endregion

    #region Functions

    private static async Task<IFileSystem> FormatAsync(string? label) =>
        await FatFileSystem.FormatAsync(new MemoryBlockStorage(DISK_BYTES), label);

    private static List<NamespaceVolume> Records(ByteWindow window, long length)
    {
        var bytes = window.Read(0, (int)length);
        return [.. Enumerable.Range(0, bytes.Length / NamespaceVolume.SIZE)
            .Select(i => NamespaceVolume.Read(bytes.AsSpan(i * NamespaceVolume.SIZE, NamespaceVolume.SIZE)))];
    }

    private static List<string> Names(ByteWindow window, long length)
    {
        var bytes = window.Read(0, (int)length);
        var names = new List<string>();
        for (var at = 0; at < bytes.Length;)
        {
            names.Add(Encoding.UTF8.GetString(bytes, at + NamespaceMessages.ENTRY_SIZE, BitConverter.ToUInt16(bytes, at + 14)));
            at += BitConverter.ToUInt16(bytes, at + 12);
        }
        return names;
    }

    private static ValueTask<NamespaceReply> Path(NamespaceSession session, ByteWindow window, uint operation, string path, uint flags = 0)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        window.Write(0, bytes);
        return session.HandleAsync(new NamespaceRequest(operation, flags, Length: bytes.Length));
    }

    #endregion

    #region Nested Types

    // The window as an array of the test's.
    private sealed class ByteWindow(int bytes) : INamespaceWindow
    {
        private readonly byte[] m_bytes = new byte[bytes];

        public int Length => m_bytes.Length;

        public byte[] Read(int offset, int count) => m_bytes.AsSpan(offset, count).ToArray();

        public void Write(int offset, ReadOnlySpan<byte> bytes) => bytes.CopyTo(m_bytes.AsSpan(offset));
    }

    #endregion
}
