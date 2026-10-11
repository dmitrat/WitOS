// The first program through the hosts on WitOS (plan step R5): dotnet App.dll, where the muxer finds hostfxr, hostfxr
// reads App.runtimeconfig.json and picks the shared framework, and hostpolicy builds the trusted platform assemblies from
// the framework's deps.json and starts CoreCLR. The line names what the hosts gave the runtime.
using System;
using System.Runtime.InteropServices;

internal static class App
{
    private static int Main(string[] arguments)
    {
        string framework = AppContext.GetData("FX_DEPS_FILE") as string ?? "";
        bool invariant = AppContext.TryGetSwitch("System.Globalization.Invariant", out bool on) && on;
        if (!framework.StartsWith("/dotnet/shared/Microsoft.NETCore.App/", StringComparison.Ordinal) || !invariant ||
            AppContext.BaseDirectory != "/app/" || arguments.Length != 1 || arguments[0] != "witos")
        {
            Console.WriteLine($"[DOTNET] FAILED: framework '{framework}', invariant {invariant}, base '{AppContext.BaseDirectory}', " +
                $"arguments '{string.Join(" ", arguments)}'");
            return 1;
        }
        Console.WriteLine($"[DOTNET] App.dll through hostfxr and hostpolicy on {RuntimeInformation.ProcessArchitecture}: " +
            $"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.RuntimeIdentifier}");
        return 0;
    }
}
