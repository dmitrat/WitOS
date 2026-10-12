using System.Security;
using WitOS.Dev.Kernel;
using WitOS.Dev.Substrate;

namespace WitOS.Dev.Runtime;

/// <summary>
/// Phase D's services on the runtime runtime-witos built (ADR 0025): NativeAOT programs of the boot package, each built
/// with the OutWit.OS sources and libwitos. The device manager (D1.1) discovers the devices the root task moves the
/// table of to it, and starts the virtio block driver with the device it moves to it (D1.2); the runtime-devices
/// scenario boots both under the system layer's root task before /bin/init.
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
    /// The runtime-devices scenario's /bin/init: it starts after the device manager answered.
    /// </summary>
    public const string DEVICES_INIT = "tests/User/devices_init.c";

    // The source folders of OutWit.OS a service compiles; never obj or bin, which a host build leaves.
    private static readonly string[] OUTWIT_OS_FOLDERS = ["Native", "Resources", "Communication", "Hardware", "Execution"];

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
            ["Uhi", "Drivers"]);
        await PublishServiceAsync(root, tree, architecture, version, sysroot, VIRTIO_BLOCK_EXECUTABLE, VIRTIO_BLOCK_PROGRAM, []);
        var image = await KernelImageBuilder.BuildAsync(root, KernelImageBuilder.RUNTIME_DEVICES_SCENARIO, architecture: architecture);
        await BootScenarioRunner.RunAsync(root, image, new BootRequest($"{architecture.Name}-runtime-devices-256", 256, 600, ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            RequiredLines = RequiredLines(architecture)
        });
        Console.WriteLine($"Devices on witos-{architecture.Name}: the device manager discovered the devices and moved the boot disk " +
            "to its driver before /bin/init.");
    }

    /// <summary>
    /// The lines the runtime-devices scenario requires (D1.1, D1.2).
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The required lines.</returns>
    public static string[] RequiredLines(KernelArchitecture architecture) =>
    [
        "[DEVICES] Device discovered: Class: BlockStorageController, Provider: Virtio, DeviceId: pci/",
        "[DEVICES] Device discovered: Class: EntropySource, Provider: Virtio, DeviceId: pci/",
        "[VIRTIO-BLOCK] driver of pci/",
        ": holds the device, its configuration space reads 1af4:",
        "[DEVICES] driver /system/Drivers/VirtioBlock of pci/",
        $"[DEVICES] device manager on {(architecture == KernelArchitecture.X64 ? "X64" : "Arm64")}: ",
        " drivers ready, DMA isolation: none (no IOMMU), ready",
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
        if (!File.Exists(driver))
            throw new InvalidOperationException($"No witos driver in {driver}. Run: dotnet run --project tools/WitOS.Dev -- runtime-services --arch {architecture.Name}");
        await Sysroot.BuildAsync(root, architecture);
        var init = Path.Combine(output, "devices_init.o");
        await MuslLibc.CompileAsync(root, architecture, Path.Combine(root, DEVICES_INIT), init, LibWitos.Options(root));
        var initProgram = await MuslLibc.LinkStartedProgramAsync(root, architecture, output, "devices_init", [init]);
        return
        [
            ("system/" + DEVICE_MANAGER_EXECUTABLE, program[0].Source), ("system/Drivers/" + VIRTIO_BLOCK_EXECUTABLE, driver),
            ("bin/init", initProgram), .. program.Skip(1)
        ];
    }

    // A service published with the SDK's targets for witos: its own sources and the folders given, OutWit.OS's source
    // folders, and libwitos.a bound directly; the executable goes beside the other witos programs.
    private static async Task PublishServiceAsync(string root, string tree, KernelArchitecture architecture, string version,
        string sysroot, string name, string program, string[] folders)
    {
        var compile = string.Concat(OUTWIT_OS_FOLDERS.Select(folder => Path.Combine(root, OUTWIT_OS_SOURCES, folder))
            .Concat(folders.Select(folder => Path.Combine(root, program, folder))).Select(Compile));
        var published = await RuntimeWitos.PublishProgramAsync(root, tree, architecture, name, Path.Combine(root, program), version,
            $$"""
                <ItemGroup>
            {{compile}}
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
