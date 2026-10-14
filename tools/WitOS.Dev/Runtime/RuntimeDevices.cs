using System.Security;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WitOS.Dev.Kernel;
using WitOS.Dev.Substrate;

namespace WitOS.Dev.Runtime;

/// <summary>
/// Phase D's services on the runtime runtime-witos built (ADR 0025): NativeAOT programs of the boot package, each built
/// with the OutWit.OS sources and libwitos. The device manager (D1.1) discovers the devices the root task moves the
/// table of to it, and starts the virtio block driver with the device it moves to it (D1.2), which reads the boot disk
/// (D2) and serves the block protocol, through which the device manager reads a data disk's partition table and writes
/// its test partition (D3.2) and its FAT partition through the FAT provider, beside the boot disk's FAT volume (D4.1);
/// the runtime-devices scenario boots them under the system layer's root task before /bin/init.
/// </summary>
internal static class RuntimeDevices
{
    #region Constants

    /// <summary>
    /// The device manager's sources (D1.1).
    /// </summary>
    public const string DEVICE_MANAGER_PROGRAM = "src/Services/DeviceManager";

    /// <summary>
    /// The virtio block driver's sources (D1.2).
    /// </summary>
    public const string VIRTIO_BLOCK_PROGRAM = "src/Services/Drivers/VirtioBlock";

    /// <summary>
    /// The virtio console driver's sources (N4.3a).
    /// </summary>
    public const string VIRTIO_CONSOLE_PROGRAM = "src/Services/Drivers/VirtioConsole";

    /// <summary>
    /// The terminal service's sources (N4.3a).
    /// </summary>
    public const string TERMINAL_PROGRAM = "src/Services/Terminal";

    /// <summary>
    /// The managed capability API every service compiles with (D1.1).
    /// </summary>
    public const string OUTWIT_OS_SOURCES = "src/OutWit.OS";

    /// <summary>
    /// The executable runtime-witos publishes from the device manager, /system/DeviceManager.
    /// </summary>
    public const string DEVICE_MANAGER_EXECUTABLE = "DeviceManager";

    /// <summary>
    /// The executable runtime-witos publishes from the virtio block driver, /system/Drivers/VirtioBlock.
    /// </summary>
    public const string VIRTIO_BLOCK_EXECUTABLE = "VirtioBlock";

    /// <summary>
    /// The executable runtime-witos publishes from the virtio console driver, /system/Drivers/VirtioConsole.
    /// </summary>
    public const string VIRTIO_CONSOLE_EXECUTABLE = "VirtioConsole";

    /// <summary>
    /// The executable runtime-witos publishes from the terminal service, /system/Terminal.
    /// </summary>
    public const string TERMINAL_EXECUTABLE = "Terminal";

    /// <summary>
    /// The namespace service's sources (D5.1).
    /// </summary>
    public const string NAMESPACE_PROGRAM = "src/Services/Namespace";

    /// <summary>
    /// The executable runtime-witos publishes from the namespace service, /system/Namespace.
    /// </summary>
    public const string NAMESPACE_EXECUTABLE = "Namespace";

    /// <summary>
    /// The System.IO check's sources (D5.2): an ordinary .NET program /bin/init starts from the ext4 volume (D5.3).
    /// </summary>
    public const string FILES_PROGRAM = "tests/Runtime.Witos/Files";

    /// <summary>
    /// The sources of the .NET application on the ext4 volume and of its library (D5.3), which dotnet runs from there.
    /// </summary>
    public const string VOLUME_APP = "tests/Runtime.Witos/VolumeApp";

    /// <summary>
    /// What the application reads from the file beside it on the volume (D5.3).
    /// </summary>
    public const string VOLUME_GREETING = "Read from the ext4 volume";

    /// <summary>
    /// The boots of one data disk the scenario makes (D6.1, D6.2, D6.3): two that end with the services stopped, a third
    /// that the host cuts off while /bin/init writes rounds of files, a fourth that finds what the cut left, and a fifth on
    /// a disk that fails some reads.
    /// </summary>
    public const int BOOTS = 5;

    /// <summary>
    /// The boot whose data disk fails the reads of /big.bin's first block (devices_init.c's FAIL_BOOT, D6.3).
    /// </summary>
    public const int FAIL_BOOT = 5;

    /// <summary>
    /// The boot the host cuts off (devices_init.c's CUT_BOOT, D6.2).
    /// </summary>
    public const int CUT_BOOT = 3;

    /// <summary>
    /// The cut writer's report after which the host cuts the boot off: the cut falls in a later round.
    /// </summary>
    public const string CUT_AFTER = "[DEVICES-INIT] cut writer: round 20 durable";

    /// <summary>
    /// The longest wall-clock wait after <see cref="CUT_AFTER"/> before the cut, chosen at random: a round takes some
    /// hundreds of milliseconds under TCG, so the cut falls anywhere in the rounds after it, inside a transaction or
    /// between two.
    /// </summary>
    public const int CUT_JITTER_MILLISECONDS = 1500;

    /// <summary>
    /// The programs the scenario lays on the ext4 volume under /programs and starts from there (D5.3): the .NET
    /// application with its runtimeconfig, its library and the file it reads, the dynamic program with its library and
    /// the library it opens, and the System.IO check.
    /// </summary>
    public static readonly string[] VOLUME_PROGRAMS =
    [
        "app/App.dll", "app/App.runtimeconfig.json", "app/VolumeLibrary.dll", "app/greeting.txt", "dynamic/dynamic",
        "dynamic/libdynamic.so", "dynamic/libplugin.so", "files/Files"
    ];

    /// <summary>
    /// The executable runtime-witos publishes from the System.IO check.
    /// </summary>
    public const string FILES_EXECUTABLE = "Files";

    /// <summary>
    /// The runtime-devices scenario's /bin/init: it starts after the device manager answered.
    /// </summary>
    public const string DEVICES_INIT = "tests/User/devices_init.c";

    // The source folders of OutWit.OS every service compiles; never obj or bin, which a host build leaves.
    private static readonly string[] OUTWIT_OS_FOLDERS =
    [
        "Native", "Resources", "Communication", "Hardware", "Hardware/Virtio", "Execution", "Storage", "Namespaces", "Terminals"
    ];

    // OutWit.OS's filesystem providers (D4.1, D4.2, N4.1), which a service compiles with OutWit.OS's package references.
    private static readonly string[] OUTWIT_OS_FILESYSTEM_FOLDERS = ["FileSystems", "FileSystems/Fat", "FileSystems/Ext4", "FileSystems/Memory"];

    #endregion

    #region Functions

    /// <summary>
    /// Phase D's step of runtime-witos alone, on the tree and the packages an earlier runtime-witos built: the services
    /// published and booted again without the runtime's build and its other scenarios (the runtime-services command).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <exception cref="InvalidOperationException">runtime-witos built no tree for the pin.</exception>
    public static Task ServicesAsync(string root, KernelArchitecture architecture)
    {
        var tree = Path.Combine(root, "artifacts", "runtime-witos", "src");
        if (!Directory.Exists(Path.Combine(tree, ".dotnet")))
            throw new InvalidOperationException($"No witos tree in {tree}. Run: dotnet run --project tools/WitOS.Dev -- runtime-witos --arch {architecture.Name}");
        return BuildAsync(root, tree, architecture, RuntimeWitos.PackageVersion(tree, architecture));
    }

    /// <summary>
    /// Publishes the device manager and the virtio block driver for the architecture and boots the runtime-devices
    /// scenario.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="tree">The witos tree, whose SDK publishes.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="version">The version of the NativeAOT packages in the tree's feed.</param>
    public static async Task BuildAsync(string root, string tree, KernelArchitecture architecture, string version)
    {
        var sysroot = await Sysroot.BuildAsync(root, architecture); // with libwitos of the current sources
        await PublishServiceAsync(root, tree, architecture, version, sysroot, DEVICE_MANAGER_EXECUTABLE, DEVICE_MANAGER_PROGRAM,
            ["Uhi", "Drivers"], OUTWIT_OS_FILESYSTEM_FOLDERS);
        await PublishServiceAsync(root, tree, architecture, version, sysroot, VIRTIO_BLOCK_EXECUTABLE, VIRTIO_BLOCK_PROGRAM, ["Virtio"], []);
        await PublishServiceAsync(root, tree, architecture, version, sysroot, VIRTIO_CONSOLE_EXECUTABLE, VIRTIO_CONSOLE_PROGRAM, ["Virtio"], []);
        await PublishServiceAsync(root, tree, architecture, version, sysroot, TERMINAL_EXECUTABLE, TERMINAL_PROGRAM, ["Server"], []);
        await PublishServiceAsync(root, tree, architecture, version, sysroot, NAMESPACE_EXECUTABLE, NAMESPACE_PROGRAM, ["Server", "Volumes"],
            OUTWIT_OS_FILESYSTEM_FOLDERS);
        var files = await RuntimeWitos.PublishProgramAsync(root, tree, architecture, FILES_EXECUTABLE, Path.Combine(root, FILES_PROGRAM), version, "");
        var image = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_DEVICES_SCENARIO, architecture: architecture);
        var programs = await BuildVolumeProgramsAsync(root, Path.GetDirectoryName(image)!, architecture, files);
        var dataDisk = Path.Combine(Path.GetDirectoryName(image)!, "data-disk.img");
        await RuntimeDataDisk.CreateAsync(dataDisk, programs: programs);
        // Boots of one data disk (D6.1, D6.2): the first writes it, the services stop and unmount its volumes, and the second
        // finds what the first wrote; the third is cut off while it writes, and the fourth finds what the cut left. The
        // host judges the disk after each: unmounted and clean after a stop, replayable by e2fsck after the cut.
        OutWit.OS.FileSystems.FileSystemCheckReport? check = null;
        var durable = -1;
        for (var boot = 1; boot <= BOOTS; boot++)
        {
            var name = $"{architecture.Name}-runtime-devices-512-boot{boot}";
            long[] failing = boot == FAIL_BOOT ? [await RuntimeDataDisk.Ext4SectorAsync(dataDisk, "/big.bin")] : [];
            await BootScenarioRunner.RunAsync(root, image, new BootRequest(name, 512, 1200,
                boot == CUT_BOOT ? ExpectedOutcome.Cut : ExpectedOutcome.Success)
            {
                Architecture = architecture,
                Suite = BootSuite.Release,
                RequiredLines = RequiredLines(architecture, boot),
                DataDisk = dataDisk,
                CutAfter = boot == CUT_BOOT ? CUT_AFTER : null,
                CutJitterMilliseconds = CUT_JITTER_MILLISECONDS,
                DataDiskReadErrors = failing
            });
            var serial = await File.ReadAllTextAsync(Path.Combine(root, "artifacts", "logs", name + ".serial.log"));
            if (boot == CUT_BOOT)
            {
                durable = Regex.Matches(serial, @"cut writer: round (\d+) durable").Select(match => int.Parse(match.Groups[1].Value)).Max();
                await RuntimeDataDisk.CheckCutAsync(dataDisk);
                continue;
            }
            if (boot > CUT_BOOT)
            {
                // Every round the cut boot reported durable is there after the cut.
                var found = int.Parse(Regex.Match(serial, @"after the cut: /data/crash holds rounds 0 to (-?\d+)").Groups[1].Value);
                if (found < durable)
                    throw new InvalidOperationException($"After the cut the volume holds rounds to {found}, but round {durable} was durable.");
                if (boot == CUT_BOOT + 1)
                    Console.WriteLine($"Cut on witos-{architecture.Name}: round {durable} was the last reported durable, and the volume holds rounds to {found}.");
            }
            check = await RuntimeDataDisk.CheckAsync(dataDisk, boot);
        }
        Console.WriteLine($"Devices on witos-{architecture.Name}: the device manager discovered the devices, moved each disk to its " +
            $"driver and wrote the data disk through the block protocol and the FAT provider before /bin/init; the image holds the " +
            $"{RuntimeDataDisk.TEST_PATTERN_BYTES} bytes and the guest's files on a clean FAT volume ({check!.Files} files, " +
            $"{check.Directories} directories), and e2fsck finds clean the ext4 partition the guest wrote and unmounted, whose files " +
            "debugfs reads after each of two boots of the disk.");
    }

    /// <summary>
    /// The lines the runtime-devices scenario requires (D1.1, D1.2, D2, D3.2, D4.1, D4.2, D4.3).
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The required lines.</returns>
    public static string[] RequiredLines(KernelArchitecture architecture, int boot = 1) =>
    [
        .. FirstLines(),
        .. boot > 1 ? [$": partition \"{RuntimeDataDisk.TEST_PARTITION}\": holds the {RuntimeDataDisk.TEST_PATTERN_BYTES} bytes a previous boot wrote",
            $": partition \"{RuntimeDataDisk.FAT_PARTITION}\": after the reboot, read back what a previous boot wrote"] : Array.Empty<string>(),
        .. boot == 1 ? FirstExt4Lines() : boot == CUT_BOOT + 1 ? AfterCutExt4Lines() : AfterRebootExt4Lines(counted: boot <= CUT_BOOT),
        .. MiddleLines(architecture, boot),
        .. boot == CUT_BOOT ? [CUT_AFTER] : LastLines(architecture)
    ];

    // What every boot of the scenario prints first: discovery, the drivers, the disks and the FAT volumes.
    private static string[] FirstLines() =>
    [
        "[DEVICES] Device discovered: Class: BlockStorageController, Provider: Virtio, DeviceId: pci/",
        "[DEVICES] Device discovered: Class: EntropySource, Provider: Virtio, DeviceId: pci/",
        "[VIRTIO-BLOCK] driver of pci/",
        ": holds the device, its configuration space reads 1af4:",
        ": read sector 0 through queue 0 and its interrupt: a boot sector (signature aa55, file system FAT",
        ": read sector 0 through queue 0 and its interrupt: a protective MBR (signature aa55) of a GUID partition table",
        $": {RuntimeDataDisk.BYTES} bytes in blocks of 512, writable, a volatile cache it flushes, transfers of up to ",
        " bytes in blocks of 512, read-only, ",
        ": serving the block protocol",
        "[DEVICES] driver /system/Drivers/VirtioBlock of pci/",
        $": {RuntimeDataDisk.BYTES} bytes in blocks of 512, writable, through the block channel and a window of 262144 bytes",
        ": the driver refuses a write with errno 30",
        ": no GUID partition table",
        $": GUID partition table of 3 partitions: \"{RuntimeDataDisk.TEST_PARTITION}\" on blocks {RuntimeDataDisk.TEST_PARTITION_FIRST_BLOCK}-" +
            $"{RuntimeDataDisk.TEST_PARTITION_LAST_BLOCK}, \"{RuntimeDataDisk.FAT_PARTITION}\" on blocks {RuntimeDataDisk.FAT_PARTITION_FIRST_BLOCK}-" +
            $"{RuntimeDataDisk.FAT_PARTITION_LAST_BLOCK}, \"{RuntimeDataDisk.EXT4_PARTITION}\" on blocks {RuntimeDataDisk.EXT4_PARTITION_FIRST_BLOCK}-" +
            $"{RuntimeDataDisk.EXT4_PARTITION_LAST_BLOCK}",
        $": partition \"{RuntimeDataDisk.TEST_PARTITION}\": wrote {RuntimeDataDisk.TEST_PATTERN_BYTES} bytes durably and read them back",
        "[DEVICES] fat pci/",
        ": FAT16 volume of ",
        ", read-only, / holds EFI, ",
        "/EFI/BOOT/BOOT",
        " bytes, a PE image (MZ)",
        $": partition \"{RuntimeDataDisk.FAT_PARTITION}\": FAT",
        $", writable, {RuntimeDataDisk.HOST_FILE} says \"{RuntimeDataDisk.HOST_TEXT}\"",
        $": partition \"{RuntimeDataDisk.FAT_PARTITION}\": wrote {RuntimeDataDisk.GUEST_FILE} ({RuntimeDataDisk.GUEST_FILE_BYTES} bytes) and " +
            $"{RuntimeDataDisk.GUEST_NOTE}, check clean (",
        $": partition \"{RuntimeDataDisk.FAT_PARTITION}\": mounted again, /guest holds written.bin ({RuntimeDataDisk.GUEST_FILE_BYTES} bytes), " +
            "note.txt (",
        ", read back what it wrote",
        $"[DEVICES] ext4 pci/"
    ];

    // The ext4 probe on the volume as the host made it: read, written through the journal and read back (D4.2, D4.3).
    private static string[] FirstExt4Lines() =>
    [
        $": partition \"{RuntimeDataDisk.EXT4_PARTITION}\": ext4 volume \"{RuntimeExt4Volume.LABEL}\" of " +
            $"{(RuntimeDataDisk.EXT4_PARTITION_LAST_BLOCK - RuntimeDataDisk.EXT4_PARTITION_FIRST_BLOCK + 1) * 512} bytes in blocks of 4096, writable, " +
            $"/ holds big.bin, docs, hello.txt, link, lost+found, many, {RuntimeExt4Volume.PROGRAMS}",
        $": /hello.txt (mode 644) says \"{RuntimeExt4Volume.HELLO_TEXT}\"",
        $": /big.bin holds {RuntimeExt4Volume.BIG_BYTES} bytes, {RuntimeExt4Volume.BIG_CHUNKS} chunks between holes, as the host wrote them",
        $": /many holds {RuntimeExt4Volume.MANY} entries, /link points to {RuntimeExt4Volume.LINK_TARGET}",
        $": check clean ({RuntimeExt4Volume.MANY + 3 + VOLUME_PROGRAMS.Length} files, {4 + VolumeDirectories} directories)",
        $": wrote /guest/logs/written.bin ({RuntimeExt4Volume.GUEST_BYTES} bytes around a hole) and /guest/note.txt, added " +
            $"{RuntimeExt4Volume.GUEST_ADDED} entries to /many, removed /docs/readme.txt, moved /link to /guest/link, through the journal",
        $": mounted again, /guest holds link, logs, note.txt, /many holds {RuntimeExt4Volume.MANY + RuntimeExt4Volume.GUEST_ADDED} entries, " +
            $"read back what it wrote, check clean ({RuntimeExt4Volume.MANY + 3 - 1 + 2 + RuntimeExt4Volume.GUEST_ADDED + VOLUME_PROGRAMS.Length} files, " +
            $"{6 + VolumeDirectories} directories)"
    ];

    // The ext4 probe on the volume a previous boot wrote and unmounted (D6.1): read back, with the files /bin/init, the
    // System.IO check and the boot count left beside the probe's, counted until the cut boot writes its rounds (D6.2).
    private static string[] AfterRebootExt4Lines(bool counted) =>
    [
        ": a previous boot wrote the volume and unmounted it cleanly: no transaction to replay",
        $": after the reboot, /guest holds link, logs, note.txt, /many holds {RuntimeExt4Volume.MANY + RuntimeExt4Volume.GUEST_ADDED} entries, " +
            "read back what it wrote, check clean (" + (counted
                ? $"{RuntimeExt4Volume.MANY + 3 - 1 + 2 + RuntimeExt4Volume.GUEST_ADDED + VOLUME_PROGRAMS.Length + 3} files, {6 + VolumeDirectories} directories)"
                : "")
    ];

    // The ext4 probe on the volume the cut boot left mounted (D6.2): the journal replayed what was committed, and the
    // check is clean; the files the cut left make the counts vary.
    private static string[] AfterCutExt4Lines() =>
    [
        ": a previous boot wrote the volume and left it mounted: the journal replayed ",
        $": after the reboot, /guest holds link, logs, note.txt, /many holds {RuntimeExt4Volume.MANY + RuntimeExt4Volume.GUEST_ADDED} entries, " +
            "read back what it wrote, check clean ("
    ];

    // What every boot prints after the probes: the namespace service and /bin/init's count of the boots, the files the
    // previous boot left and, after the cut, the files the cut left (D6.1, D6.2).
    private static string[] MiddleLines(KernelArchitecture architecture, int boot) =>
    [
        $"[DEVICES] device manager on {(architecture == KernelArchitecture.X64 ? "X64" : "Arm64")}: ",
        " 2 drivers ready, DMA isolation: none (no IOMMU), ready",
        "[NAMESPACE] mounted /tmp: tmpfs volume of 67108864 bytes in memory", // N4.1
        "[NAMESPACE] mounted /boot: FAT16 volume of ",
        $"[NAMESPACE] mounted /data: ext4 volume \"{RuntimeExt4Volume.LABEL}\" of " +
            $"{(RuntimeDataDisk.EXT4_PARTITION_LAST_BLOCK - RuntimeDataDisk.EXT4_PARTITION_FIRST_BLOCK + 1) * 512} bytes, writable, from pci/",
        $"[NAMESPACE] mounted /fat: FAT",
        "[NAMESPACE] namespace service: 4 volumes mounted, serving their files",
        "[DEVICES] namespace service /system/Namespace with 2 disks: ready",
        $"[DEVICES-INIT] boot {boot} of the data disk",
        .. boot > 1 ? ["[DEVICES-INIT] /data/posix-kept.txt and /data/dotnet-kept.txt hold what the previous boot wrote: yes"] : Array.Empty<string>(),
        .. boot > CUT_BOOT ? [", each as its last durable change left it"] : Array.Empty<string>(),
        .. boot == FAIL_BOOT ? FailingDiskLines() : Array.Empty<string>()
    ];

    // The boot on a disk that fails the reads of /big.bin's first block (D6.3): the driver names the device and the
    // failed read, the namespace service the file and the read, /bin/init gets EIO, and the rest of the volume reads.
    private static string[] FailingDiskLines() =>
    [
        ": the device failed a read at sector ",
        ": status 1 (VIRTIO_BLK_S_IOERR); the client gets EIO",
        "[NAMESPACE] a read of /data/big.bin (",
        ") failed below the volume: The driver answered READ of ",
        " with errno 5.",
        "[DEVICES-INIT] a read of /data/big.bin on the failing sector: EIO (Input/output error); /data/hello.txt still reads"
    ];

    // What a boot that is not cut off prints last: /bin/init's checks and programs, and the stop (D6.1).
    private static string[] LastLines(KernelArchitecture architecture) =>
    [
        "[DEVICES-INIT] POSIX files over the namespace service: ",
        " checks passed, 0 failed",
        $"[FILES] System.IO over the namespace service under NativeAOT on {(architecture == KernelArchitecture.X64 ? "X64" : "Arm64")}: " +
            "10 areas passed, 0 failed",
        $"[DEVICES-INIT] /data/{RuntimeExt4Volume.PROGRAMS}/files/Files exited with 0",
        architecture.DynamicPassedLine,
        $"[DEVICES-INIT] /data/{RuntimeExt4Volume.PROGRAMS}/dynamic/dynamic exited with 0",
        $"[VOLUME-APP] App.dll and its library from /data/{RuntimeExt4Volume.PROGRAMS}/app/ under CoreCLR on " +
            $"{(architecture == KernelArchitecture.X64 ? "X64" : "Arm64")}: {VOLUME_GREETING}, answered by VolumeLibrary",
        "[DEVICES-INIT] /dotnet/dotnet exited with 0",
        "[ROOT-TASK] /system/DeviceManager was ready before /bin/init",
        ", 4 volumes unmounted",
        "[DEVICES] namespace service /system/Namespace: stopped",
        ": stopped, the device reset",
        "[DEVICES] device manager: stopped",
        "[ROOT-TASK] /system/DeviceManager stopped",
        architecture.RootTaskPassedLine
    ];

    /// <summary>
    /// The package of the runtime-devices scenario: the device manager as /system/DeviceManager, the virtio block driver
    /// as /system/Drivers/VirtioBlock, the namespace service as /system/Namespace, tests/User/devices_init.c as /bin/init,
    /// musl's libc.so as the dynamic linker, the shared C++ runtime in /lib and the .NET install of R5 under /dotnet. The
    /// programs /bin/init starts lie on the data disk's ext4 volume instead (D5.3).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> BuildPackageAsync(string root, string output,
        KernelArchitecture architecture)
    {
        var services = ServiceFiles(root, architecture);
        await Sysroot.BuildAsync(root, architecture);
        var init = Path.Combine(output, "devices_init.o");
        await MuslLibc.CompileAsync(root, architecture, Path.Combine(root, DEVICES_INIT), init, LibWitos.Options(root));
        var initProgram = await MuslLibc.LinkStartedProgramAsync(root, architecture, output, "devices_init", [init]);
        // The .NET install of R5 (D5.3: dotnet runs the application on the volume), whose dynamic linker and C++ runtime
        // the services share.
        IEnumerable<(string Name, string Source)> package =
        [
            .. services, ("bin/init", initProgram), .. await RuntimeWitos.InstallFilesAsync(root, output, architecture)
        ];
        return [.. package.DistinctBy(file => file.Name)];
    }

    /// <summary>
    /// The device services' files of the boot package: the device manager, the virtio block driver and the namespace
    /// service runtime-witos or runtime-services published, with the dynamic linker and the C++ runtime they need.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    /// <exception cref="InvalidOperationException">No services were published for the architecture.</exception>
    public static IReadOnlyList<(string Name, string Source)> ServiceFiles(string root, KernelArchitecture architecture)
    {
        var program = RuntimeWitos.ProgramPackage(root, architecture, DEVICE_MANAGER_EXECUTABLE);
        var directory = RuntimeWitos.ProgramDirectory(root, architecture);
        var driver = Path.Combine(directory, VIRTIO_BLOCK_EXECUTABLE);
        var console = Path.Combine(directory, VIRTIO_CONSOLE_EXECUTABLE);
        var names = Path.Combine(directory, NAMESPACE_EXECUTABLE);
        var terminal = Path.Combine(directory, TERMINAL_EXECUTABLE);
        if (!File.Exists(driver) || !File.Exists(console) || !File.Exists(names) || !File.Exists(terminal))
            throw new InvalidOperationException($"No witos drivers, namespace service or terminal service in {directory}. " +
                $"Run: dotnet run --project tools/WitOS.Dev -- runtime-services --arch {architecture.Name}");
        return
        [
            ("system/" + DEVICE_MANAGER_EXECUTABLE, program[0].Source), ("system/Drivers/" + VIRTIO_BLOCK_EXECUTABLE, driver),
            ("system/Drivers/" + VIRTIO_CONSOLE_EXECUTABLE, console), ("system/" + NAMESPACE_EXECUTABLE, names),
            ("system/" + TERMINAL_EXECUTABLE, terminal), .. program.Skip(1)
        ];
    }

    // The programs the scenario starts from the ext4 volume (D5.3), as VOLUME_PROGRAMS names them: the .NET application
    // and its library compiled against the reference pack, the dynamic program of the spawn scenario with its library and
    // plugin, linked with RUNPATH $ORIGIN so that the dynamic linker finds them beside it, and the System.IO check.
    private static async Task<IReadOnlyList<(string Path, string Source)>> BuildVolumeProgramsAsync(string root, string output,
        KernelArchitecture architecture, string files)
    {
        var directory = Path.Combine(output, "volume-programs");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        async Task<string> Compile(string name, params string[] extra)
        {
            var obj = Path.Combine(directory, name + ".o");
            await MuslLibc.CompileAsync(root, architecture, Path.Combine(root, "tests", "User", name + ".c"), obj, [.. LibWitos.Options(root), .. extra]);
            return obj;
        }
        var library = await MuslLibc.LinkSharedLibraryAsync(root, architecture, directory, "libdynamic.so", [await Compile("dynamic_library", "-fPIC")]);
        var plugin = await MuslLibc.LinkSharedLibraryAsync(root, architecture, directory, "libplugin.so", [await Compile("dynamic_plugin", "-fPIC")]);
        var dynamic = await MuslLibc.LinkDynamicProgramAsync(root, architecture, directory, "dynamic",
            [await Compile("dynamic_main", $"-DLIBRARY_PATH=\"/data/{RuntimeExt4Volume.PROGRAMS}/dynamic/libdynamic.so\"")], [library],
            ["-rpath", "$ORIGIN"]);
        var volumeLibrary = Path.Combine(directory, "VolumeLibrary.dll");
        await RuntimeWitos.CompileAsync(root, volumeLibrary, "library", [Path.Combine(root, VOLUME_APP, "VolumeLibrary.cs")], []);
        var app = Path.Combine(directory, "App.dll");
        await RuntimeWitos.CompileAsync(root, app, "exe", [Path.Combine(root, VOLUME_APP, "VolumeApp.cs")], [volumeLibrary]);
        var config = Path.Combine(directory, "App.runtimeconfig.json");
        await RuntimeWitos.WriteRuntimeConfigAsync(root, config);
        var greeting = Path.Combine(directory, "greeting.txt");
        await File.WriteAllTextAsync(greeting, VOLUME_GREETING + "\n");
        (string Path, string Source)[] programs =
        [
            ("app/App.dll", app), ("app/App.runtimeconfig.json", config), ("app/VolumeLibrary.dll", volumeLibrary),
            ("app/greeting.txt", greeting), ("dynamic/dynamic", dynamic), ("dynamic/libdynamic.so", library),
            ("dynamic/libplugin.so", plugin), ("files/Files", files)
        ];
        if (!programs.Select(program => program.Path).SequenceEqual(VOLUME_PROGRAMS))
            throw new InvalidOperationException("The volume's programs differ from VOLUME_PROGRAMS.");
        return programs;
    }

    // The directories the volume's programs add: /programs and each directory below it.
    private static int VolumeDirectories => 1 + VOLUME_PROGRAMS.Select(path => path[..path.IndexOf('/')]).Distinct().Count();

    /// <summary>
    /// OutWit.OS's package references, as its project names them: what a service compiling its FAT provider references
    /// too, so that the version has one place.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Each package and its version.</returns>
    public static IReadOnlyList<(string Package, string Version)> PackageReferences(string root) =>
    [
        .. XDocument.Load(Path.Combine(root, OUTWIT_OS_SOURCES, "OutWit.OS.csproj")).Descendants("PackageReference")
            .Select(reference => ((string)reference.Attribute("Include")!, (string)reference.Attribute("Version")!))
    ];

    // A service published with the SDK's targets for witos: its own sources and the folders given, OutWit.OS's source
    // folders (and its FAT provider's, with OutWit.OS's package references), and libwitos.a bound directly; the
    // executable goes beside the other witos programs.
    private static async Task PublishServiceAsync(string root, string tree, KernelArchitecture architecture, string version,
        string sysroot, string name, string program, string[] folders, string[] outwitFolders)
    {
        var compile = string.Concat(OUTWIT_OS_FOLDERS.Concat(outwitFolders).Select(folder => Path.Combine(root, OUTWIT_OS_SOURCES, folder))
            .Concat(folders.Select(folder => Path.Combine(root, program, folder))).Select(Compile));
        var packages = outwitFolders.Length == 0 ? "" : string.Concat(PackageReferences(root).Select(reference =>
            $"      <PackageReference Include=\"{SecurityElement.Escape(reference.Package)}\" Version=\"{SecurityElement.Escape(reference.Version)}\" />\n"));
        var published = await RuntimeWitos.PublishProgramAsync(root, tree, architecture, name, Path.Combine(root, program), version,
            $$"""
                <ItemGroup>
            {{compile}}{{packages}}
                  <DirectPInvoke Include="libwitos" />
                  <NativeLibrary Include="{{SecurityElement.Escape(Path.Combine(sysroot, "usr", "lib", "libwitos.a"))}}" />
                </ItemGroup>
            """);
        File.Copy(published, Path.Combine(RuntimeWitos.ProgramDirectory(root, architecture), name), overwrite: true);
    }

    private static string Compile(string folder) =>
        $"      <Compile Include=\"{SecurityElement.Escape(Path.Combine(folder, "*.cs"))}\" />\n";

    #endregion
}
