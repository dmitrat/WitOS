using WitOS.Dev.Host;
using WitOS.Dev.Kernel;
using WitOS.Dev.Substrate;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The PowerShell scenario (plan step C1.3, RFC 0020 section 57): upstream PowerShell, the pinned framework-dependent
/// archive with libpsl-native built for the ISA, lies on the data volume under /data/pwsh and runs as
/// <c>dotnet /data/pwsh/pwsh.dll</c> beside the device services, the .NET install and the terminal; /bin/init runs it
/// twice without a terminal session (the version table; a pipeline, files on the volume, the date and a listing) and
/// then interactively, where the runner types into PSReadLine on its emulated terminal. The required lines are in the
/// serial log or in the terminal's transcript.
/// </summary>
internal static class RuntimePowerShell
{
    #region Constants

    /// <summary>
    /// PowerShell's directory on the data volume.
    /// </summary>
    public const string VOLUME_DIRECTORY = "pwsh";

    /// <summary>
    /// How /bin/init starts PowerShell: the install's dotnet on pwsh.dll, without the banner and the profile.
    /// </summary>
    public const string PWSH = "/dotnet/dotnet /data/pwsh/pwsh.dll -NoLogo -NoProfile";

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
    /// The prompt PowerShell shows in the data volume's root, which the runner types after.
    /// </summary>
    public const string PROMPT = "PS /data>";

    /// <summary>
    /// The guest's memory: PowerShell's engine and its modules through the JIT need more than the suites' 512 MiB.
    /// </summary>
    public const int MEMORY_MIB = 1024;

    private const int TIMEOUT_SECONDS = 3600;

    private const long DISK_BYTES = 1024L << 20;

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
        var package = (await RuntimeApps.PackageAsync(root, output, architecture, [], Commands())).ToList();
        // Until upstream guards its login-shell check with Platform.IsMacOS (C1.1: every Unix that is not Linux takes
        // the macOS path and calls sysctl), the variable pwsh's own re-exec sets skips the check for this scenario.
        var environmentIndex = package.FindIndex(file => file.Name == "etc/environment");
        var environment = Path.Combine(output, "environment");
        await File.WriteAllTextAsync(environment, await File.ReadAllTextAsync(package[environmentIndex].Source) + "__PWSH_LOGIN_CHECKED=1\n");
        package[environmentIndex] = ("etc/environment", environment);
        var keep = Path.Combine(output, "keep");
        await File.WriteAllTextAsync(keep, "");
        var volume = new List<(string Path, string Source)> { ("home/.keep", keep) };
        volume.AddRange(await PowerShell.VolumeFilesAsync(root, architecture, VOLUME_DIRECTORY));
        foreach (var log in new[] { "serial", "terminal" })
        {
            var path = Path.Combine(root, "artifacts", "logs", $"{architecture.Name}-{scenario}-{MEMORY_MIB}.{log}.log");
            if (File.Exists(path))
                File.Delete(path);
        }
        await RuntimeApps.BootAsync(root, architecture, scenario, RequiredLines(architecture), TIMEOUT_SECONDS, volume, DISK_BYTES, MEMORY_MIB, package,
            Script());
        Console.WriteLine($"PowerShell on witos-{architecture.Name}: {(await PowerShell.ReadPinAsync(root)).Version} ran unchanged from the data volume, " +
            "non-interactively and under PSReadLine on the terminal.");
    }

    /// <summary>
    /// The commands /bin/init runs: PowerShell's version table, then one line of the acceptance's cmdlets, then an
    /// interactive session.
    /// </summary>
    /// <returns>The lines of /etc/apps.</returns>
    public static string[] Commands() =>
    [
        "cd /data",
        $"{PWSH} -NonInteractive -Command $PSVersionTable",
        // The pipeline's count as one plain word: PowerShell colors its formatted output on a terminal (PSStyle), so a
        // required line never spans a label and its value.
        $"{PWSH} -NonInteractive -Command '{EVEN}=' + (1..10 | Where-Object {{ $_ % 2 -eq 0 }} | Measure-Object).Count; " +
            $"Set-Content /data/pwsh.txt {WRITTEN}; Get-Content /data/pwsh.txt; '{DATE}=' + (Get-Date).ToString('yyyy-MM-dd'); Get-ChildItem /data",
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
    /// The lines the scenario requires, in the serial log or the transcript: the version table with WitOS's name, the
    /// pipeline's count, the file's text, the date, the listing, the interactive prompt, and every pwsh exiting with 0.
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The required lines.</returns>
    public static string[] RequiredLines(KernelArchitecture architecture) =>
    [
        "PSVersion",
        "WitOS",
        "Platform",
        $"{EVEN}=5",
        WRITTEN,
        $"{DATE}=20",
        "Directory: /data",
        PROMPT,
        "[APPS-INIT] /dotnet/dotnet /data/pwsh/pwsh.dll exited with 0",
        "[APPS-INIT] 3 commands, 0 failed"
    ];

    #endregion
}
