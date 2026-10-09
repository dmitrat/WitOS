using System.Security.Cryptography;

namespace WitOS.Dev.Host;

/// <summary>
/// Locates and verifies the pinned host tools: QEMU, firmware, MSVC, LLVM and 7-Zip. A Windows host extracts QEMU and
/// LLVM from their pinned Windows installers with 7-Zip; a Linux host (plan step T2.1a) extracts LLVM from its pinned
/// Linux archive and builds QEMU from its pinned source release, whose firmware is the same EDK II builds.
/// </summary>
internal static class Toolchain
{
    #region Constants

    public const string QEMU_VERSION = "11.1.0";

    private const string QEMU_INSTALLER = "qemu-w64-setup-20260811.exe";

    private const string QEMU_SHA512 = "5bcf9eed634e8575a37b74f445af41a2fe4106da512d0c30c368301d4c105037fdfab40a5287367a28a957624cddebbc8c07e16c88ab6634f554cdf3d16bf543";

    // QEMU's source release for a Linux host, by the SHA-256 of download.qemu.org's bytes (plan step T2.1a).
    private const string QEMU_SOURCE = "qemu-" + QEMU_VERSION + ".tar.xz";

    private const string QEMU_SOURCE_SHA256 = "6ee1d1a61f68212476b27108c26da5f449dc09b626d42f8279ba0dc2e08fa858";

    // The two system emulators WitOS boots and the options of their build: no documentation, no tools, no user-mode
    // emulation, warnings not fatal for the host compiler, and the device tree library QEMU carries.
    private static readonly string[] QEMU_CONFIGURE =
    [
        "--target-list=x86_64-softmmu,aarch64-softmmu", "--disable-docs", "--disable-tools", "--disable-user",
        "--disable-werror", "--enable-fdt=internal"
    ];

    // Official LLVM release used for coverage, sanitizers and clang-format.
    public const string LLVM_VERSION = "20.1.8";

    public const string LLVM_INSTALLER_SHA256 = "3197846a2b19063687dd56e93e34cd941e3548d907f23a6131571321bdf9fe7b";

    // The same release's Linux x64 archive (plan step T2.1a).
    public const string LLVM_LINUX_ARCHIVE_SHA256 = "1ead36b3dfcb774b57be530df42bec70ab2d239fbce9889447c7a29a4ddc1ae6";

    // The archive's bin/clang-format by its own bytes: decompressing one member of the 2 GiB archive takes minutes.
    private const string LLVM_LINUX_CLANG_FORMAT_SHA256 = "8ded0cd6430fa0d3422217e81d2d9a72647e18bb329dacdb691b8bff61bd2deb";

    // The archive's members setup extracts: the tools layer 2, the kernel and the formatter need, the targets of their
    // symbolic links, and the compiler's own headers.
    private static readonly string[] LLVM_LINUX_MEMBERS =
    [
        "bin/clang", "bin/clang-20", "bin/lld", "bin/ld.lld", "bin/lld-link", "bin/llvm-ar", "bin/llvm-objcopy",
        "bin/llvm-readobj", "bin/clang-format", "lib/clang/20/include"
    ];

    #endregion

    #region Functions

    /// <summary>
    /// Directory of the pinned QEMU package.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Directory path.</returns>
    public static string QemuDirectory(string root) => Path.Combine(root, ".tools", $"qemu-{QEMU_VERSION}");

    /// <summary>
    /// A host executable's file name: with .exe on Windows, as is elsewhere.
    /// </summary>
    /// <param name="name">Name without an extension.</param>
    /// <returns>File name.</returns>
    public static string Executable(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <summary>
    /// Path of one of the pinned QEMU's programs: beside the package's files on Windows, in bin of the built prefix on
    /// a Linux host.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="name">Program name without an extension.</param>
    /// <returns>Executable path.</returns>
    public static string QemuExecutable(string root, string name) => OperatingSystem.IsWindows()
        ? Path.Combine(QemuDirectory(root), Executable(name))
        : Path.Combine(QemuDirectory(root), "bin", name);

    /// <summary>
    /// Directory of the pinned QEMU's firmware: share in the Windows package, share/qemu of the built prefix.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Directory path.</returns>
    public static string QemuShareDirectory(string root) => OperatingSystem.IsWindows()
        ? Path.Combine(QemuDirectory(root), "share")
        : Path.Combine(QemuDirectory(root), "share", "qemu");

    /// <summary>
    /// Path of the pinned QEMU x86_64 system emulator.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Executable path.</returns>
    public static string Qemu(string root) => QemuExecutable(root, "qemu-system-x86_64");

    /// <summary>
    /// Path of the EDK II x86_64 firmware code image.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Firmware path.</returns>
    public static string Firmware(string root) => Path.Combine(QemuShareDirectory(root), "edk2-x86_64-code.fd");

    /// <summary>
    /// Path of the EDK II firmware variables template.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Template path.</returns>
    public static string FirmwareVariables(string root) => Path.Combine(QemuShareDirectory(root), "edk2-i386-vars.fd");

    /// <summary>
    /// The host's tar: Windows' own bsdtar, the one on the path elsewhere.
    /// </summary>
    /// <returns>Executable path or name.</returns>
    public static string Tar() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe")
        : "tar";

    /// <summary>
    /// Path of the cached pinned LLVM Linux archive (plan step T2.1a).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Archive path.</returns>
    public static string LlvmLinuxArchive(string root)
        => Path.Combine(root, ".tools", "downloads", $"LLVM-{LLVM_VERSION}-Linux-X64.tar.xz");

    /// <summary>
    /// Path of the cached pinned LLVM installer.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Installer path.</returns>
    public static string LlvmInstaller(string root)
        => Path.Combine(root, ".tools", "downloads", $"LLVM-{LLVM_VERSION}-win64.exe");

    /// <summary>
    /// Finds the installed 7-Zip executable used to extract the LLVM installer.
    /// </summary>
    /// <returns>Executable path.</returns>
    public static string SevenZip()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("Install 7-Zip from https://www.7-zip.org/; pinned tools are extracted with it.");
        }
        return path;
    }

    /// <summary>
    /// Downloads the pinned LLVM installer when missing and verifies its SHA-256.
    /// </summary>
    /// <remarks>
    /// Downloads the pinned installer when absent and always verifies its digest.
    /// </remarks>
    /// <param name="root">Repository root.</param>
    /// <returns>Installer path.</returns>
    public static async Task<string> RequireLlvmInstallerAsync(string root)
    {
        var installer = LlvmInstaller(root);
        Directory.CreateDirectory(Path.GetDirectoryName(installer)!);
        if (!File.Exists(installer))
        {
            var url = $"https://github.com/llvm/llvm-project/releases/download/llvmorg-{LLVM_VERSION}/LLVM-{LLVM_VERSION}-win64.exe";
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var partial = installer + ".partial";
            await using (var stream = File.Create(partial))
            {
                await response.Content.CopyToAsync(stream);
            }
            File.Move(partial, installer, overwrite: true);
        }
        await using (var stream = File.OpenRead(installer))
        {
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            if (digest != LLVM_INSTALLER_SHA256)
            {
                throw new InvalidDataException($"LLVM installer hash mismatch. Remove the invalid download: {installer}");
            }
        }
        return installer;
    }

    /// <summary>
    /// Extracts the pinned clang-format from the verified LLVM installer.
    /// </summary>
    /// <remarks>
    /// Extracts only clang-format from the verified installer on every use; a
    /// previously extracted executable is never trusted as the pinned tool.
    /// </remarks>
    /// <param name="root">Repository root.</param>
    /// <returns>clang-format path.</returns>
    public static async Task<string> PrepareClangFormatAsync(string root)
    {
        var directory = Path.Combine(root, ".tools", $"clang-format-{LLVM_VERSION}");
        if (OperatingSystem.IsWindows())
        {
            var installer = await RequireLlvmInstallerAsync(root);
            await Processes.RequireSuccessAsync(SevenZip(),
                ["e", installer, @"bin\clang-format.exe", $"-o{directory}", "-y", "-bso0", "-bsp0"], root);
        }
        else
        {
            // A Linux host (plan step T2.1a): the binary is pinned by its own hash, so a copy extracted earlier is used
            // only while its bytes are the pinned ones.
            Directory.CreateDirectory(directory);
            var copy = Path.Combine(directory, "clang-format");
            if (!File.Exists(copy) || await FileSha256Async(copy) != LLVM_LINUX_CLANG_FORMAT_SHA256)
            {
                var unpacked = await Processes.RunAsync(Tar(), ["-xJf", await RequireLlvmLinuxArchiveAsync(root), "-C", directory,
                    "--strip-components=2", $"LLVM-{LLVM_VERSION}-Linux-X64/bin/clang-format"], root, 600);
                if (unpacked.TimedOut || unpacked.ExitCode != 0)
                    throw new InvalidOperationException($"clang-format extraction failed. {unpacked.Error}");
                if (await FileSha256Async(copy) != LLVM_LINUX_CLANG_FORMAT_SHA256)
                    throw new InvalidDataException($"clang-format hash mismatch: {copy}");
            }
        }
        var formatter = Path.Combine(directory, Executable("clang-format"));
        var version = await Processes.RunAsync(formatter, ["--version"], root);
        if (version.ExitCode != 0 || !version.Output.Contains($"clang-format version {LLVM_VERSION}", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unexpected clang-format version: {version.Output.Trim()}");
        }
        return formatter;
    }

    /// <summary>
    /// Directory of the pinned clang, lld and LLVM resource headers that build layer 2 (plan step T1).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Directory path.</returns>
    public static string ClangDirectory(string root) => Path.Combine(root, ".tools", $"clang-{LLVM_VERSION}");

    /// <summary>
    /// Path of the pinned clang driver.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Executable path.</returns>
    public static string Clang(string root) => Path.Combine(ClangDirectory(root), "bin", Executable("clang"));

    /// <summary>
    /// Path of the pinned ELF linker.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Executable path.</returns>
    public static string Lld(string root) => Path.Combine(ClangDirectory(root), "bin", Executable("ld.lld"));

    /// <summary>
    /// Path of the pinned PE/COFF linker, which links the kernel's EFI image (plan step T3.1).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Executable path.</returns>
    public static string LldLink(string root) => Path.Combine(ClangDirectory(root), "bin", Executable("lld-link"));

    /// <summary>
    /// Path of the pinned archiver.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Executable path.</returns>
    public static string LlvmAr(string root) => Path.Combine(ClangDirectory(root), "bin", Executable("llvm-ar"));

    /// <summary>
    /// Requires the pinned clang and lld that <c>setup</c> extracts.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <exception cref="InvalidOperationException">The tools are missing.</exception>
    public static void RequireClang(string root)
    {
        if (!File.Exists(Clang(root)) || !File.Exists(Lld(root)) || !File.Exists(LldLink(root)) || !File.Exists(LlvmAr(root)) ||
            !Directory.Exists(Path.Combine(ClangDirectory(root), "lib", "clang", LLVM_VERSION.Split('.')[0], "include")))
            throw new InvalidOperationException($"Pinned clang {LLVM_VERSION} is missing. Run: dotnet run --project tools/WitOS.Dev -- setup");
    }

    /// <summary>
    /// Extracts the pinned clang, lld (as ld.lld and lld-link), llvm-ar, llvm-objcopy, llvm-readobj and the compiler's own
    /// headers (stdint.h and the like, which freestanding layer 2 code includes) from the verified LLVM installer; the
    /// installer is not executed.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>clang path.</returns>
    public static async Task<string> PrepareClangAsync(string root)
    {
        var directory = ClangDirectory(root);
        var major = LLVM_VERSION.Split('.')[0];
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            var prefix = $"LLVM-{LLVM_VERSION}-Linux-X64/";
            var unpacked = await Processes.RunAsync(Tar(), ["-xJf", await RequireLlvmLinuxArchiveAsync(root), "-C", directory,
                "--strip-components=1", .. LLVM_LINUX_MEMBERS.Select(member => prefix + member)], root, 600);
            if (unpacked.TimedOut || unpacked.ExitCode != 0)
                throw new InvalidOperationException($"clang extraction failed. {unpacked.Error}");
            return await RequireClangVersionAsync(root);
        }
        var installer = await RequireLlvmInstallerAsync(root);
        var extraction = await Processes.RunAsync(SevenZip(),
        [
            "x", installer, @"bin\clang.exe", @"bin\ld.lld.exe", @"bin\lld-link.exe", @"bin\llvm-ar.exe", @"bin\llvm-objcopy.exe",
            @"bin\llvm-readobj.exe",
            $@"lib\clang\{major}\include", $"-o{directory}", "-y", "-bso0", "-bsp0"
        ], root, 180);
        if (extraction.TimedOut || extraction.ExitCode != 0)
            throw new InvalidOperationException($"clang extraction failed. {extraction.Error}");
        return await RequireClangVersionAsync(root);
    }

    private static async Task<string> RequireClangVersionAsync(string root)
    {
        RequireClang(root);
        var version = await Processes.RunAsync(Clang(root), ["--version"], root);
        if (version.ExitCode != 0 || !version.Output.Contains($"clang version {LLVM_VERSION}", StringComparison.Ordinal))
            throw new InvalidDataException($"Unexpected clang version: {version.Output.Trim()}");
        return Clang(root);
    }

    /// <summary>
    /// Environment for NativeAOT publish children, with vswhere on the path.
    /// </summary>
    /// <returns>Environment variables to add.</returns>
    public static IReadOnlyDictionary<string, string> NativeAotEnvironment()
    {
        // NativeAOT's SDK invokes VS discovery scripts that can use bare
        // vswhere.exe. Scope its directory to the publish child process.
        var installer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer");
        if (!File.Exists(Path.Combine(installer, "vswhere.exe")))
            throw new InvalidOperationException("Install Visual Studio Build Tools with Desktop development with C++ (x64). vswhere.exe was not found.");
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = installer + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
        };
    }

    /// <summary>
    /// Finds the newest MSVC x64 host tools with cl, link and ml64.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Tool directory.</returns>
    public static Task<string> FindMsvcAsync(string root) =>
        FindMsvcAsync(root, "x64", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "ml64.exe");

    /// <summary>
    /// Finds the newest MSVC tools that run on x64 and build for <paramref name="target"/>: cl, link and the assembler.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="target">Target directory under bin/Hostx64, such as x64 or arm64.</param>
    /// <param name="component">Visual Studio component that installs those tools.</param>
    /// <param name="assembler">Assembler executable, such as ml64.exe or armasm64.exe.</param>
    /// <returns>Tool directory.</returns>
    /// <exception cref="InvalidOperationException">Visual Studio or the tools are not installed.</exception>
    public static async Task<string> FindMsvcAsync(string root, string target, string component, string assembler)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "MSVC builds the frozen line's fixtures on a Windows host until plan step K8 (T2.2); this needs them.");
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere))
            throw new InvalidOperationException("Install Visual Studio Build Tools with Desktop development with C++ (x64). vswhere.exe was not found.");
        var result = await Processes.RunAsync(vswhere,
            ["-latest", "-products", "*", "-requires", component, "-property", "installationPath"], root);
        var installation = result.Output.Trim();
        if (result.ExitCode != 0 || result.TimedOut || installation.Length == 0)
            throw new InvalidOperationException($"Visual Studio C++ tools for {target} were not found; install the {component} component.");
        var versionsRoot = Path.Combine(installation, "VC", "Tools", "MSVC");
        var candidate = Directory.GetDirectories(versionsRoot)
            .Where(path => Version.TryParse(Path.GetFileName(path), out _))
            .OrderByDescending(path => Version.Parse(Path.GetFileName(path)))
            .Select(path => Path.Combine(path, "bin", "Hostx64", target))
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "cl.exe")) && File.Exists(Path.Combine(path, "link.exe")) &&
                File.Exists(Path.Combine(path, assembler)));
        return candidate ?? throw new InvalidOperationException($"MSVC {target} compiler/linker/{assembler} were not found.");
    }

    /// <summary>
    /// Finds an x64 library in the newest installed Windows SDK.
    /// </summary>
    /// <param name="name">Library file name.</param>
    /// <returns>Library path.</returns>
    public static string FindWindowsSdkLibrary(string name)
    {
        if (Path.GetFileName(name) != name || !name.EndsWith(".lib", StringComparison.Ordinal))
            throw new ArgumentException("Expected an SDK library filename.", nameof(name));
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Windows Kits", "10", "Lib");
        var library = Directory.Exists(directory) ? Directory.GetDirectories(directory)
            .Where(path => Version.TryParse(Path.GetFileName(path), out _))
            .OrderByDescending(path => Version.Parse(Path.GetFileName(path)))
            .Select(path => Path.Combine(path, "um", "x64", name)).FirstOrDefault(File.Exists) : null;
        return library ?? throw new InvalidOperationException($"Install the Windows SDK x64 libraries; {name} was not found.");
    }

    /// <summary>
    /// Throws unless the pinned QEMU and its firmware are installed.
    /// </summary>
    /// <param name="root">Repository root.</param>
    public static void RequireQemu(string root)
    {
        if (!File.Exists(Qemu(root)) || !File.Exists(Firmware(root)) || !File.Exists(FirmwareVariables(root)))
            throw new InvalidOperationException("QEMU/EDK II are missing. Run: dotnet run --project tools/WitOS.Dev -- setup");
    }

    /// <summary>
    /// Downloads and verifies the pinned QEMU package into .tools.
    /// </summary>
    /// <param name="root">Repository root.</param>
    public static async Task SetupAsync(string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            await BuildQemuAsync(root);
            await PrepareSubstrateAsync(root);
            return;
        }
        var sevenZip = SevenZip();

        var downloads = Path.Combine(root, ".tools", "downloads");
        Directory.CreateDirectory(downloads);
        var installer = Path.Combine(downloads, QEMU_INSTALLER);
        if (!File.Exists(installer))
        {
            Console.WriteLine($"Downloading pinned QEMU {QEMU_VERSION} (about 197 MiB)...");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            using var response = await client.GetAsync($"https://qemu.weilnetz.de/w64/{QEMU_INSTALLER}", HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var partial = installer + ".partial";
            await using (var file = File.Create(partial))
                await response.Content.CopyToAsync(file);
            File.Move(partial, installer, overwrite: true);
        }
        await using (var file = File.OpenRead(installer))
        {
            var hash = Convert.ToHexString(await SHA512.HashDataAsync(file));
            if (!hash.Equals(QEMU_SHA512, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"QEMU SHA-512 mismatch. Remove the invalid download and run setup again: {installer}");
        }
        Console.WriteLine("SHA-512 verified. Extracting QEMU locally; the installer is not executed.");
        var extraction = await Processes.RunAsync(sevenZip,
            ["x", installer, $"-o{QemuDirectory(root)}", "-y", "-bso0", "-bsp0"], root, 180);
        if (extraction.TimedOut || extraction.ExitCode != 0)
            throw new InvalidOperationException($"QEMU extraction failed. {extraction.Error}");
        RequireQemu(root);
        Console.WriteLine($"Ready: {Qemu(root)}");
        await PrepareSubstrateAsync(root);
    }

    // The pinned LLVM tools, the musl tarball, compiler-rt, the LLVM runtimes and libc-test, on either host.
    private static async Task PrepareSubstrateAsync(string root)
    {
        Console.WriteLine($"Extracting clang {LLVM_VERSION} and lld from the verified LLVM release...");
        Console.WriteLine($"Ready: {await PrepareClangAsync(root)}");
        Console.WriteLine($"Ready: {await Substrate.MuslLibc.PrepareAsync(root)}");
        Console.WriteLine($"Ready: {await Substrate.CompilerRtBuiltins.PrepareAsync(root)}");
        Console.WriteLine($"Ready: {await Substrate.LlvmRuntimes.PrepareAsync(root)}");
        Console.WriteLine($"Ready: {await Substrate.LibcTestSuite.PrepareAsync(root)}");
    }

    /// <summary>
    /// Downloads the pinned LLVM Linux archive when missing and verifies its SHA-256 (plan step T2.1a).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Archive path.</returns>
    public static async Task<string> RequireLlvmLinuxArchiveAsync(string root)
        => await RequireDownloadAsync(LlvmLinuxArchive(root),
            $"https://github.com/llvm/llvm-project/releases/download/llvmorg-{LLVM_VERSION}/LLVM-{LLVM_VERSION}-Linux-X64.tar.xz",
            LLVM_LINUX_ARCHIVE_SHA256);

    // QEMU on a Linux host (plan step T2.1a): the pinned source release configured for the two system emulators and
    // installed into .tools/qemu-<version>, whose share/qemu holds the EDK II images the release carries. A stamp of the
    // source hash and the options keeps a finished build; anything else rebuilds from a clean tree.
    private static async Task BuildQemuAsync(string root)
    {
        var source = await RequireDownloadAsync(Path.Combine(root, ".tools", "downloads", QEMU_SOURCE),
            $"https://download.qemu.org/{QEMU_SOURCE}", QEMU_SOURCE_SHA256);
        var prefix = QemuDirectory(root);
        var stamp = Path.Combine(prefix, "witos-build.txt");
        var expected = string.Join('\n', [QEMU_SOURCE_SHA256, .. QEMU_CONFIGURE]) + "\n";
        if (File.Exists(stamp) && await File.ReadAllTextAsync(stamp) == expected && File.Exists(Qemu(root)))
        {
            RequireQemu(root);
            Console.WriteLine($"Ready: {Qemu(root)}");
            return;
        }
        var build = Path.Combine(root, ".tools", "qemu-build");
        if (Directory.Exists(build))
            Directory.Delete(build, recursive: true);
        if (Directory.Exists(prefix))
            Directory.Delete(prefix, recursive: true);
        Directory.CreateDirectory(build);
        Console.WriteLine($"Building QEMU {QEMU_VERSION} from its verified source release...");
        await Processes.RequireSuccessAsync(Tar(), ["-xJf", source, "-C", build, "--strip-components=1"], root);
        await RequireBuildStepAsync("./configure", ["--prefix=" + prefix, .. QEMU_CONFIGURE], build, 900);
        await RequireBuildStepAsync("make", ["-j" + Environment.ProcessorCount], build, 3600);
        await RequireBuildStepAsync("make", ["install"], build, 900);
        RequireQemu(root);
        await File.WriteAllTextAsync(stamp, expected);
        Directory.Delete(build, recursive: true);
        Console.WriteLine($"Ready: {Qemu(root)}");
    }

    private static async Task RequireBuildStepAsync(string executable, string[] arguments, string directory, int timeoutSeconds)
    {
        var result = await Processes.RunAsync(executable, arguments, directory, timeoutSeconds);
        if (result.TimedOut || result.ExitCode != 0)
            throw new InvalidOperationException($"{executable} {string.Join(' ', arguments)} failed (exit {result.ExitCode}, " +
                $"timeout={result.TimedOut}).\n{Tail(result.Output)}\n{Tail(result.Error)}");
    }

    private static string Tail(string text) => text.Length <= 4000 ? text : text[^4000..];

    private static async Task<string> FileSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    // A pinned download: fetched when missing, its SHA-256 always checked.
    private static async Task<string> RequireDownloadAsync(string path, string url, string sha256)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            Console.WriteLine($"Downloading {Path.GetFileName(path)}...");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var partial = path + ".partial";
            await using (var stream = File.Create(partial))
                await response.Content.CopyToAsync(stream);
            File.Move(partial, path, overwrite: true);
        }
        await using (var stream = File.OpenRead(path))
        {
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            if (digest != sha256)
                throw new InvalidDataException($"{Path.GetFileName(path)} hash mismatch. Remove the invalid download: {path}");
        }
        return path;
    }

    #endregion
}
