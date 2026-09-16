using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

namespace WitOS.Dev;

internal static class DevTool
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length > 1)
                throw new ArgumentException("Use a single command: doctor, setup, build, run, test.");
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("The M0 development host is Windows x64 with Visual Studio C++ tools. The guest does not use Windows.");

            var root = FindRoot();
            var command = args.Length == 0 ? "help" : args[0];
            switch (command)
            {
                case "doctor":
                    Console.WriteLine($"Root: {root}");
                    Console.WriteLine($"MSVC: {await Toolchain.FindMsvcAsync(root)}");
                    Toolchain.RequireQemu(root);
                    var version = await Processes.RunAsync(Toolchain.Qemu(root), ["--version"], root);
                    if (version.ExitCode != 0 || version.TimedOut)
                        throw new InvalidOperationException($"QEMU could not start. {version.Error}");
                    Console.WriteLine(version.Output.Trim());
                    Console.WriteLine($"Firmware: {Toolchain.Firmware(root)}");
                    break;
                case "setup":
                    await Toolchain.SetupAsync(root);
                    break;
                case "build":
                    await BuildAsync(root, "boot");
                    break;
                case "run":
                    var image = await BuildAsync(root, "boot");
                    await BootAsync(root, image, "boot-256", 256, 60, ExpectedOutcome.Success);
                    break;
                case "test":
                    await TestAsync(root);
                    break;
                case "help":
                    Console.WriteLine("WitOS development tool\nUsage: dotnet run --project tools/WitOS.Dev -- <command>\n\n  doctor  Check compiler, QEMU and firmware\n  setup   Download and verify pinned QEMU into .tools\n  build   Build the x64 UEFI image (no VM)\n  run     Build and boot headlessly in QEMU\n  test    Test boot at two RAM sizes, panic and timeout handling");
                    break;
                default:
                    throw new ArgumentException($"Unknown command: {command}. Use help.");
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"ERROR: {error.Message}");
            return 1;
        }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WitOS.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Run this command inside the WitOS repository.");
    }

    private static async Task<string> BuildAsync(string root, string scenario)
    {
        var msvc = await Toolchain.FindMsvcAsync(root);
        var output = Path.Combine(root, "artifacts", "m0", scenario);
        Directory.CreateDirectory(output);
        var buildId = await BuildIdAsync(root);
        await File.WriteAllTextAsync(Path.Combine(output, "build_info.h"), $"#define WITOS_BUILD_ID \"{buildId}\"\n", Encoding.ASCII);

        string[] sources = ["src/Boot.Uefi/entry.c", "src/Kernel/kernel.c", "src/Kernel.Arch.X64/platform.c"];
        var objects = new List<string>();
        foreach (var source in sources)
        {
            var obj = Path.Combine(output, Path.GetFileNameWithoutExtension(source) + ".obj");
            objects.Add(obj);
            var arguments = new List<string>
            {
                "/nologo", "/c", "/TC", "/std:c17", "/W4", "/WX", "/GS-", "/Zl", "/Oi", "/Od", "/Zi",
                $"/I{Path.Combine(root, "src", "Kernel", "include")}", $"/I{output}",
                $"/Fo{obj}", $"/Fd{Path.Combine(output, "compiler.pdb")}"
            };
            if (scenario == "invalid-boot-info") arguments.Add("/DWITOS_TEST_INVALID_BOOTINFO=1");
            if (scenario == "timeout") arguments.Add("/DWITOS_TEST_HANG=1");
            arguments.Add(Path.Combine(root, source));
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), arguments, root);
        }

        var efi = Path.Combine(output, "BOOTX64.EFI");
        var linkArgs = new List<string>
        {
            "/nologo", "/subsystem:efi_application", "/entry:efi_main", "/nodefaultlib", "/machine:x64",
            "/fixed:no", "/dynamicbase:no", "/incremental:no", "/debug:full", "/Brepro",
            $"/out:{efi}", $"/pdb:{Path.Combine(output, "WitOS.pdb")}", $"/map:{Path.Combine(output, "WitOS.map")}"
        };
        linkArgs.AddRange(objects);
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), linkArgs, root);

        using (var file = File.OpenRead(efi))
        using (var pe = new PEReader(file))
        {
            var header = pe.PEHeaders.PEHeader;
            if (header is null || (int)header.Subsystem != 10 ||
                pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || header.ImportTableDirectory.Size != 0)
                throw new InvalidOperationException("Output must be an x64 EFI image with no imported OS/CRT functions.");
        }

        var disk = Path.Combine(output, "WitOS-x64.img");
        FatImage.Create(disk, await File.ReadAllBytesAsync(efi));
        await File.WriteAllTextAsync(Path.Combine(output, "build.txt"),
            $"Build: {buildId}\nScenario: {scenario}\nCompiler: {msvc}\nQEMU: {Toolchain.QemuVersion}\n");
        Console.WriteLine($"Built {scenario}: {disk}");
        return disk;
    }

    private static async Task<string> BuildIdAsync(string root)
    {
        // Trust only this explicitly selected workspace; do not change global Git settings.
        string[] prefix = ["-c", $"safe.directory={root.Replace('\\', '/')}"];
        var commit = await Processes.RunAsync("git", [.. prefix, "rev-parse", "--short=12", "HEAD"], root);
        var id = commit.Output.Trim();
        if (commit.ExitCode != 0 || !Regex.IsMatch(id, "^[0-9a-f]{7,40}$"))
            return "uncommitted";
        var state = await Processes.RunAsync("git", [.. prefix, "status", "--porcelain"], root);
        if (state.ExitCode != 0 || state.TimedOut)
            throw new InvalidOperationException("Could not determine Git worktree state for build identification.");
        return id + (state.Output.Length == 0 ? "" : "-dirty");
    }

    private static async Task TestAsync(string root)
    {
        Toolchain.RequireQemu(root);
        var image = await BuildAsync(root, "boot");
        await BootAsync(root, image, "boot-128", 128, 60, ExpectedOutcome.Success);
        await BootAsync(root, image, "boot-512", 512, 60, ExpectedOutcome.Success);
        var panic = await BuildAsync(root, "invalid-boot-info");
        await BootAsync(root, panic, "invalid-boot-info", 256, 60, ExpectedOutcome.Panic);
        var timeout = await BuildAsync(root, "timeout");
        await BootAsync(root, timeout, "timeout", 256, 15, ExpectedOutcome.Timeout);
        Console.WriteLine("PASS: all 4 M0 integration scenarios.");
    }

    private enum ExpectedOutcome { Success, Panic, Timeout }

    private static async Task BootAsync(string root, string image, string name, int memoryMiB, int timeoutSeconds, ExpectedOutcome expected)
    {
        Toolchain.RequireQemu(root);
        var firmwareState = Path.Combine(Path.GetDirectoryName(image)!, name + ".vars.fd");
        File.Copy(Toolchain.FirmwareVariables(root), firmwareState, overwrite: true);
        var arguments = new[]
        {
            "-machine", "q35", "-accel", "tcg,thread=single", "-cpu", "qemu64", "-smp", "1", "-m", memoryMiB.ToString(),
            "-display", "none", "-monitor", "none", "-serial", "stdio", "-nic", "none", "-no-reboot",
            "-drive", $"if=pflash,unit=0,format=raw,readonly=on,file={QemuPath(Toolchain.Firmware(root))}",
            "-drive", $"if=pflash,unit=1,format=raw,file={QemuPath(firmwareState)}",
            "-drive", $"if=none,id=boot,format=raw,readonly=on,file={QemuPath(image)}",
            "-device", "virtio-blk-pci,drive=boot,bootindex=1",
            "-device", "isa-debug-exit,iobase=0xf4,iosize=0x04"
        };
        Console.WriteLine($"Booting {name} ({memoryMiB} MiB, TCG, no networking)...");
        var result = await Processes.RunAsync(Toolchain.Qemu(root), arguments, root, timeoutSeconds);
        var logs = Path.Combine(root, "artifacts", "logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, name + ".serial.log"), result.Output);
        await File.WriteAllTextAsync(Path.Combine(logs, name + ".stderr.log"), result.Error);
        await File.WriteAllTextAsync(Path.Combine(logs, name + ".result.txt"),
            $"ExitCode={result.ExitCode}\nTimedOut={result.TimedOut}\nExpected={expected}\n");
        Console.Write(result.Output);

        var exitedFirmware = result.Output.IndexOf("[BOOT] ExitBootServices OK", StringComparison.Ordinal);
        var contract = result.Output.IndexOf("[TEST-PASS] Boot.Contract", StringComparison.Ordinal);
        var hello = result.Output.IndexOf("[TEST-PASS] Boot.Hello", StringComparison.Ordinal);
        var panic = result.Output.Contains("[PANIC]", StringComparison.Ordinal);
        var memory = Regex.Match(result.Output, @"Usable memory: (\d+) MiB");
        var validMemory = memory.Success && int.TryParse(memory.Groups[1].Value, out var usable) && usable > 0 && usable < memoryMiB;
        var booted = exitedFirmware >= 0 && contract > exitedFirmware && hello > contract && validMemory && !panic;
        var passed = expected switch
        {
            ExpectedOutcome.Success => !result.TimedOut && result.ExitCode == 33 && booted,
            ExpectedOutcome.Panic => !result.TimedOut && result.ExitCode == 35 && exitedFirmware >= 0 &&
                result.Output.Contains("[PANIC] Invalid WitBootInfo", StringComparison.Ordinal) && contract < 0 && hello < 0,
            ExpectedOutcome.Timeout => result.TimedOut && booted,
            _ => false
        };
        if (!passed)
            throw new InvalidOperationException($"{name}: expected {expected}, got exit={result.ExitCode}, timeout={result.TimedOut}. Logs: {logs}\n{result.Error}");
        Console.WriteLine($"PASS: {name} (exit={result.ExitCode}, timeout={result.TimedOut}).");
    }

    // Escape QEMU's comma-separated key/value syntax independently of shell quoting.
    private static string QemuPath(string path) => path.Replace('\\', '/').Replace(",", ",,");
}
