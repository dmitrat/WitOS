using System.Globalization;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;
using WitOS.Dev.Substrate;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The board's time (plan step H7.4, the author's decision of 2026-10-26: the saved time and SNTP): the device services
/// and the time service, whose package names the runner's time server, boot twice with one data disk. The first boot has
/// the adapter: the time service sets UTC to the server's answer, which is the host's time plus three days, so that the
/// guest's clock shows the answer set it, and /bin/init (tests/User/time_check.c) prints UTC and checks the clocks. The
/// second boot has no adapter. On ARM64 the board is virt booted by the Linux protocol with a tree that lacks its PL031,
/// a board without a real-time clock as a Raspberry Pi is: the first boot finds no saved time and saves the server's at
/// its stop, and the second sets UTC to that saved time. x64's and riscv64's boards have their real-time clocks: the
/// second boot reads the board's time again, and nothing is saved.
/// </summary>
internal static class RuntimeTime
{
    #region Constants

    /// <summary>
    /// The scenario's /bin/init.
    /// </summary>
    public const string INIT = "tests/User/time_check.c";

    /// <summary>
    /// What the runner's time server adds to the host's time.
    /// </summary>
    public static readonly TimeSpan SERVER_OFFSET = TimeSpan.FromDays(3);

    // How far the guest's clock may stand from what the host expects of it: the guest's time runs ahead of the host's
    // while it idles under QEMU's instruction clock, never far behind.
    private static readonly TimeSpan BEHIND = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AHEAD = TimeSpan.FromMinutes(10);

    private const int MEMORY_MIB = 512;
    private const int TIMEOUT_SECONDS = 1800;

    private const string SAVED_FILE = "/data/system/time";
    private const string SERVERS_FILE = "etc/witos/time-servers";

    private static readonly Regex CHECK_UTC = new(@"\[TIME-CHECK\] UTC (\d+)\.(\d{9}) ", RegexOptions.CultureInvariant);
    private static readonly Regex RESTORED = new(@"\[TIME\] no real-time clock: UTC set to (\S+), the time saved on ", RegexOptions.CultureInvariant);
    private static readonly Regex SAVED = new(@"\[TIME\] saved (\S+) on ", RegexOptions.CultureInvariant);

    #endregion

    #region Functions

    /// <summary>
    /// Boots the scenario on the services an earlier run published.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>When both boots passed and the guest's clock stood where the host expected it.</returns>
    /// <exception cref="InvalidOperationException">A boot failed, or the guest's clock stood elsewhere.</exception>
    public static async Task RunAsync(string root, KernelArchitecture architecture)
    {
        var scenario = KernelImageBuilder.RUNTIME_TIME_SCENARIO;
        var output = Path.Combine(root, "artifacts", architecture.Name, scenario);
        Directory.CreateDirectory(output);
        using var server = new TimePeer(SERVER_OFFSET);
        var withoutClock = architecture == KernelArchitecture.Arm64;

        var services = RuntimeDevices.ServiceFiles(root, architecture);
        await Sysroot.BuildAsync(root, architecture);
        var init = Path.Combine(output, "time_check.o");
        await MuslLibc.CompileAsync(root, architecture, Path.Combine(root, INIT), init, LibWitos.Options(root));
        var initProgram = await MuslLibc.LinkStartedProgramAsync(root, architecture, output, "time_check", [init]);
        var servers = Path.Combine(output, "time-servers");
        await File.WriteAllTextAsync(servers, $"# the runner's time server (H7.4)\n{TimePeer.GUEST_ADDRESS}:{server.Port}\n");
        List<(string Name, string Source)> package = [("bin/init", initProgram), (SERVERS_FILE, servers), .. services];
        var image = await KernelImageBuilder.BuildAsync(root, scenario, architecture: architecture, package: [.. package.DistinctBy(file => file.Name)]);
        var dataDisk = Path.Combine(Path.GetDirectoryName(image)!, "data-disk.img");
        await RuntimeDataDisk.CreateAsync(dataDisk);

        // The first boot: the server's answer sets UTC.
        var serverLine = $"[TIME] SNTP {TimePeer.GUEST_ADDRESS}:{server.Port} at {TimePeer.GUEST_ADDRESS}:{server.Port}, stratum 1";
        var (first, firstStart, firstEnd) = await BootAsync(root, architecture, image, dataDisk, 1, network: true, withoutClock,
        [
            serverLine,
            .. withoutClock
                ? (string[])["UTC: no real-time clock", "[DEVICES] PCI: ", "[TIME] no real-time clock and no saved time", "[TIME] saved "]
                : ["UTC at boot: ", "[TIME] the board's real-time clock reads "]
        ]);
        var firstUtc = CheckUtc(first, 1);
        Require(firstUtc, server.ServerTime(firstStart) - BEHIND, server.ServerTime(firstEnd) + AHEAD, "the first boot's UTC, the time server's answer");
        if (server.Answered == 0)
            throw new InvalidOperationException("The runner's time server answered no request.");

        // The second boot: no adapter, so no server; the saved time on a board without a clock, the board's otherwise.
        var (second, secondStart, secondEnd) = await BootAsync(root, architecture, image, dataDisk, 2, network: false, withoutClock,
            withoutClock
                ? ["UTC: no real-time clock", $"the time saved on {SAVED_FILE}"]
                : ["UTC at boot: ", "[TIME] the board's real-time clock reads "]);
        var secondUtc = CheckUtc(second, 2);
        if (withoutClock)
        {
            var saved = Times(SAVED, first).Last();
            var restored = Times(RESTORED, second).Single();
            if (restored != saved)
                throw new InvalidOperationException($"The second boot set UTC to {restored:O}, where the first boot saved {saved:O} last.");
            if (saved < firstUtc)
                throw new InvalidOperationException($"The first boot saved {saved:O} at its stop, before the {firstUtc:O} its /bin/init read.");
            Require(secondUtc, saved, saved + AHEAD, "the second boot's UTC, which goes on from the saved time");
        }
        else
        {
            Require(secondUtc, secondStart - BEHIND, secondEnd + AHEAD, "the second boot's UTC, the board's real-time clock");
            if (first.Contains("[TIME] saved ") || second.Contains("[TIME] saved "))
                throw new InvalidOperationException("The time service saved the time on a board with a real-time clock.");
        }
        Console.WriteLine(withoutClock
            ? $"The board's time on witos-{architecture.Name}: on virt without its PL031, the time service set UTC to the runner's SNTP answer " +
              $"({firstUtc:O}) and saved it at its stop; the second boot, without a network, went on from the saved time ({secondUtc:O})."
            : $"The board's time on witos-{architecture.Name}: the time service set UTC to the runner's SNTP answer ({firstUtc:O}); the second " +
              $"boot, without a network, read the board's real-time clock ({secondUtc:O}) and saved nothing.");
    }

    // A boot of the scenario: its serial log and the host's time around it.
    private static async Task<(string Log, DateTime Start, DateTime End)> BootAsync(string root, KernelArchitecture architecture, string image,
        string dataDisk, int number, bool network, bool withoutClock, IReadOnlyList<string> lines)
    {
        var name = $"{architecture.Name}-{KernelImageBuilder.RUNTIME_TIME_SCENARIO}-{number}";
        var start = DateTime.UtcNow;
        await BootScenarioRunner.RunAsync(root, image, new BootRequest(name, MEMORY_MIB, TIMEOUT_SECONDS, ExpectedOutcome.Success)
        {
            Architecture = architecture,
            Suite = BootSuite.Release,
            RequiredLines =
            [
                .. lines, "[ROOT-TASK] /system/Time was ready before /bin/init", "[TIME-CHECK] passed", "[TIME] time service: stopped",
                "[ROOT-TASK] /system/Time stopped", "[ROOT-TASK] /system/DeviceManager stopped", architecture.RootTaskPassedLine
            ],
            DataDisk = dataDisk,
            Network = network,
            LinuxBoot = withoutClock,
            WithoutRealTimeClock = withoutClock
        });
        var end = DateTime.UtcNow;
        return (await File.ReadAllTextAsync(Path.Combine(root, "artifacts", "logs", name + ".serial.log")), start, end);
    }

    // The UTC /bin/init printed.
    private static DateTime CheckUtc(string log, int boot)
    {
        var match = CHECK_UTC.Matches(log).SingleOrDefault()
            ?? throw new InvalidOperationException($"Boot {boot} printed no single \"[TIME-CHECK] UTC\" line.");
        return DateTime.UnixEpoch.AddSeconds(long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .AddTicks(long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) / 100);
    }

    // The times of the lines the expression matches.
    private static IReadOnlyList<DateTime> Times(Regex expression, string log) =>
    [
        .. expression.Matches(log).Select(match => DateTime.ParseExact(match.Groups[1].Value, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal))
    ];

    private static void Require(DateTime value, DateTime earliest, DateTime latest, string what)
    {
        if (value < earliest || value > latest)
            throw new InvalidOperationException($"{what} is {value:O}, outside {earliest:O} .. {latest:O}.");
        Console.WriteLine($"{what}: {value:O}, within {earliest:O} .. {latest:O}.");
    }

    #endregion
}
