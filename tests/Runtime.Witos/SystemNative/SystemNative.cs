// System.Native for witos (plan step R4): the class libraries' Unix flavours over the system layer, checked from managed
// code: files of the boot package, the current directory, time, the environment, invariant globalization and the
// refusals the compatibility contract records (RFC 0015 sections 5 and 8). Each area prints one line; the last counts.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

internal static class SystemNative
{
    private static int m_passed, m_failed;

    private static int Main(string[] arguments)
    {
        Area("files", Files);
        Area("writes refused", WritesRefused);
        Area("current directory", CurrentDirectory);
        Area("time", Time);
        Area("environment", EnvironmentArea);
        Area("globalization", Globalization);
        Area("random", RandomBytes);
        Area("console", ConsoleArea);
        Area("signals", Signals);
        Area("processes refused", ProcessesRefused);
        Area("hashes", Hashes);
        Area("cryptography refused", CryptographyRefused);
        string runtime = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled ? "CoreCLR" : "NativeAOT";
        Console.WriteLine($"[R4] System.Native under {runtime} on {RuntimeInformation.ProcessArchitecture}: {m_passed} areas passed, {m_failed} failed");
        return m_failed == 0 ? 0 : 1;
    }

    private static void Area(string name, Func<string?> check)
    {
        string? failure;
        try
        {
            failure = check();
        }
        catch (Exception exception)
        {
            failure = $"threw {exception.GetType().Name}: {exception.Message}";
        }
        if (failure is null)
            ++m_passed;
        else
            ++m_failed;
        Console.WriteLine($"[R4] {name}: {failure ?? "ok"}");
    }

    private static string? Files()
    {
        if (File.ReadAllText("/test/hello.txt") != "Hello, package!\nsecond line\n")
            return "ReadAllText";
        if (new FileInfo("/test/hello.txt").Length != 28 || !File.Exists("/test/hello.txt") || File.Exists("/test/none"))
            return "FileInfo or Exists";
        if (!Directory.Exists("/test/dir") || Directory.Exists("/test/hello.txt"))
            return "Directory.Exists";
        string[] files = Directory.GetFiles("/test/dir");
        Array.Sort(files, StringComparer.Ordinal);
        if (files.Length != 2 || files[0] != "/test/dir/a.txt" || files[1] != "/test/dir/b.txt")
            return $"GetFiles: {string.Join(",", files)}";
        using (var stream = new FileStream("/test/hello.txt", FileMode.Open, FileAccess.Read))
        {
            var buffer = new byte[6];
            stream.Seek(16, SeekOrigin.Begin);
            if (stream.Read(buffer, 0, 6) != 6 || Encoding.ASCII.GetString(buffer) != "second")
                return "FileStream seek and read";
        }
        if (File.ReadAllBytes("/coreclr/System.Private.CoreLib.dll").Length < 1 << 20)
            return "a large file";
        return null;
    }

    private static string? WritesRefused()
    {
        try
        {
            File.WriteAllText("/test/new.txt", "x");
            return "a write succeeded";
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        try
        {
            Directory.CreateDirectory("/test/newdir");
            return "a directory was created";
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private static string? CurrentDirectory()
    {
        string start = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory("/test");
        try
        {
            if (Directory.GetCurrentDirectory() != "/test" || !File.Exists("hello.txt") ||
                Path.GetFullPath("dir/a.txt") != "/test/dir/a.txt")
                return $"relative paths from {Directory.GetCurrentDirectory()}";
        }
        finally
        {
            Directory.SetCurrentDirectory(start);
        }
        return start == "/" ? null : $"started in {start}";
    }

    private static string? Time()
    {
        DateTime utc = DateTime.UtcNow;
        if (utc.Year < 2026)
            return $"UtcNow {utc:O}";
        var watch = Stopwatch.StartNew();
        Thread.Sleep(50);
        watch.Stop();
        if (watch.ElapsedMilliseconds < 50)
            return $"slept {watch.ElapsedMilliseconds} ms";
        if (TimeZoneInfo.Local.BaseUtcOffset != TimeSpan.Zero || (DateTime.Now - DateTime.UtcNow).Duration() > TimeSpan.FromSeconds(1))
            return $"local time {TimeZoneInfo.Local.Id}";
        return null;
    }

    private static string? EnvironmentArea()
    {
        Environment.SetEnvironmentVariable("WITOS_R4", "set");
        if (Environment.GetEnvironmentVariable("WITOS_R4") != "set")
            return "SetEnvironmentVariable";
        if (Environment.ProcessorCount < 1 || !Environment.Is64BitProcess || Environment.OSVersion.Platform != PlatformID.Unix)
            return "processors, bitness or platform";
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Create("WITOS")))
            return $"OSPlatform: {RuntimeInformation.OSDescription}";
        long before = Environment.TickCount64;
        Thread.Sleep(20);
        if (Environment.TickCount64 - before < 20)
            return "TickCount64";
        return string.IsNullOrEmpty(Environment.MachineName) ? "MachineName" : null;
    }

    private static string? Globalization()
    {
        if (CultureInfo.CurrentCulture.Name != "" || 1234.5.ToString() != "1234.5")
            return $"culture '{CultureInfo.CurrentCulture.Name}'";
        if (string.Compare("a", "B", StringComparison.CurrentCultureIgnoreCase) >= 0 || "abc".ToUpper() != "ABC")
            return "comparison or casing";
        return null;
    }

    private static string? RandomBytes()
    {
        var first = RandomNumberGenerator.GetBytes(32);
        var second = RandomNumberGenerator.GetBytes(32);
        return first.AsSpan().SequenceEqual(second) ? "two draws are equal" : null;
    }

    private static string? ConsoleArea()
    {
        // Standard input is no terminal and ends at once: a line read from it is the end of the input.
        return Console.In.ReadLine() is null && Console.IsInputRedirected ? null : "standard input is a terminal or gave a line";
    }

    private static string? Signals()
    {
        using var registration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => context.Cancel = true);
        Console.CancelKeyPress += (sender, args) => args.Cancel = true;
        return null;
    }

    private static string? ProcessesRefused()
    {
        if (Environment.ProcessId <= 0)
            return "ProcessId";
        try
        {
            Process.Start("/coreclr/corerun");
            return "Process.Start succeeded";
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    // The managed hashes the browser builds too: SHA-256 of "abc" (FIPS 180-2) and an HMAC.
    private static string? Hashes()
    {
        string sha = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes("abc")));
        if (sha != "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD")
            return $"SHA256 {sha}";
        return HMACSHA256.HashData(new byte[16], new byte[] { 1 }).Length == 32 ? null : "HMAC";
    }

    // No provider of asymmetric cryptography or X.509 yet: an explicit refusal (RFC 0015 section 8).
    private static string? CryptographyRefused()
    {
        try
        {
            using var rsa = RSA.Create();
            return "RSA.Create succeeded";
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }
}
