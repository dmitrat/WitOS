// A .NET application on a mounted volume (plan step D5.3): dotnet /data/programs/app/App.dll, where the muxer and the
// shared framework come from the boot package and the application from the ext4 volume the namespace service mounts.
// hostfxr reads the runtimeconfig there, hostpolicy lists the directory for the application's assemblies (it has no
// deps.json), and CoreCLR maps App.dll and VolumeLibrary.dll from it; the application reads a file beside it.
using System;
using System.IO;
using System.Runtime.InteropServices;

internal static class App
{
    private const string BASE = "/data/programs/app/";

    private static int Main(string[] arguments)
    {
        var greeting = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "greeting.txt")).Trim();
        var library = typeof(VolumeLibrary).Assembly.Location;
        if (AppContext.BaseDirectory != BASE || library != BASE + "VolumeLibrary.dll" || arguments.Length != 1 || arguments[0] != "witos")
        {
            Console.WriteLine($"[VOLUME-APP] FAILED: base '{AppContext.BaseDirectory}', library '{library}', " +
                $"arguments '{string.Join(" ", arguments)}'");
            return 1;
        }
        Console.WriteLine($"[VOLUME-APP] App.dll and its library from {BASE} under CoreCLR on {RuntimeInformation.ProcessArchitecture}: " +
            $"{VolumeLibrary.Answer(greeting)}");
        return 0;
    }
}
