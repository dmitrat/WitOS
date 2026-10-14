using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DotNetCompatibility;

/// <summary>
/// What the platform says it is, and what an application may infer from it (RFC 0015 §1): the checks that record, rather
/// than test, the differences of an operating system that is neither Windows nor Linux nor macOS.
/// </summary>
internal static class PlatformChecks
{
    /// <summary>
    /// The platform's key in expectations.json.
    /// </summary>
    public static string Key =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsMacOS() ? "macos"
        : RuntimeInformation.IsOSPlatform(OSPlatform.Create("WITOS")) ? "witos"
        : "other";

    /// <summary>
    /// The area's checks.
    /// </summary>
    /// <returns>The checks.</returns>
    public static IEnumerable<Check> All()
    {
        yield return new Check("Platform", "Identity", () => CheckResult.Now(
            RuntimeInformation.OSDescription.Length > 0 && RuntimeInformation.RuntimeIdentifier.Length > 0,
            $"{RuntimeInformation.OSDescription}, {RuntimeInformation.RuntimeIdentifier}, {RuntimeInformation.FrameworkDescription}"));
        yield return new Check("Platform", "KnownOperatingSystem", () =>
        {
            var known = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "macOS"
                : OperatingSystem.IsFreeBSD() ? "FreeBSD" : null;
            return CheckResult.Now(known is not null, known ?? "none of Windows, Linux, macOS and FreeBSD");
        });
        yield return new Check("Platform", "Processors", () =>
            CheckResult.Now(Environment.ProcessorCount >= 1 && Environment.Is64BitProcess,
                $"{Environment.ProcessorCount} processors, 64-bit {Environment.Is64BitProcess}"));
        yield return new Check("Platform", "ProcessStart", async () =>
        {
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--list-runtimes")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            })!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return CheckResult.That(process.ExitCode == 0 && output.Contains("Microsoft.NETCore.App", StringComparison.Ordinal),
                $"{Path.GetFileName(Environment.ProcessPath)} --list-runtimes exited with {process.ExitCode}");
        });
        // The suite itself as a child (Program.cs's --echo, N4.2): its input, output and error through pipes, many times
        // a pipe's buffer, its directory, a variable of its environment and its exit code.
        yield return new Check("Platform", "ProcessPipes", async () =>
        {
            var directory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, [Suite, "--echo", "7"])
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = directory,
                Environment = { ["DOTNET_COMPATIBILITY_CHILD"] = "echo" }
            })!;
            var text = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog.\n", 4096));
            var writing = Task.Run(async () =>
            {
                await process.StandardInput.WriteAsync(text);
                process.StandardInput.Close();
            });
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(writing, output, error);
            await process.WaitForExitAsync();
            var expectedError = $"echoed {text.Length} characters in {directory} with DOTNET_COMPATIBILITY_CHILD=echo\n";
            return CheckResult.That(process.ExitCode == 7 && await output == text && await error == expectedError,
                $"exit code {process.ExitCode}, {(await output).Length} of {text.Length} characters back, error stream \"{(await error).Trim()}\"");
        });
        // A child that never ends is killed (N4.2): on Unix with SIGKILL, which its exit code reports as 128 + 9.
        yield return new Check("Platform", "ProcessKill", async () =>
        {
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, [Suite, "--sleep"]) { UseShellExecute = false })!;
            var endedByItself = process.WaitForExit(500);
            process.Kill();
            await process.WaitForExitAsync();
            return CheckResult.That(!endedByItself && process.HasExited && (OperatingSystem.IsWindows() || process.ExitCode == 137),
                $"exit code {process.ExitCode} after Kill, ended by itself {endedByItself}");
        });
    }

    // The suite's own assembly, which dotnet runs as the child of the process checks.
    private static string Suite => Path.Combine(AppContext.BaseDirectory, "DotNetCompatibility.dll");
}
