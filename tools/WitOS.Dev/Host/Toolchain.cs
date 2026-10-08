using System.Security.Cryptography;

namespace WitOS.Dev.Host;

/// <summary>
/// Locates and verifies the pinned host tools: QEMU, firmware, MSVC, LLVM clang-format and 7-Zip.
/// </summary>
internal static class Toolchain
{
    #region Constants

    public const string QEMU_VERSION = "11.1.0";

    private const string QEMU_INSTALLER = "qemu-w64-setup-20260811.exe";

    private const string QEMU_SHA512 = "5bcf9eed634e8575a37b74f445af41a2fe4106da512d0c30c368301d4c105037fdfab40a5287367a28a957624cddebbc8c07e16c88ab6634f554cdf3d16bf543";

    // Official LLVM release used for coverage, sanitizers and clang-format.
    public const string LLVM_VERSION = "20.1.8";

    public const string LLVM_INSTALLER_SHA256 = "3197846a2b19063687dd56e93e34cd941e3548d907f23a6131571321bdf9fe7b";

    #endregion

    #region Functions

    /// <summary>
    /// Directory of the pinned QEMU package.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Directory path.</returns>
    public static string QemuDirectory(string root) => Path.Combine(root, ".tools", $"qemu-{QEMU_VERSION}");

    /// <summary>
    /// Path of the pinned QEMU x86_64 system emulator.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Executable path.</returns>
    public static string Qemu(string root) => Path.Combine(QemuDirectory(root), "qemu-system-x86_64.exe");

    /// <summary>
    /// Path of the EDK II x86_64 firmware code image.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Firmware path.</returns>
    public static string Firmware(string root) => Path.Combine(QemuDirectory(root), "share", "edk2-x86_64-code.fd");

    /// <summary>
    /// Path of the EDK II firmware variables template.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Template path.</returns>
    public static string FirmwareVariables(string root) => Path.Combine(QemuDirectory(root), "share", "edk2-i386-vars.fd");

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
        var installer = await RequireLlvmInstallerAsync(root);
        var directory = Path.Combine(root, ".tools", $"clang-format-{LLVM_VERSION}");
        await Processes.RequireSuccessAsync(SevenZip(),
            ["e", installer, @"bin\clang-format.exe", $"-o{directory}", "-y", "-bso0", "-bsp0"], root);
        var formatter = Path.Combine(directory, "clang-format.exe");
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
    public static string Clang(string root) => Path.Combine(ClangDirectory(root), "bin", "clang.exe");

    /// <summary>
    /// Path of the pinned ELF linker.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Executable path.</returns>
    public static string Lld(string root) => Path.Combine(ClangDirectory(root), "bin", "ld.lld.exe");

    /// <summary>
    /// Requires the pinned clang and lld that <c>setup</c> extracts.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <exception cref="InvalidOperationException">The tools are missing.</exception>
    public static void RequireClang(string root)
    {
        if (!File.Exists(Clang(root)) || !File.Exists(Lld(root)) ||
            !Directory.Exists(Path.Combine(ClangDirectory(root), "lib", "clang", LLVM_VERSION.Split('.')[0], "include")))
            throw new InvalidOperationException($"Pinned clang {LLVM_VERSION} is missing. Run: dotnet run --project tools/WitOS.Dev -- setup");
    }

    /// <summary>
    /// Extracts the pinned clang, lld, llvm-objcopy, llvm-readobj and the compiler's own headers (stdint.h and the
    /// like, which freestanding layer 2 code includes) from the verified LLVM installer; the installer is not executed.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>clang path.</returns>
    public static async Task<string> PrepareClangAsync(string root)
    {
        var installer = await RequireLlvmInstallerAsync(root);
        var directory = ClangDirectory(root);
        var major = LLVM_VERSION.Split('.')[0];
        var extraction = await Processes.RunAsync(SevenZip(),
        [
            "x", installer, @"bin\clang.exe", @"bin\ld.lld.exe", @"bin\llvm-objcopy.exe", @"bin\llvm-readobj.exe",
            $@"lib\clang\{major}\include", $"-o{directory}", "-y", "-bso0", "-bsp0"
        ], root, 180);
        if (extraction.TimedOut || extraction.ExitCode != 0)
            throw new InvalidOperationException($"clang extraction failed. {extraction.Error}");
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
        Console.WriteLine($"Extracting clang {LLVM_VERSION} and lld for layer 2 from the verified LLVM installer...");
        Console.WriteLine($"Ready: {await PrepareClangAsync(root)}");
    }

    #endregion
}
