// The first managed program under CoreCLR on WitOS (plan step R3.2): corerun loads it with CoreLib and the framework's
// libraries for witos, and the JIT compiles every method, since the libraries carry IL alone.
using System;
using System.Runtime.InteropServices;

internal static class CoreRun
{
    private static int Main()
    {
        Console.WriteLine($"[CORERUN] managed Main through the JIT on {RuntimeInformation.OSArchitecture}");
        return 0;
    }
}
