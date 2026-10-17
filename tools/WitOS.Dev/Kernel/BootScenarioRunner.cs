using WitOS.Dev.Host;

namespace WitOS.Dev.Kernel;

/// <summary>
/// Boots a built image headlessly in the pinned QEMU and requires the requested outcome.
/// </summary>
internal static class BootScenarioRunner
{
    #region Constants

    // Guest time follows executed instructions, not the host clock: 4 ns per instruction, so a 10 ms timer tick is
    // 2.5 million instructions on every host and under any load. An idle guest skips ahead to its next timer instead of
    // sleeping. Wall-clock limits of a boot only stop QEMU; they never change what the guest measures.
    private const string VIRTUAL_TIME = "shift=2,sleep=off";

    #endregion

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
        var terminalPath = Path.Combine(logs, name + ".terminal.log");
        using var terminal = request.Terminal is { } script ? new TerminalLink(script, terminalPath) : null;
        using var relay = request.InteractiveTerminal && terminal is null ? new TerminalRelay(terminalPath) : null;
        // The HPET-less q35 is the one board variant a scenario selects.
        var machine = request.Expected == ExpectedOutcome.ClockUnavailable ? "q35,hpet=off" : architecture.QemuMachine;
        var arguments = new List<string>
        {
            "-machine", machine, "-accel", "tcg,thread=single", "-icount", VIRTUAL_TIME, "-cpu", cpu, "-smp", request.Processors.ToString(),
            "-m", request.MemoryMiB.ToString(), "-display", "none", "-monitor", "none", "-qmp", qmpControl.Argument,
            "-serial", "file:" + QemuPath(serialPath), "-nic", "none", "-no-reboot",
            "-drive", $"if=pflash,unit=0,format=raw,readonly=on,file={QemuPath(architecture.FirmwarePath(root))}",
            "-drive", $"if=pflash,unit=1,format=raw,file={QemuPath(firmwareState)}",
            "-drive", $"if=none,id=boot,format=raw,readonly=on,file={QemuPath(image)}",
            "-device", "virtio-blk-pci,drive=boot,bootindex=1"
        };
        arguments.AddRange(architecture.ExitDevice);
        if (request.Display)
        {
            arguments.AddRange(architecture.DisplayArguments);
        }
        if (request.Keyboard)
        {
            // Slot 6 (C2.2): the board's four INTx lines rotate by slot, the kernel binds a line once, and the boot disk,
            // the data disk and the console take three of them; the fifth virtio device shares a line with the entropy
            // source (slot 2), which nothing binds once the firmware reset it, instead of with a device a driver holds.
            arguments.AddRange(["-device", "virtio-keyboard-pci,addr=0x6"]);
        }
        if (request.Expected != ExpectedOutcome.EntropyUnavailable)
        {
            arguments.AddRange(["-object", "rng-builtin,id=entropy0", "-device", "virtio-rng-pci,rng=entropy0"]);
        }
        if (request.DataDisk is not null && request.DataDiskReadErrors.Length > 0)
        {
            // A failing medium (D6.3): blkdebug between the raw format and the image fails the reads of the sectors, and the
            // device reports each failure to the guest instead of stopping the board.
            var rules = Path.Combine(logs, name + ".blkdebug.conf");
            await File.WriteAllTextAsync(rules, string.Concat(request.DataDiskReadErrors.Select(sector =>
                $"[inject-error]\nevent = \"read_aio\"\niotype = \"read\"\nerrno = \"5\"\nsector = \"{sector}\"\n\n")));
            arguments.AddRange(["-drive", $"if=none,id=data,format=raw,rerror=report,werror=report,file.driver=blkdebug," +
                $"file.config={QemuPath(rules)},file.image.filename={QemuPath(request.DataDisk)}", "-device", "virtio-blk-pci,drive=data"]);
        }
        else if (request.DataDisk is not null)
        {
            arguments.AddRange(["-drive", $"if=none,id=data,format=raw,file={QemuPath(request.DataDisk)}", "-device", "virtio-blk-pci,drive=data"]);
        }
        if (terminal is not null || relay is not null)
        {
            // The terminal (N4.3a): a virtio console with one port, whose character device connects to the runner — to
            // its script, or to the host's console (C1.4).
            arguments.AddRange(["-chardev", terminal?.Argument ?? relay!.Argument, "-device", "virtio-serial-pci,id=serial0,max_ports=1",
                "-device", "virtconsole,chardev=term0"]);
        }
        Console.WriteLine($"Booting {name} ({request.MemoryMiB} MiB, {cpu}, {request.Processors} processor(s), TCG, no networking)...");
        ProcessResult monitorResult;
        using var cut = new CancellationTokenSource();
        using var watching = new CancellationTokenSource();
        var jitter = request.CutJitterMilliseconds > 0 ? Random.Shared.Next(request.CutJitterMilliseconds + 1) : 0;
        var watcher = request.CutAfter is not null || request.Actions.Count > 0
            ? WatchAsync(serialPath, terminal, request, jitter, qmpControl, ScreenPath(logs, name), cut, watching.Token)
            : Task.FromResult<(BootAction Action, string Error)?>(null);
        try
        {
            monitorResult = await Processes.RunWithFilesAsync(architecture.QemuPath(root), arguments, root, request.TimeoutSeconds,
                Path.Combine(logs, name + ".stdout.log"), Path.Combine(logs, name + ".stderr.log"), qmpControl.QuitAsync, cut.Token);
        }
        finally
        {
            watching.Cancel();
            var failed = await watcher;
            await File.WriteAllTextAsync(Path.Combine(logs, name + ".monitor.log"), qmpControl.Transcript);
            if (failed is not null)
                Console.WriteLine($"The runner's action after \"{failed.Value.Action.AfterLine}\" failed: {failed.Value.Error}");
        }
        var serial = File.Exists(serialPath) ? await BoundedCapture.ReadFileAsync(serialPath) : "";
        var result = monitorResult with { Output = serial };
        var transcript = terminal?.Transcript ?? "";
        await File.WriteAllTextAsync(Path.Combine(logs, name + ".stderr.log"), result.Error);
        await File.WriteAllTextAsync(Path.Combine(logs, name + ".result.txt"),
            $"ExitCode={result.ExitCode}\nTimedOut={result.TimedOut}\nExpected={request.Expected}\n");
        Console.Write(result.Output);
        if (terminal is not null)
        {
            await File.WriteAllTextAsync(terminalPath, transcript);
            Console.Write($"=== terminal ({terminal.Typed} of {request.Terminal!.Count} inputs typed) ===\n{transcript}\n");
        }

        var verdict = BootValidation.Evaluate(root, request, result, transcript);
        if (!verdict.Passed)
        {
            throw new InvalidOperationException($"{name}: expected {request.Expected}, got exit={result.ExitCode}, " +
                $"timeout={result.TimedOut}. Checks: {verdict.Diagnostics}. Logs: {logs}\n{result.Error}");
        }
        if (request.CutAfter is not null)
            Console.WriteLine($"Cut {name} {jitter} ms after \"{request.CutAfter}\".");
        Console.WriteLine($"PASS: {name} (exit={result.ExitCode}, timeout={result.TimedOut}).");
    }

    /// <summary>
    /// The terminal's transcript as it was when a screendump was taken (plan step C2.2), beside the screen's file.
    /// </summary>
    /// <param name="screen">The screen's file.</param>
    /// <returns>The transcript's file.</returns>
    public static string TranscriptPath(string screen) => screen + ".transcript.txt";

    /// <summary>
    /// The file a boot's screendump action writes (plan step C2.1).
    /// </summary>
    /// <param name="logs">The boot's log directory.</param>
    /// <param name="name">The boot's name.</param>
    /// <returns>The PPM file's path.</returns>
    public static string ScreenPath(string logs, string name) => Path.Combine(logs, name + ".screen.ppm");

    #endregion

    #region Tools

    private static string QemuPath(string path) => path.Replace('\\', '/').Replace(",", ",,");

    // Watches the serial log, and the terminal's transcript when there is one, while the guest runs: performs each action
    // of the request once its line is there (C2.1, C2.2) — a screendump keeps the transcript of that moment beside it, for
    // the host to render the frame it expects — and cuts the boot off once the log holds the cut line and the jitter's
    // milliseconds passed (D6.2): the guest runs on meanwhile, and the cut falls where that moment ends. The first action
    // that failed, with its error, or null.
    private static async Task<(BootAction Action, string Error)?> WatchAsync(string serialPath, TerminalLink? terminal, BootRequest request,
        int jitter, QemuControl control, string screen, CancellationTokenSource cut, CancellationToken stop)
    {
        var next = 0;
        var cutDone = request.CutAfter is null;
        try
        {
            while (!stop.IsCancellationRequested && (!cutDone || next < request.Actions.Count))
            {
                var log = "";
                if (File.Exists(serialPath))
                {
                    using var stream = new FileStream(serialPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    log = await reader.ReadToEndAsync(stop);
                }
                var transcript = terminal?.Transcript ?? "";
                var seen = log + "\n" + transcript;
                if (log.Length > 0 || transcript.Length > 0)
                {
                    while (next < request.Actions.Count && seen.Contains(request.Actions[next].AfterLine, StringComparison.Ordinal))
                    {
                        var action = request.Actions[next++];
                        try
                        {
                            if (action.Kind == BootActionKind.Screendump)
                            {
                                await control.ScreendumpAsync(screen, stop);
                                await File.WriteAllTextAsync(TranscriptPath(screen), transcript, stop);
                            }
                            else
                                await control.SendKeyAsync(action.Argument, stop);
                        }
                        catch (Exception exception) when (exception is IOException or InvalidDataException)
                        {
                            return (action, exception.Message);
                        }
                    }
                    if (!cutDone && log.Contains(request.CutAfter!, StringComparison.Ordinal))
                    {
                        cutDone = true;
                        await Task.Delay(jitter, stop);
                        await cut.CancelAsync();
                    }
                }
                await Task.Delay(20, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        return null;
    }

    #endregion
}
