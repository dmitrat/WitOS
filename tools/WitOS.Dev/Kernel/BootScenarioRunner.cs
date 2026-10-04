using WitOS.Dev.Host;

namespace WitOS.Dev.Kernel;

/// <summary>
/// Boots a built image headlessly in the pinned QEMU and requires the requested outcome.
/// </summary>
internal static class BootScenarioRunner
{
    #region Functions

    /// <summary>
    /// Boots <paramref name="image"/> once and throws unless the boot produced the requested outcome.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="image">FAT disk image built by <see cref="KernelImageBuilder"/>.</param>
    /// <param name="request">Machine profile and required outcome.</param>
    /// <exception cref="InvalidOperationException">The boot did not produce the requested outcome.</exception>
    public static async Task RunAsync(string root, string image, BootRequest request)
    {
        var architecture = request.Architecture;
        architecture.RequireQemu(root);
        var cpu = request.CpuModel ?? architecture.DefaultCpu;
        var name = request.Name;
        var logs = request.LogDirectory ?? Path.Combine(root, "artifacts", "logs");
        Directory.CreateDirectory(logs);
        var serialPath = Path.Combine(logs, name + ".serial.log");
        if (File.Exists(serialPath))
        {
            File.Delete(serialPath);
        }
        var firmwareState = Path.Combine(Path.GetDirectoryName(image)!, name + ".vars.fd");
        File.Copy(architecture.FirmwareVariablesPath(root), firmwareState, overwrite: true);
        using var qmpControl = new QemuControl();
        // The HPET-less q35 is the one board variant a scenario selects.
        var machine = request.Expected == ExpectedOutcome.ClockUnavailable ? "q35,hpet=off" : architecture.QemuMachine;
        var arguments = new List<string>
        {
            "-machine", machine, "-accel", "tcg,thread=single", "-cpu", cpu, "-smp", "1",
            "-m", request.MemoryMiB.ToString(), "-display", "none", "-monitor", "none", "-qmp", qmpControl.Argument,
            "-serial", "file:" + QemuPath(serialPath), "-nic", "none", "-no-reboot",
            "-drive", $"if=pflash,unit=0,format=raw,readonly=on,file={QemuPath(architecture.FirmwarePath(root))}",
            "-drive", $"if=pflash,unit=1,format=raw,file={QemuPath(firmwareState)}",
            "-drive", $"if=none,id=boot,format=raw,readonly=on,file={QemuPath(image)}",
            "-device", "virtio-blk-pci,drive=boot,bootindex=1"
        };
        arguments.AddRange(architecture.ExitDevice);
        if (request.Expected != ExpectedOutcome.EntropyUnavailable)
        {
            arguments.AddRange(["-object", "rng-builtin,id=entropy0", "-device", "virtio-rng-pci,rng=entropy0"]);
        }
        Console.WriteLine($"Booting {name} ({request.MemoryMiB} MiB, {cpu}, TCG, no networking)...");
        ProcessResult monitorResult;
        try
        {
            monitorResult = await Processes.RunWithFilesAsync(architecture.QemuPath(root), arguments, root, request.TimeoutSeconds,
                Path.Combine(logs, name + ".stdout.log"), Path.Combine(logs, name + ".stderr.log"), qmpControl.QuitAsync);
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(logs, name + ".monitor.log"), qmpControl.Transcript);
        }
        var serial = File.Exists(serialPath) ? await BoundedCapture.ReadFileAsync(serialPath) : "";
        var result = monitorResult with { Output = serial };
        await File.WriteAllTextAsync(Path.Combine(logs, name + ".stderr.log"), result.Error);
        await File.WriteAllTextAsync(Path.Combine(logs, name + ".result.txt"),
            $"ExitCode={result.ExitCode}\nTimedOut={result.TimedOut}\nExpected={request.Expected}\n");
        Console.Write(result.Output);

        var verdict = BootValidation.Evaluate(root, request, result);
        if (!verdict.Passed)
        {
            throw new InvalidOperationException($"{name}: expected {request.Expected}, got exit={result.ExitCode}, " +
                $"timeout={result.TimedOut}. Checks: {verdict.Diagnostics}. Logs: {logs}\n{result.Error}");
        }
        Console.WriteLine($"PASS: {name} (exit={result.ExitCode}, timeout={result.TimedOut}).");
    }

    #endregion

    #region Tools

    private static string QemuPath(string path) => path.Replace('\\', '/').Replace(",", ",,");

    #endregion
}
