using System.Text;
using WitOS.Dev.Host;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The ext4 volume of the runtime-devices data disk (plan step D4.2), made by the host's e2fsprogs from a known tree, as
/// the storage document's cross-implementation test asks (§73): <c>mkfs.ext4 -d</c> lays the tree in and
/// <c>e2fsck -D</c> indexes its large directory as an htree. The guest reads the tree and writes it (D4.3); after the
/// boot the host's e2fsck must find it clean and debugfs must read what the guest wrote.
/// </summary>
internal static class RuntimeExt4Volume
{
    #region Constants

    /// <summary>
    /// The volume's label.
    /// </summary>
    public const string LABEL = "WITOS EXT4";

    /// <summary>
    /// What /hello.txt says.
    /// </summary>
    public const string HELLO_TEXT = "Written on the host by mkfs.ext4 -d; read by WitOS's managed ext4 provider.";

    /// <summary>
    /// The chunks of /big.bin, each between holes, more than an inode's extents hold.
    /// </summary>
    public const int BIG_CHUNKS = 6;

    /// <summary>
    /// The bytes of each chunk of /big.bin.
    /// </summary>
    public const int BIG_CHUNK_BYTES = 20_000;

    /// <summary>
    /// The distance between the chunks of /big.bin.
    /// </summary>
    public const int BIG_STRIDE = 65_536;

    /// <summary>
    /// The bytes of /big.bin.
    /// </summary>
    public const int BIG_BYTES = (BIG_CHUNKS - 1) * BIG_STRIDE + BIG_CHUNK_BYTES;

    /// <summary>
    /// The empty files of /many, an htree.
    /// </summary>
    public const int MANY = 400;

    /// <summary>
    /// The target of /link.
    /// </summary>
    public const string LINK_TARGET = "hello.txt";

    /// <summary>
    /// The bytes of the file the guest writes, /guest/logs/written.bin (Ext4Probe's WRITTEN_BYTES).
    /// </summary>
    public const int GUEST_BYTES = 200_000;

    /// <summary>
    /// The entries the guest adds to /many (Ext4Probe's ADDED).
    /// </summary>
    public const int GUEST_ADDED = 50;

    /// <summary>
    /// What the guest's note says (Ext4Probe's NOTE).
    /// </summary>
    public const string GUEST_NOTE = "Written by WitOS through the journal of its managed ext4 provider.";

    /// <summary>
    /// What /bin/init writes to /data/posix-kept.txt through the libc and the namespace service (D5.1, devices_init.c).
    /// </summary>
    public const string INIT_TEXT = "Written by /bin/init through the libc and the namespace service.";

    /// <summary>
    /// What /bin/files writes to /data/dotnet-kept.txt through System.IO (D5.2, tests/Runtime.Witos/Files).
    /// </summary>
    public const string DOTNET_TEXT = "Written by a .NET program through System.IO and the namespace service.";

    /// <summary>
    /// The directory of the programs a scenario lays on the volume, which the guest starts from it (D5.3).
    /// </summary>
    public const string PROGRAMS = "programs";

    private const string UUID = "4e2a7c19-0b3d-4f58-9e61-5749544f5334";
    private static readonly string[] MKFS_PLACES = ["/sbin/mkfs.ext4", "/usr/sbin/mkfs.ext4", "/usr/bin/mkfs.ext4"];

    #endregion

    #region Functions

    /// <summary>
    /// The host's mkfs.ext4, or null on a host without e2fsprogs.
    /// </summary>
    /// <returns>Its path.</returns>
    public static string? FindMkfs() => OperatingSystem.IsLinux() ? MKFS_PLACES.FirstOrDefault(File.Exists) : null;

    /// <summary>
    /// Makes the volume.
    /// </summary>
    /// <param name="image">The image to write.</param>
    /// <param name="bytes">Its bytes.</param>
    /// <param name="programs">Files laid under /programs, each by its path there (D5.3); none by default.</param>
    /// <returns>When the image is written.</returns>
    /// <exception cref="InvalidOperationException">The host has no e2fsprogs, or mkfs or e2fsck failed.</exception>
    public static async Task CreateAsync(string image, long bytes, IReadOnlyList<(string Path, string Source)>? programs = null)
    {
        var mkfs = FindMkfs() ?? throw new InvalidOperationException("The data disk's ext4 volume needs e2fsprogs (mkfs.ext4) on a Linux host.");
        var tree = Path.Combine(Path.GetDirectoryName(image)!, "ext4-tree");
        if (Directory.Exists(tree))
            Directory.Delete(tree, recursive: true);
        WriteTree(tree, programs ?? []);
        File.Delete(image);
        await RunAsync(mkfs, ["-t", "ext4", "-b", "4096", "-I", "256", "-N", "1024", "-T", "default", "-F", "-q", "-L", LABEL, "-U", UUID,
            "-d", tree, image, $"{bytes / 1024}k"], 0);
        await RunAsync(Path.Combine(Path.GetDirectoryName(mkfs)!, "e2fsck"), ["-f", "-y", "-D", image], 1);
        Directory.Delete(tree, recursive: true);
    }

    /// <summary>
    /// Requires what the guest wrote to a volume (its image): the guest unmounted it, e2fsck finds it clean, and debugfs
    /// reads the guest's files.
    /// </summary>
    /// <param name="image">The volume's image.</param>
    /// <param name="boots">The boots /boots.txt must count (D6.1); 0 to skip.</param>
    /// <returns>When the volume is judged.</returns>
    /// <exception cref="InvalidOperationException">The journal needs recovery, e2fsck finds a problem, or a file holds other
    /// bytes.</exception>
    public static async Task CheckAsync(string image, int boots = 0)
    {
        var tools = Path.GetDirectoryName(FindMkfs() ?? throw new InvalidOperationException("Judging the ext4 volume needs e2fsprogs."))!;
        var header = await Processes.RunAsync(Path.Combine(tools, "dumpe2fs"), ["-h", image], Path.GetTempPath(), 300);
        var features = header.Output.Split('\n').FirstOrDefault(line => line.StartsWith("Filesystem features:", StringComparison.Ordinal));
        if (header.ExitCode != 0 || features is null || features.Contains("needs_recovery", StringComparison.Ordinal))
            throw new InvalidOperationException($"The guest did not unmount its ext4 volume: dumpe2fs says '{features}' ({header.ExitCode}).");
        var check = await Processes.RunAsync(Path.Combine(tools, "e2fsck"), ["-fn", image], Path.GetTempPath(), 300);
        if (check.ExitCode != 0)
            throw new InvalidOperationException($"e2fsck finds the guest's ext4 volume unclean ({check.ExitCode}): {check.Output} {check.Error}");
        if (boots > 0 && Encoding.UTF8.GetString(await DumpAsync(tools, image, "/boots.txt")) != $"{boots}")
            throw new InvalidOperationException($"debugfs reads another /boots.txt than the guest's count of {boots} boots.");
        var written = await DumpAsync(tools, image, "/guest/logs/written.bin");
        var expected = Enumerable.Range(0, GUEST_BYTES).Select(GuestPattern).ToArray();
        if (!written.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException($"debugfs reads {written.Length} other bytes in /guest/logs/written.bin than the guest wrote.");
        if (Encoding.UTF8.GetString(await DumpAsync(tools, image, "/guest/note.txt")) != GUEST_NOTE)
            throw new InvalidOperationException("debugfs reads another /guest/note.txt than the guest wrote.");
        if (Encoding.UTF8.GetString(await DumpAsync(tools, image, "/posix-kept.txt")) != INIT_TEXT)
            throw new InvalidOperationException("debugfs reads another /posix-kept.txt than /bin/init wrote through the namespace service.");
        if (Encoding.UTF8.GetString(await DumpAsync(tools, image, "/dotnet-kept.txt")) != DOTNET_TEXT)
            throw new InvalidOperationException("debugfs reads another /dotnet-kept.txt than /bin/files wrote through System.IO.");
    }

    /// <summary>
    /// Requires what a cut left on a volume (D6.2): the guest had it mounted (needs_recovery), and the host's e2fsck
    /// replays its journal and finds it clean then. The image is the host's copy: the guest's next boot replays the
    /// journal itself on the disk.
    /// </summary>
    /// <param name="image">A copy of the volume's image.</param>
    /// <returns>When the volume is judged.</returns>
    /// <exception cref="InvalidOperationException">The volume was not mounted, or e2fsck cannot recover it.</exception>
    public static async Task CheckCutAsync(string image)
    {
        var tools = Path.GetDirectoryName(FindMkfs() ?? throw new InvalidOperationException("Judging the ext4 volume needs e2fsprogs."))!;
        var header = await Processes.RunAsync(Path.Combine(tools, "dumpe2fs"), ["-h", image], Path.GetTempPath(), 300);
        var features = header.Output.Split('\n').FirstOrDefault(line => line.StartsWith("Filesystem features:", StringComparison.Ordinal));
        if (header.ExitCode != 0 || features is null || !features.Contains("needs_recovery", StringComparison.Ordinal))
            throw new InvalidOperationException($"The cut did not find the guest's ext4 volume mounted: dumpe2fs says '{features}'.");
        var repair = await Processes.RunAsync(Path.Combine(tools, "e2fsck"), ["-fy", image], Path.GetTempPath(), 300);
        if (repair.ExitCode > 1)
            throw new InvalidOperationException($"e2fsck cannot recover the volume the cut left ({repair.ExitCode}): {repair.Output} {repair.Error}");
        var check = await Processes.RunAsync(Path.Combine(tools, "e2fsck"), ["-fn", image], Path.GetTempPath(), 300);
        if (check.ExitCode != 0)
            throw new InvalidOperationException($"e2fsck finds the volume the cut left unclean after its recovery ({check.ExitCode}): {check.Output}");
    }

    /// <summary>
    /// The byte the guest writes at an offset of /guest/logs/written.bin (Ext4Probe.Pattern).
    /// </summary>
    /// <param name="offset">The offset.</param>
    /// <returns>The byte.</returns>
    public static byte GuestPattern(int offset) => (byte)(offset % 241 ^ offset / 8192);

    /// <summary>
    /// The byte of /big.bin's data at an offset of its chunk, never zero, so that mkfs keeps every data block.
    /// </summary>
    /// <param name="chunk">The chunk.</param>
    /// <param name="offset">The offset in the chunk.</param>
    /// <returns>The byte.</returns>
    public static byte BigPattern(int chunk, int offset) => (byte)(1 + (chunk * 11 + offset) % 251);

    // The tree: a text file, a directory with a file, the sparse file, the htree directory and a link, and the programs
    // under /programs, executable as their sources are (mode 755).
    private static void WriteTree(string root, IReadOnlyList<(string Path, string Source)> programs)
    {
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        Directory.CreateDirectory(Path.Combine(root, "many"));
        File.WriteAllText(Path.Combine(root, "hello.txt"), HELLO_TEXT);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(Path.Combine(root, "hello.txt"), (UnixFileMode)Convert.ToInt32("644", 8));
        File.WriteAllText(Path.Combine(root, "docs", "readme.txt"), string.Concat(Enumerable.Repeat("A line of the ext4 volume.\n", 100)));
        using (var big = File.Create(Path.Combine(root, "big.bin")))
        {
            for (var chunk = 0; chunk < BIG_CHUNKS; chunk++)
            {
                big.Position = (long)chunk * BIG_STRIDE;
                big.Write([.. Enumerable.Range(0, BIG_CHUNK_BYTES).Select(offset => BigPattern(chunk, offset))]);
            }
        }
        for (var i = 0; i < MANY; i++)
            File.WriteAllBytes(Path.Combine(root, "many", $"an-entry-of-the-htree-directory-{i:000}"), []);
        File.CreateSymbolicLink(Path.Combine(root, "link"), LINK_TARGET);
        foreach (var (path, source) in programs)
        {
            var target = Path.Combine(root, PROGRAMS, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(target, (UnixFileMode)Convert.ToInt32("755", 8));
        }
    }

    private static async Task<byte[]> DumpAsync(string tools, string image, string file)
    {
        var output = Path.Combine(Path.GetDirectoryName(image)!, "dump-" + Path.GetFileName(file));
        File.Delete(output);
        await RunAsync(Path.Combine(tools, "debugfs"), ["-R", $"dump {file} {output}", image], 0);
        if (!File.Exists(output))
            throw new InvalidOperationException($"debugfs found no {file} in the guest's ext4 volume.");
        var bytes = await File.ReadAllBytesAsync(output);
        File.Delete(output);
        return bytes;
    }

    private static async Task RunAsync(string program, string[] arguments, int acceptedExit)
    {
        var result = await Processes.RunAsync(program, arguments, Path.GetTempPath(), 300);
        if (result.TimedOut || (result.ExitCode != 0 && result.ExitCode != acceptedExit))
            throw new InvalidOperationException($"{program} {string.Join(' ', arguments)} exited with {result.ExitCode}: {result.Error}");
    }

    #endregion
}
