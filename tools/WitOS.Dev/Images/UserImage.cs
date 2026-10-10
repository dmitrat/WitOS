using WitOS.Dev.Kernel;
using WitOS.Dev.Host;

namespace WitOS.Dev.Images;

/// <summary>
/// Builds the ring-3 test fixtures a self-test kernel embeds and the root task fixture.
/// </summary>
internal static class UserImage
{
    #region Constants

    // The root task fixture's sources in tests/User, linked in this order.
    private static readonly string[] ROOT_SOURCES = ["root.c", "root_mechanisms.c"];

    // The mechanism fixtures in C (plan step K8.3), one source in tests/User/Fixtures for both ISAs: the source, the
    // fixture's name, the C array the kernel self-test embeds and the header that holds it.
    private static readonly (string Source, string Name, string Symbol, string Header)[] CLANG_FIXTURES =
    [
        ("channels", "ChannelFixture", "wit_user_channel_image", "user_channel_image.h"),
        ("exceptions", "ExceptionFixture", "wit_user_exception_image", "user_exception_image.h"),
        ("memory_objects", "MemoryObjectFixture", "wit_user_memory_object_image", "user_memory_object_image.h"),
        ("devices", "DeviceFixture", "wit_user_device_image", "user_device_image.h"),
        ("interrupts", "InterruptFixture", "wit_user_interrupt_image", "user_interrupt_image.h"),
        ("virtio", "VirtioFixture", "wit_user_virtio_image", "user_virtio_image.h"),
        ("threads2", "Thread2Fixture", "wit_user_thread2_image", "user_thread2_image.h"),
        ("processes", "ProcessFixture", "wit_user_process_image", "user_process_image.h"),
        ("waits", "WaitFixture", "wit_user_wait_image", "user_wait_image.h"),
        ("entry", "UserFixture", "wit_user_test_image", "user_image.h"),
        ("threads", "ThreadFixture", "wit_user_thread_image", "user_thread_image.h"),
        ("processors", "ProcessorFixture", "wit_user_processor_image", "user_processor_image.h")
    ];

    #endregion

    #region Functions

    /// <summary>
    /// Builds the mechanism fixtures of the architecture (plan steps K8.3 and K8.4c): each source of tests/User/Fixtures
    /// compiled by the pinned clang, linked by lld at the image window as one executable segment that starts with the
    /// entry, converted to a flat image by <see cref="FlatImage"/> and embedded as the C array its kernel self-test
    /// includes.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="architecture">Target architecture.</param>
    public static async Task BuildFixturesAsync(string root, string output, KernelArchitecture architecture)
    {
        foreach (var (source, name, symbol, header) in CLANG_FIXTURES)
        {
            var obj = Path.Combine(output, name + ".o");
            var image = Path.Combine(output, name + ".elf");
            // Optimized for size: every fixture is one segment of a few pages.
            await CompileFreestandingAsync(root, architecture, Path.Combine(root, "tests", "User", "Fixtures", source + ".c"), obj,
                "-Os", "-I", Path.Combine(root, "tests", "User"));
            await Processes.RequireSuccessAsync(Toolchain.Lld(root),
            [
                "-o", image, "-static", "--no-dynamic-linker", "--build-id=none", "-z", "max-page-size=4096", "-z", "norelro",
                "--gc-sections", "-T", Path.Combine(root, "tests", "User", "Fixtures", "fixture.ld"), obj
            ], root);
            var (entry, segments) = FlatImage.ParseElf(await File.ReadAllBytesAsync(image), architecture.ElfMachine);
            if (segments.Count != 1 || entry != segments[0].Address)
                throw new InvalidDataException($"{name} must be one executable segment that starts with its entry, and no data.");
            await FlatImage.FromElfAsync(output, architecture.ElfMachine, image, name, symbol, header);
        }
    }

    #endregion

    #region Tools

    // A freestanding layer-2 compilation by the pinned clang for the architecture's triple: no libc and no runtime, the
    // ABI-1 transport of the sysroot and the kernel's ABI headers, and the options given, which come last.
    private static Task CompileFreestandingAsync(string root, KernelArchitecture architecture, string source, string obj,
        params string[] options) =>
        Processes.RequireSuccessAsync(Toolchain.Clang(root),
        [
            $"--target={architecture.Triple}", "-std=c11", "-O2", "-ffreestanding", "-fno-builtin", "-nostdlib", "-nostdlibinc",
            "-fPIE", "-fno-plt", "-fno-stack-protector", "-fno-asynchronous-unwind-tables", "-fno-unwind-tables",
            "-Wall", "-Wextra", "-Werror", .. architecture.ClangOptions,
            "-I", Path.Combine(root, "src", "Sysroot", "include"), "-I", Path.Combine(root, "src", "Kernel", "include"),
            .. options, "-c", source, "-o", obj
        ], root);

    /// <summary>
    /// Builds the root task fixture of the architecture as a flat image (plan steps K4 and T1): its C sources
    /// (tests/User/root.c and, since K8.2, root_mechanisms.c) compiled by the pinned clang for the architecture's
    /// triple against the sysroot's ABI-1
    /// transport header and the kernel's ABI headers, with no libc and no runtime, linked by lld as a static ELF at
    /// the component's image window (tests/User/root.ld) and converted by <see cref="FlatImage"/>; also embedded
    /// as a C array for the kernel self-test. Built for every scenario, since the release kernel starts it.
    /// </summary>
    /// <returns>Path of the flat image to place on the boot disk.</returns>
    public static async Task<string> BuildRootAsync(string root, string output, KernelArchitecture architecture)
    {
        var image = Path.Combine(output, "RootFixture.elf");
        var objects = new List<string>();
        foreach (var source in ROOT_SOURCES)
        {
            var obj = Path.Combine(output, "RootFixture." + Path.GetFileNameWithoutExtension(source) + ".o");
            await CompileFreestandingAsync(root, architecture, Path.Combine(root, "tests", "User", source), obj);
            objects.Add(obj);
        }
        await Processes.RequireSuccessAsync(Toolchain.Lld(root),
        [
            "-o", image, "-static", "--no-dynamic-linker", "--build-id=none", "-z", "max-page-size=4096", "-z", "norelro",
            "--gc-sections", "-T", Path.Combine(root, "tests", "User", "root.ld"), .. objects
        ], root);
        return await FlatImage.FromElfAsync(output, architecture.ElfMachine, image, "RootFixture", "wit_user_root_image", "user_root_image.h");
    }

    #endregion
}
