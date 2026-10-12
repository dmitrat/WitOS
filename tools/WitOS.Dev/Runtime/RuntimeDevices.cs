using System.Security;
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
    /// The namespace service's sources (D5.1).
    /// </summary>
    public const string NAMESPACE_PROGRAM = "src/Services/Namespace";

    /// <summary>
    /// The executable runtime-witos publishes from the namespace service, /system/Namespace.
    /// </summary>
    public const string NAMESPACE_EXECUTABLE = "Namespace";

    /// <summary>
    /// The System.IO check's sources (D5.2): an ordinary .NET program /bin/init starts as /bin/files.
    /// </summary>
    public const string FILES_PROGRAM = "tests/Runtime.Witos/Files";

    /// <summary>
    /// The executable runtime-witos publishes from the System.IO check.
    /// </summary>
    public const string FILES_EXECUTABLE = "Files";

    /// <summary>
    /// The runtime-devices scenario's /bin/init: it starts after the device manager answered.
    /// </summary>
    public const string DEVICES_INIT = "tests/User/devices_init.c";

    // The source folders of OutWit.OS every service compiles; never obj or bin, which a host build leaves.
    private static readonly string[] OUTWIT_OS_FOLDERS = ["Native", "Resources", "Communication", "Hardware", "Execution", "Storage", "Namespaces"];

    // OutWit.OS's filesystem providers (D4.1, D4.2), which a service compiles with OutWit.OS's package references.
    private static readonly string[] OUTWIT_OS_FILESYSTEM_FOLDERS = ["FileSystems", "FileSystems/Fat", "FileSystems/Ext4"];

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
        await PublishServiceAsync(root, tree, architecture, version, sysroot, NAMESPACE_EXECUTABLE, NAMESPACE_PROGRAM, ["Server", "Volumes"],
            OUTWIT_OS_FILESYSTEM_FOLDERS);
        var files = await RuntimeWitos.PublishProgramAsync(root, tree, architecture, FILES_EXECUTABLE, Path.Combine(root, FILES_PROGRAM), version, "");
        File.Copy(files, Path.Combine(RuntimeWitos.ProgramDirectory(root, architecture), FILES_EXECUTABLE), overwrite: true);
        var image = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_DEVICES_SCENARIO, architecture: architecture);
        var dataDisk = Path.Combine(Path.GetDirectoryName(image)!, "data-disk.img");
        await RuntimeDataDisk.CreateAsync(dataDisk);
        await BootScenarioRunner.RunAsync(root, image, new BootRequest($"{architecture.Name}-runtime-devices-256", 256, 600, ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            RequiredLines = RequiredLines(architecture),
            DataDisk = dataDisk
        });
        var check = await RuntimeDataDisk.CheckAsync(dataDisk);
        Console.WriteLine($"Devices on witos-{architecture.Name}: the device manager discovered the devices, moved each disk to its " +
            $"driver and wrote the data disk through the block protocol and the FAT provider before /bin/init; the image holds the " +
            $"{RuntimeDataDisk.TEST_PATTERN_BYTES} bytes and the guest's files on a clean FAT volume ({check.Files} files, " +
            $"{check.Directories} directories), and e2fsck finds clean the ext4 partition the guest wrote, whose files debugfs reads.");
    }

    /// <summary>
    /// The lines the runtime-devices scenario requires (D1.1, D1.2, D2, D3.2, D4.1, D4.2, D4.3).
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The required lines.</returns>
    public static string[] RequiredLines(KernelArchitecture architecture) =>
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
        $"[DEVICES] ext4 pci/",
        $": partition \"{RuntimeDataDisk.EXT4_PARTITION}\": ext4 volume \"{RuntimeExt4Volume.LABEL}\" of " +
            $"{(RuntimeDataDisk.EXT4_PARTITION_LAST_BLOCK - RuntimeDataDisk.EXT4_PARTITION_FIRST_BLOCK + 1) * 512} bytes in blocks of 4096, writable, " +
            "/ holds big.bin, docs, hello.txt, link, lost+found, many",
        $": /hello.txt (mode 644) says \"{RuntimeExt4Volume.HELLO_TEXT}\"",
        $": /big.bin holds {RuntimeExt4Volume.BIG_BYTES} bytes, {RuntimeExt4Volume.BIG_CHUNKS} chunks between holes, as the host wrote them",
        $": /many holds {RuntimeExt4Volume.MANY} entries, /link points to {RuntimeExt4Volume.LINK_TARGET}",
        $": check clean ({RuntimeExt4Volume.MANY + 3} files, 4 directories)",
        $": wrote /guest/logs/written.bin ({RuntimeExt4Volume.GUEST_BYTES} bytes around a hole) and /guest/note.txt, added " +
            $"{RuntimeExt4Volume.GUEST_ADDED} entries to /many, removed /docs/readme.txt, moved /link to /guest/link, through the journal",
        $": mounted again, /guest holds link, logs, note.txt, /many holds {RuntimeExt4Volume.MANY + RuntimeExt4Volume.GUEST_ADDED} entries, " +
            $"read back what it wrote, check clean ({RuntimeExt4Volume.MANY + 3 - 1 + 2 + RuntimeExt4Volume.GUEST_ADDED} files, 6 directories)",
        $"[DEVICES] device manager on {(architecture == KernelArchitecture.X64 ? "X64" : "Arm64")}: ",
        " 2 drivers ready, DMA isolation: none (no IOMMU), ready",
        "[NAMESPACE] mounted /boot: FAT16 volume of ",
        $"[NAMESPACE] mounted /data: ext4 volume \"{RuntimeExt4Volume.LABEL}\" of " +
            $"{(RuntimeDataDisk.EXT4_PARTITION_LAST_BLOCK - RuntimeDataDisk.EXT4_PARTITION_FIRST_BLOCK + 1) * 512} bytes, writable, from pci/",
        $"[NAMESPACE] mounted /fat: FAT",
        "[NAMESPACE] namespace service: 3 volumes mounted, serving their files",
        "[DEVICES] namespace service /system/Namespace with 2 disks: ready",
        "[DEVICES-INIT] POSIX files over the namespace service: ",
        " checks passed, 0 failed",
        $"[FILES] System.IO over the namespace service under NativeAOT on {(architecture == KernelArchitecture.X64 ? "X64" : "Arm64")}: " +
            "10 areas passed, 0 failed",
        "[DEVICES-INIT] /bin/files exited with 0",
        "[ROOT-TASK] /system/DeviceManager was ready before /bin/init",
        architecture.RootTaskPassedLine
    ];

    /// <summary>
    /// The package of the runtime-devices scenario: the device manager as /system/DeviceManager, the virtio block driver
    /// as /system/Drivers/VirtioBlock, tests/User/devices_init.c as /bin/init, musl's libc.so as the dynamic linker and
    /// the shared C++ runtime in /lib.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> BuildPackageAsync(string root, string output,
        KernelArchitecture architecture)
    {
        var program = RuntimeWitos.ProgramPackage(root, architecture, DEVICE_MANAGER_EXECUTABLE);
        var driver = Path.Combine(RuntimeWitos.ProgramDirectory(root, architecture), VIRTIO_BLOCK_EXECUTABLE);
        var names = Path.Combine(RuntimeWitos.ProgramDirectory(root, architecture), NAMESPACE_EXECUTABLE);
        var files = Path.Combine(RuntimeWitos.ProgramDirectory(root, architecture), FILES_EXECUTABLE);
        if (!File.Exists(driver) || !File.Exists(names) || !File.Exists(files))
            throw new InvalidOperationException($"No witos driver or namespace service in {RuntimeWitos.ProgramDirectory(root, architecture)}. " +
                $"Run: dotnet run --project tools/WitOS.Dev -- runtime-services --arch {architecture.Name}");
        await Sysroot.BuildAsync(root, architecture);
        var init = Path.Combine(output, "devices_init.o");
        await MuslLibc.CompileAsync(root, architecture, Path.Combine(root, DEVICES_INIT), init, LibWitos.Options(root));
        var initProgram = await MuslLibc.LinkStartedProgramAsync(root, architecture, output, "devices_init", [init]);
        return
        [
            ("system/" + DEVICE_MANAGER_EXECUTABLE, program[0].Source), ("system/Drivers/" + VIRTIO_BLOCK_EXECUTABLE, driver),
            ("system/" + NAMESPACE_EXECUTABLE, names), ("bin/files", files),
            ("bin/init", initProgram), .. program.Skip(1)
        ];
    }

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
