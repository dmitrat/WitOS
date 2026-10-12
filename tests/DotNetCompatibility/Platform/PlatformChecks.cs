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
    }
}
