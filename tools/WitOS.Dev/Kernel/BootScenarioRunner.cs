using WitOS.Dev.Host;

namespace WitOS.Dev.Kernel;

/// <summary>
/// Boots a built image headlessly in the pinned QEMU and requires the requested outcome.
/// </summary>
internal static class BootScenarioRunner
{
    #region Constants

    // Guest time follows executed instructions, not the host clock (InstructionClock): a 10 ms timer tick is 2.5 million
    // instructions of each processor on every host and under any load. An idle guest skips ahead to its next timer
    // instead of sleeping. Wall-clock limits of a boot only stop QEMU; they never change what the guest measures.
    private const string VIRTUAL_TIME = "sleep=off";

    // A session a person works in (C1.4, C2.4) keeps the instruction clock but sleeps while the guest idles, so that its
    // time follows the host's between keys instead of racing ahead, and the host's processor rests.
    private const string INTERACTIVE_TIME = "sleep=on";

    // The window of a session on the host's screen (C2.4): QEMU's GTK display, the framebuffer scaled to the window.
    private const string WINDOW_DISPLAY = "gtk,zoom-to-fit=on";

    // A screendump waits until the guest wrote nothing for this long, looking every step, up to the limit (C2.3).
    private const int QUIET_MILLISECONDS = 1000;
    private const int QUIET_STEP_MILLISECONDS = 100;
    private const int QUIET_LIMIT_MILLISECONDS = 15000;

    // The guest's keyboard driver reports every key transition to the log with this prefix (C2.1); the runner types a
    // key once the previous one's transitions are there, and gives up on a key the guest has not reported in this long.
    internal const string KEY_REPORT = "[VIRTIO-INPUT] key ";
    private const int KEY_REPORT_MILLISECONDS = 60000;
    private const int KEY_STEP_MILLISECONDS = 20;

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
        // The runner's side of the guest's network (W1.2b): its echo server and the port it reaches the guest through.
        using var peer = request.Network && (request.NetworkEcho || request.NetworkForward != 0) ? new NetworkPeer(request.NetworkForward) : null;
        var terminalPath = Path.Combine(logs, name + ".terminal.log");
        // The mirror of a display answers no terminal query: the display does (C2.3).
        using var terminal = request.Terminal is { } script ? new TerminalLink(script, terminalPath, answersQueries: !request.DisplayIsMainConsole) : null;
        // A window's keys are the input (C2.4): the console then shows the mirror and types nothing.
        using var relay = request.InteractiveTerminal && terminal is null ? new TerminalRelay(terminalPath, keys: !request.Window) : null;
        // The HPET-less q35 is the one board variant a scenario selects.
        var machine = request.Expected == ExpectedOutcome.ClockUnavailable ? "q35,hpet=off" : architecture.QemuMachine;
        // Emulated with the instruction clock, or on the host's hardware virtualization for a session (C2.7).
        string[] execution = request.Accelerated
            ? ["-accel", HostAccelerator(architecture)]
            : ["-accel", "tcg,thread=single", "-icount", InstructionClock(request.Processors, request.InteractiveTerminal)];
        List<string> arguments =
        [
            "-machine", machine, .. execution,
            "-cpu", cpu, "-smp", request.Processors.ToString(), "-m", request.MemoryMiB.ToString(),
            "-display", request.Window ? WINDOW_DISPLAY : "none", "-monitor", "none", "-qmp", qmpControl.Argument,
            "-serial", "file:" + QemuPath(serialPath), "-nic", "none", "-no-reboot",
            "-drive", $"if=pflash,unit=0,format=raw,readonly=on,file={QemuPath(architecture.FirmwarePath(root))}",
            "-drive", $"if=pflash,unit=1,format=raw,file={QemuPath(firmwareState)}",
            "-drive", $"if=none,id=boot,format=raw,readonly=on,file={QemuPath(image)}",
            "-device", "virtio-blk-pci,drive=boot,bootindex=1"
        ];
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
        if (request.Network)
        {
            // The entropy source's line too (W1.1), for the same reason: a keyboard and a network adapter would need it
            // both, and the kernel binds a line once, so a board has one or the other until a line serves several bindings.
            if (request.Keyboard)
                throw new InvalidOperationException($"{name}: a keyboard and a network adapter would share an INTx line, which the kernel binds once.");
            arguments.AddRange(["-netdev", "user,id=net0" + (peer?.NetdevOptions ?? ""), "-device",
                $"virtio-net-pci,netdev=net0,addr=0x{architecture.EntropyLineSlot(request.Display):x}"]);
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
        // The accelerator a session runs on (C2.7), WHPX or KVM, or TCG with the instruction clock.
        var accelerator = request.Accelerated ? execution[1].ToUpperInvariant() : "TCG";
        Console.WriteLine($"Booting {name} ({request.MemoryMiB} MiB, {cpu}, {request.Processors} processor(s), {accelerator}, " +
            $"{(request.Network ? "user networking" : "no networking")})...");
        ProcessResult monitorResult;
        using var cut = new CancellationTokenSource();
        using var watching = new CancellationTokenSource();
        var jitter = request.CutJitterMilliseconds > 0 ? Random.Shared.Next(request.CutJitterMilliseconds + 1) : 0;
        var watcher = request.CutAfter is not null || request.Actions.Count > 0
            ? WatchAsync(serialPath, terminal, request, jitter, qmpControl, peer, ScreenPath(logs, name), cut, watching.Token)
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
            if (peer is { Echoed: > 0 })
                Console.WriteLine($"The runner's echo server sent {peer.Echoed} bytes back to the guest.");
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

    /// <summary>
    /// The host's hardware virtualization for a guest of an architecture (plan step C2.7): WHPX on Windows, KVM on Linux,
    /// for a guest of the host's own ISA alone.
    /// </summary>
    /// <param name="architecture">The guest's architecture.</param>
    /// <returns>QEMU's accelerator.</returns>
    /// <exception cref="NotSupportedException">The host is another ISA, or has no accelerator QEMU knows.</exception>
    internal static string HostAccelerator(KernelArchitecture architecture)
    {
        var host = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => KernelArchitecture.X64,
            System.Runtime.InteropServices.Architecture.Arm64 => KernelArchitecture.Arm64,
            _ => null
        };
        if (host != architecture)
            throw new NotSupportedException($"A {architecture.Name} guest runs on hardware virtualization only on a {architecture.Name} host; this host is {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}.");
        if (OperatingSystem.IsWindows())
            return "whpx";
        if (OperatingSystem.IsLinux())
            return File.Exists("/dev/kvm") ? "kvm" : throw new NotSupportedException("This Linux host has no /dev/kvm for hardware virtualization.");
        throw new NotSupportedException("Hardware virtualization of a session is WHPX on Windows or KVM on Linux.");
    }

    // Watches the serial log, and the terminal's transcript when there is one, while the guest runs: performs each action
    // of the request once its line is there after the previous action's (C2.1-C2.3, ActionSequence) - a screendump waits
    // until the guest wrote nothing for a moment and keeps the transcript of that moment beside the screen, for the host
    // to render the frame it expects - and cuts the boot off once the log holds the cut line and the jitter's
    // milliseconds passed (D6.2): the guest runs on meanwhile, and the cut falls where that moment ends. The first action
    // that failed, with its error, or null.
    private static async Task<(BootAction Action, string Error)?> WatchAsync(string serialPath, TerminalLink? terminal, BootRequest request,
        int jitter, QemuControl control, NetworkPeer? peer, string screen, CancellationTokenSource cut, CancellationToken stop)
    {
        var actions = new ActionSequence(request.Actions);
        var cutDone = request.CutAfter is null;
        try
        {
            while (!stop.IsCancellationRequested && (!cutDone || !actions.Done))
            {
                var (log, transcript) = await ReadAsync(serialPath, terminal, stop);
                if (log.Length > 0 || transcript.Length > 0)
                {
                    while (actions.Next(log, transcript) is { } action)
                    {
                        try
                        {
                            if (action.Kind == BootActionKind.Connect)
                            {
                                await (peer ?? throw new InvalidDataException("a connection without a forward to the guest's port"))
                                    .ExchangeAsync(int.Parse(action.Argument, System.Globalization.CultureInfo.InvariantCulture), stop);
                            }
                            else if (action.Kind == BootActionKind.Screendump)
                            {
                                (log, transcript) = await QuietAsync(serialPath, terminal, log, transcript, stop);
                                await control.ScreendumpAsync(screen, stop);
                                await File.WriteAllTextAsync(TranscriptPath(screen), transcript, stop);
                            }
                            else if (request.Keyboard)
                            {
                                // Each key after the guest's driver reported the previous one's transitions, which it does once it
                                // gave their buffers back (C2.3): a report the device's queue has no room for is lost.
                                var reported = KeyReports((await ReadAsync(serialPath, terminal, stop)).Log);
                                await control.SendKeyAsync(action.Argument, stop,
                                    (transitions, token) => KeysReportedAsync(serialPath, reported += transitions, token));
                            }
                            else
                                await control.SendKeyAsync(action.Argument, stop);
                        }
                        catch (Exception exception) when (exception is IOException or InvalidDataException)
                        {
                            // The boot ends now: a lost key leaves the guest waiting until the boot's timeout.
                            await cut.CancelAsync();
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

    /// <summary>
    /// QEMU's instruction clock for a board (P2): its single-threaded TCG runs the processors in turn and counts every
    /// processor's instructions in one clock, so its shift falls with the processors — 4 ns an instruction on one, 2 on
    /// two, 1 on four or more — and a 10 ms tick is 2.5 million instructions of each processor while all of them run, as
    /// on one. With one shift for every board, four busy processors would see four times the time pass for the same work,
    /// and a program's timeouts would be four times as tight as on one processor or on hardware.
    /// </summary>
    /// <param name="processors">The board's processors.</param>
    /// <param name="interactive">Whether the guest sleeps while idle, a session a person works in (C1.4).</param>
    /// <returns>The -icount option's value.</returns>
    internal static string InstructionClock(int processors, bool interactive) =>
        $"shift={(processors >= 4 ? 0 : processors >= 2 ? 1 : 2)},{(interactive ? INTERACTIVE_TIME : VIRTUAL_TIME)}";

    /// <summary>
    /// The key transitions the guest's keyboard driver reported in a serial log (C2.3).
    /// </summary>
    /// <param name="log">The serial log.</param>
    /// <returns>The number of the driver's key lines.</returns>
    internal static int KeyReports(string log)
    {
        var count = 0;
        for (var at = log.IndexOf(KEY_REPORT, StringComparison.Ordinal); at >= 0; at = log.IndexOf(KEY_REPORT, at + KEY_REPORT.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    // Waits until the serial log holds the given number of key reports (C2.3); an IOException names a key the guest did
    // not report in KEY_REPORT_MILLISECONDS.
    private static async Task KeysReportedAsync(string serialPath, int expected, CancellationToken stop)
    {
        var reported = 0;
        for (var waited = 0; waited < KEY_REPORT_MILLISECONDS; waited += KEY_STEP_MILLISECONDS)
        {
            reported = KeyReports((await ReadAsync(serialPath, null, stop)).Log);
            if (reported >= expected)
                return;
            await Task.Delay(KEY_STEP_MILLISECONDS, stop);
        }
        throw new IOException($"the guest's keyboard driver reported {reported} of {expected} key transitions in {KEY_REPORT_MILLISECONDS / 1000} s: a key was lost");
    }

    // What the guest wrote so far: the serial log and the terminal's transcript.
    private static async Task<(string Log, string Transcript)> ReadAsync(string serialPath, TerminalLink? terminal, CancellationToken stop)
    {
        var log = "";
        if (File.Exists(serialPath))
        {
            using var stream = new FileStream(serialPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            log = await reader.ReadToEndAsync(stop);
        }
        return (log, terminal?.Transcript ?? "");
    }

    // Waits until the guest wrote nothing for QUIET_MILLISECONDS, up to the limit (C2.3): a screendump judged against the
    // transcript needs the screen the transcript describes, and a program that showed its prompt may still be writing
    // its last sequences. What the guest wrote by then.
    private static async Task<(string Log, string Transcript)> QuietAsync(string serialPath, TerminalLink? terminal, string log, string transcript,
        CancellationToken stop)
    {
        var waited = 0;
        var quiet = 0;
        while (quiet < QUIET_MILLISECONDS && waited < QUIET_LIMIT_MILLISECONDS)
        {
            await Task.Delay(QUIET_STEP_MILLISECONDS, stop);
            waited += QUIET_STEP_MILLISECONDS;
            var (nextLog, nextTranscript) = await ReadAsync(serialPath, terminal, stop);
            if (nextLog.Length == log.Length && nextTranscript.Length == transcript.Length)
                quiet += QUIET_STEP_MILLISECONDS;
            else
            {
                quiet = 0;
                (log, transcript) = (nextLog, nextTranscript);
            }
        }
        return (log, transcript);
    }

    #endregion
}
