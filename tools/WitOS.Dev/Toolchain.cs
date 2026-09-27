using System.Security.Cryptography;

namespace WitOS.Dev;

internal static class Toolchain
{
    public const string QemuVersion = "11.1.0";
    private const string QemuInstaller = "qemu-w64-setup-20260811.exe";
    private const string QemuSha512 = "5bcf9eed634e8575a37b74f445af41a2fe4106da512d0c30c368301d4c105037fdfab40a5287367a28a957624cddebbc8c07e16c88ab6634f554cdf3d16bf543";

    public static string QemuDirectory(string root) => Path.Combine(root, ".tools", $"qemu-{QemuVersion}");
    public static string Qemu(string root) => Path.Combine(QemuDirectory(root), "qemu-system-x86_64.exe");
    public static string Firmware(string root) => Path.Combine(QemuDirectory(root), "share", "edk2-x86_64-code.fd");

    public static string FirmwareVariables(string root) => Path.Combine(QemuDirectory(root), "share", "edk2-i386-vars.fd");

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

    public static async Task<string> FindMsvcAsync(string root)
    {
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere))
            throw new InvalidOperationException("Install Visual Studio Build Tools with Desktop development with C++ (x64). vswhere.exe was not found.");
        var result = await Processes.RunAsync(vswhere,
            ["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"], root);
        var installation = result.Output.Trim();
        if (result.ExitCode != 0 || result.TimedOut || installation.Length == 0)
            throw new InvalidOperationException("Visual Studio x64 C++ tools were not found.");
        var versionsRoot = Path.Combine(installation, "VC", "Tools", "MSVC");
        var candidate = Directory.GetDirectories(versionsRoot)
            .Where(path => Version.TryParse(Path.GetFileName(path), out _))
            .OrderByDescending(path => Version.Parse(Path.GetFileName(path)))
            .Select(path => Path.Combine(path, "bin", "Hostx64", "x64"))
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "cl.exe")) && File.Exists(Path.Combine(path, "link.exe")) && File.Exists(Path.Combine(path, "ml64.exe")));
        return candidate ?? throw new InvalidOperationException("MSVC x64 compiler/linker/MASM were not found.");
    }

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

    public static void RequireQemu(string root)
    {
        if (!File.Exists(Qemu(root)) || !File.Exists(Firmware(root)) || !File.Exists(FirmwareVariables(root)))
            throw new InvalidOperationException("QEMU/EDK II are missing. Run: dotnet run --project tools/WitOS.Dev -- setup");
    }

    public static async Task SetupAsync(string root)
    {
        var sevenZip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe");
        if (!File.Exists(sevenZip))
            throw new InvalidOperationException("Install 7-Zip from https://www.7-zip.org/ before setup.");

        var downloads = Path.Combine(root, ".tools", "downloads");
        Directory.CreateDirectory(downloads);
        var installer = Path.Combine(downloads, QemuInstaller);
        if (!File.Exists(installer))
        {
            Console.WriteLine($"Downloading pinned QEMU {QemuVersion} (about 197 MiB)...");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            using var response = await client.GetAsync($"https://qemu.weilnetz.de/w64/{QemuInstaller}", HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var partial = installer + ".partial";
            await using (var file = File.Create(partial))
                await response.Content.CopyToAsync(file);
            File.Move(partial, installer, overwrite: true);
        }
        await using (var file = File.OpenRead(installer))
        {
            var hash = Convert.ToHexString(await SHA512.HashDataAsync(file));
            if (!hash.Equals(QemuSha512, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"QEMU SHA-512 mismatch. Remove the invalid download and run setup again: {installer}");
        }
        Console.WriteLine("SHA-512 verified. Extracting QEMU locally; the installer is not executed.");
        var extraction = await Processes.RunAsync(sevenZip,
            ["x", installer, $"-o{QemuDirectory(root)}", "-y", "-bso0", "-bsp0"], root, 180);
        if (extraction.TimedOut || extraction.ExitCode != 0)
            throw new InvalidOperationException($"QEMU extraction failed. {extraction.Error}");
        RequireQemu(root);
        Console.WriteLine($"Ready: {Qemu(root)}");
    }
}
