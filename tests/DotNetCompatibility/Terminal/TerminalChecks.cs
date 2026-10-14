using System.Runtime.InteropServices;

namespace DotNetCompatibility;

/// <summary>
/// The console as a terminal (plan step N4.3b, RFC 0020 §6): what an interactive program does with System.Console when
/// its input is a terminal — reads a line, reads a key, asks whether a key waits, sees the window's size and gets
/// Ctrl-C as CancelKeyPress and as SIGINT. The checks run where the input is a terminal, which the runtime-compatibility
/// scenario gives the suite with a runner that types what each prompt asks for; where the input is redirected, as on
/// the hosts' test runs, they are not applicable.
/// </summary>
internal static class TerminalChecks
{
    /// <summary>
    /// The prompts the checks print, which the runner answers.
    /// </summary>
    public const string LINE_PROMPT = "[N1] type a line";

    public const string LINE = "hello n1";

    public const string KEY_PROMPT = "[N1] press a key";

    public const string KEY_AVAILABLE_PROMPT = "[N1] type y";

    public const string CANCEL_PROMPT = "[N1] press Ctrl-C";

    public const string SIGNAL_PROMPT = "[N1] press Ctrl-C again";

    private const string NO_TERMINAL = "the input is not a terminal";

    /// <summary>
    /// The area's checks.
    /// </summary>
    /// <returns>The checks.</returns>
    public static IEnumerable<Check> All()
    {
        yield return new Check("Terminal", "IsTerminal", () => Applicable(() =>
            CheckResult.That(!Console.IsInputRedirected && !Console.IsOutputRedirected && !Console.IsErrorRedirected,
                "the input, the output and the error are a terminal")));
        yield return new Check("Terminal", "WindowSize", () => Applicable(() =>
            CheckResult.That(Console.WindowWidth > 0 && Console.WindowHeight > 0, $"{Console.WindowWidth} by {Console.WindowHeight}")));
        yield return new Check("Terminal", "ReadLine", () => Applicable(() =>
        {
            Console.WriteLine(LINE_PROMPT);
            var line = Console.ReadLine();
            return CheckResult.That(line == LINE, $"read \"{line}\"");
        }));
        yield return new Check("Terminal", "ReadKey", () => Applicable(() =>
        {
            Console.WriteLine(KEY_PROMPT);
            var key = Console.ReadKey(intercept: true);
            return CheckResult.That(key.KeyChar == 'k', $"read '{key.KeyChar}' ({key.Key})");
        }));
        yield return new Check("Terminal", "KeyAvailable", () => Applicable(() =>
        {
            var before = Console.KeyAvailable;
            Console.WriteLine(KEY_AVAILABLE_PROMPT);
            var waited = 0;
            while (!Console.KeyAvailable && waited < 10000)
            {
                Thread.Sleep(50);
                waited += 50;
            }
            var available = Console.KeyAvailable;
            var key = Console.ReadKey(intercept: true);
            while (Console.KeyAvailable)
                Console.ReadKey(intercept: true); // the line's end
            return CheckResult.That(!before && available && key.KeyChar == 'y', $"none before, {(available ? "a key" : "none")} after {waited} ms, '{key.KeyChar}'");
        }));
        yield return new Check("Terminal", "CancelKeyPress", () => Applicable(() =>
        {
            var pressed = new ManualResetEventSlim();
            ConsoleCancelEventHandler handler = (_, arguments) =>
            {
                arguments.Cancel = true;
                pressed.Set();
            };
            Console.CancelKeyPress += handler;
            try
            {
                Console.WriteLine(CANCEL_PROMPT);
                var raised = pressed.Wait(TimeSpan.FromSeconds(10));
                return CheckResult.That(raised, raised ? "CancelKeyPress ran and cancelled the break" : "no CancelKeyPress within 10 s");
            }
            finally
            {
                Console.CancelKeyPress -= handler;
            }
        }));
        yield return new Check("Terminal", "PosixSignal", () => Applicable(() =>
        {
            var raised = new ManualResetEventSlim();
            using var registration = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
            {
                context.Cancel = true;
                raised.Set();
            });
            Console.WriteLine(SIGNAL_PROMPT);
            var got = raised.Wait(TimeSpan.FromSeconds(10));
            return CheckResult.That(got, got ? "SIGINT reached the registration, which cancelled it" : "no SIGINT within 10 s");
        }));
    }

    // A check that runs only with a terminal, on a thread of its own so that a wait for the runner blocks no other.
    private static Task<CheckResult> Applicable(Func<CheckResult> check) =>
        Console.IsInputRedirected ? Task.FromResult(CheckResult.NotApplicable(NO_TERMINAL)) : Task.Run(check);
}
