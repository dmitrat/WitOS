using System.Security.Cryptography;
using System.Text.Json;
using WitOS.Dev.Host;
using WitOS.Dev.Images;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Native;

/// <summary>
/// Windows reference for DLL thread attach/detach notifications on normal and suspended threads (hosted).
/// </summary>
[TestFixture]
public sealed class ThreadNotificationsTests
{
    #region Functions

    [Test]
    public Task NativeDllThreadReferenceTest() => RunAsync(TestEnvironment.Root, TestEnvironment.Scratch());

    #endregion

    #region Tools

    private static async Task RunAsync(string root, string output)
    {
        var msvc = await Toolchain.FindMsvcAsync(root);
        var vc = Path.GetFullPath(Path.Combine(msvc, "../../.."));
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits/10");
        var version = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        var dll = await NativeThreadLibraryImage.BuildAsync(root, output, msvc);
        var exe = Path.Combine(output, "thread-notification-reference.exe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo","/MD","/TC","/std:c17","/W4","/WX","/O2",
            "/I"+Path.Combine(vc,"include"),"/I"+Path.Combine(sdk,"Include",version,"ucrt"),"/I"+Path.Combine(sdk,"Include",version,"um"),"/I"+Path.Combine(sdk,"Include",version,"shared"),
            "/Fo"+output+"/","/Fe"+exe,Path.Combine(root,"tests/WitOS.Dev.Tests/Native/ThreadNotifications.c"),"/link",
            "/LIBPATH:"+Path.Combine(vc,"lib/x64"),"/LIBPATH:"+Path.Combine(sdk,"Lib",version,"ucrt/x64"),"/LIBPATH:"+Path.Combine(sdk,"Lib",version,"um/x64"),"kernel32.lib"], root);
        var run = await Processes.RunAsync(exe, [dll], output, 90);
        await File.WriteAllTextAsync(Path.Combine(output, "thread-notifications.log"), run.Output + run.Error);
        if (run.TimedOut || run.ExitCode != 0 || !run.Output.StartsWith("PASS: actual Windows normal/suspended DLL", StringComparison.Ordinal))
            throw new InvalidDataException("Thread notification reference failed: " + run.ExitCode + " " + run.Output + run.Error);
        string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        await File.WriteAllTextAsync(Path.Combine(output, "thread-notifications.json"), JsonSerializer.Serialize(new
        {
            hostOnly = true,
            guestExecuted = false,
            dllSha256 = Hash(dll),
            executableSha256 = Hash(exe),
            sources = new[] { "tests/User.X64/library_thread_entry.c", "tests/User.X64/library_entry_x64.asm", "tests/WitOS.Dev.Tests/Native/ThreadNotifications.c" }.ToDictionary(file => file, file => Hash(Path.Combine(root, file)))
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.Write(run.Output);
    }

    #endregion
}
