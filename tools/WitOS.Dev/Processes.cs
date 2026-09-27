using System.Diagnostics;

namespace WitOS.Dev;

internal sealed record ProcessResult(int ExitCode, string Output, string Error, bool TimedOut);

internal static class Processes
{
    public static async Task<ProcessResult> RunAsync(
        string executable, IEnumerable<string> arguments, string directory, int timeoutSeconds = 60, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (environment is not null)
            foreach (var (name, value) in environment) start.Environment[name] = value;
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {executable}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        return new ProcessResult(process.ExitCode, await output, await error, timedOut);
    }

    public static async Task RequireSuccessAsync(string executable, IEnumerable<string> arguments, string directory)
    {
        var result = await RunAsync(executable, arguments, directory);
        if (result.TimedOut || result.ExitCode != 0)
            throw new InvalidOperationException(
                $"{Path.GetFileName(executable)} failed (exit {result.ExitCode}, timeout={result.TimedOut}).\n{result.Output}\n{result.Error}");
    }
}
