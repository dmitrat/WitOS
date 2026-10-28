using WitOS.Apps;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;
using WitOS.Dev.Substrate;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The PowerShell scenario (plan step C1.3, RFC 0020 section 57): upstream PowerShell, the pinned framework-dependent
/// archive with libpsl-native built for the ISA, is a base application of the data volume's layer (ADR 0027, C4.1) under
/// /data/apps/PowerShell and runs through its launcher /data/apps/bin/pwsh beside the device services, the .NET install
/// and the terminal; /bin/init runs it
/// twice without a terminal session (the version table; a pipeline, files on the volume, the date and a listing) and
/// then interactively, where the session's profile renders the console mark (C1.4) and the runner types into PSReadLine
/// on its emulated terminal. The required lines are in the serial log or in the terminal's transcript.
/// </summary>
internal static class RuntimePowerShell
{
    #region Constants

    /// <summary>
    /// How /bin/init starts an interactive PowerShell: the layer's launcher (C4.1, AppsLayer.GuestCommand) without
    /// PowerShell's own banner, with the profile that renders WitOS's mark (C1.4).
    /// </summary>
    public const string PWSH = "/data/apps/bin/pwsh -NoLogo";

    /// <summary>
    /// The word before the shell's variable in the scenario's output: "shell=/data/apps/bin/pwsh" (ADR 0027 decision 6).
    /// </summary>
    public const string SHELL = "shell";

    /// <summary>
    /// How /bin/init runs one command: no profile, no session, the command's text after it.
    /// </summary>
    public const string BATCH = PWSH + " -NoProfile -NonInteractive -Command";

    /// <summary>
    /// The text the scenario writes to a file on the volume through Set-Content and reads back with Get-Content.
    /// </summary>
    public const string WRITTEN = "written-by-pwsh";

    /// <summary>
    /// The word before the pipeline's count in the scenario's output: "even=5".
    /// </summary>
    public const string EVEN = "even";

    /// <summary>
    /// The word before the date in the scenario's output: "date=2026-10-16", of which the century is required.
    /// </summary>
    public const string DATE = "date";

    /// <summary>
    /// The word before PowerShell's home directory in the scenario's output: "pshome=/data/apps/PowerShell/7.6.7/native-x64"
    /// where the layer carries the version's native code (R9.2).
    /// </summary>
    public const string PSHOME = "pshome";

    /// <summary>
    /// The word before the count of methods the JIT compiled for the batch command: "jitted=312", reported for R9.2.
    /// </summary>
    public const string JITTED = "jitted";

    /// <summary>
    /// The prompt PowerShell shows in the data volume's root, which the runner types after.
    /// </summary>
    public const string PROMPT = "PS /data>";

    /// <summary>
    /// The first row of the emblem as the profile draws it in colour (@Docs/WitOS-Console-Logos.md section 3.2): every tier
    /// one shade, the layers told apart by colour, which the transcript keeps between the tiers' escape sequences.
    /// </summary>
    public const string EMBLEM_ROW = "████          ████          ████";

    /// <summary>
    /// The guest's memory: PowerShell's engine and its modules through the JIT need more than the suites' 512 MiB.
    /// </summary>
    public const int MEMORY_MIB = 1024;

    /// <summary>
    /// The data disk's size: PowerShell's files and the home directory on the volume.
    /// </summary>
    public const long DISK_BYTES = 1536L << 20; // the .NET SDK takes 400 MiB of the layer (C2.5); the disk is written from one array, so below 2 GiB

    private const int TIMEOUT_SECONDS = 3600;

    #endregion

    #region Functions

    /// <summary>
    /// Boots the scenario on the services and the install earlier runs built.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>When the boot passed.</returns>
    public static async Task RunAsync(string root, KernelArchitecture architecture)
    {
        var scenario = KernelImageBuilder.RUNTIME_POWERSHELL_SCENARIO;
        var output = Path.Combine(root, "artifacts", architecture.Name, scenario);
        Directory.CreateDirectory(output);
        var pin = await PowerShell.ReadPinAsync(root);
        var package = await PackageAsync(root, output, architecture, Commands());
        var volume = await AppsDisk.VolumeFilesAsync(root, architecture, output);
        foreach (var log in new[] { "serial", "terminal" })
        {
            var path = Path.Combine(root, "artifacts", "logs", $"{architecture.Name}-{scenario}-{MEMORY_MIB}.{log}.log");
            if (File.Exists(path))
                File.Delete(path);
        }
        // Four processors (S8.1): pwsh's exit writes a file in many small writes, which on several processors never ended
        // while the libc held its tables through each round trip with the namespace service; one processor hid it.
        await RuntimeApps.BootAsync(root, architecture, scenario, RequiredLines(architecture, pin.Version), TIMEOUT_SECONDS, volume, DISK_BYTES, MEMORY_MIB,
            package, Script(), RuntimeProcessors.PROCESSORS);
        Console.WriteLine($"PowerShell on witos-{architecture.Name}: {pin.Version} ran unchanged from the data volume's layer through its launcher, " +
            "non-interactively and under PSReadLine on the terminal, with the console mark of its profile.");
    }

    /// <summary>
    /// The package of a PowerShell session: the device services, the install and apps_init with the commands, and the
    /// environment with the variable that skips PowerShell's login-shell check. Until upstream guards that check with
    /// Platform.IsMacOS (C1.1: every Unix that is not Linux takes the macOS path and calls sysctl), the variable pwsh's own
    /// re-exec sets skips it.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="commands">The lines of /etc/apps.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> PackageAsync(string root, string output, KernelArchitecture architecture,
        IReadOnlyList<string> commands)
    {
        var package = (await RuntimeApps.PackageAsync(root, output, architecture, [], commands)).ToList();
        var environmentIndex = package.FindIndex(file => file.Name == "etc/environment");
        var environment = Path.Combine(output, "environment");
        await File.WriteAllTextAsync(environment, await File.ReadAllTextAsync(package[environmentIndex].Source) + "__PWSH_LOGIN_CHECKED=1\n");
        package[environmentIndex] = ("etc/environment", environment);
        return package;
    }

    /// <summary>
    /// The commands /bin/init runs: PowerShell's version table, then one line of the acceptance's cmdlets, then an
    /// interactive session with the profile.
    /// </summary>
    /// <returns>The lines of /etc/apps.</returns>
    public static string[] Commands() =>
    [
        "cd /data",
        $"{BATCH} $PSVersionTable",
        // The pipeline's count and the date as plain words: PowerShell colors its formatted output on a terminal (PSStyle)
        // and formats a date by what came before it, so a required line never spans a label and its value.
        $"{BATCH} '{EVEN}=' + (1..10 | Where-Object {{ $_ % 2 -eq 0 }} | Measure-Object).Count; " +
            $"Set-Content /data/pwsh.txt {WRITTEN}; Get-Content /data/pwsh.txt; '{DATE}=' + (Get-Date).ToString('yyyy-MM-dd'); Get-ChildItem /data; " +
            // The shell applications run commands through, and the POSIX shell WitOS does not provide (ADR 0027 decision 6).
            $"'{SHELL}=' + $env:SHELL; 'sh=' + (Test-Path /bin/sh); " +
            // Where PowerShell runs from (R9.2): the version's native directory where the architecture's framework is
            // ReadyToRun, and how many methods the JIT compiled for this command, which its ReadyToRun images spare.
            $"'{PSHOME}=' + $PSHOME; '{JITTED}=' + [System.Runtime.JitInfo]::GetCompiledMethodCount()",
        PWSH
    ];

    /// <summary>
    /// What the runner types: the terminal probe's line, then the interactive session's commands, each after the prompt.
    /// Enter is CR: PSReadLine reads the terminal raw, where the Enter key is a carriage return.
    /// </summary>
    /// <returns>The inputs.</returns>
    public static IReadOnlyList<TerminalInput> Script() =>
    [
        new("[TERMINAL] type a line", RuntimeTerminal.PROBE_LINE + "\n"),
        new(PROMPT, "$PSVersionTable\r"),
        new(PROMPT, "1 + 2\r"),
        new(PROMPT, "Get-Date\r"),
        new(PROMPT, "Get-ChildItem /data\r"),
        new(PROMPT, "exit\r")
    ];

    /// <summary>
    /// The directory PowerShell runs from, as the batch command prints it: the layer's version, in its native directory
    /// where the architecture's framework is ReadyToRun and the host compiled the version (R9.2).
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="version">PowerShell's version.</param>
    /// <returns>The line.</returns>
    public static string HomeLine(KernelArchitecture architecture, string version) =>
        $"{PSHOME}=/data/apps/{PowerShell.CATALOG_ID}/{version}" + (architecture.ReadyToRunFramework ? "/" + NativeCode.DirectoryName(architecture.Name) : "");

    /// <summary>
    /// The lines the scenario requires, in the serial log or the transcript: the version table with WitOS's name, the
    /// pipeline's count, the file's text, the date, the listing, the console mark the profile drew with the shell's
    /// version in its panel, the interactive prompt, and every pwsh exiting with 0.
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="version">PowerShell's version, which the mark's panel names.</param>
    /// <returns>The required lines.</returns>
    public static string[] RequiredLines(KernelArchitecture architecture, string version) =>
    [
        "PSVersion",
        "WitOS",
        "Platform",
        $"{EVEN}=5",
        WRITTEN,
        $"{DATE}=20",
        "Directory: /data",
        $"{SHELL}=/data/apps/bin/pwsh",
        "sh=False",
        HomeLine(architecture, version),
        $"{JITTED}=",
        EMBLEM_ROW,
        $"PowerShell {version}",
        PROMPT,
        "[APPS-INIT] /data/apps/bin/pwsh -NoLogo exited with 0",
        "[APPS-INIT] 3 commands, 0 failed"
    ];

    #endregion
}
