// The first program ILC compiles for WitOS (plan step R1.3), against the NativeAOT CoreLib built for TargetOS=witos alone:
// Main answers whether the platform is WitOS and none of the other platforms the runtime knows. CoreLib spells the
// platform as a constant (OSPlatformName, RFC 0015 section 3) and the other answers are constants of a witos build, so
// the JIT folds Main into returning zero; runtime-witos requires that code. Plan step R2 runs the program in the guest.
internal static class Program
{
    private static int Main() =>
        System.OperatingSystem.IsOSPlatform("WitOS") && !System.OperatingSystem.IsLinux() &&
        !System.OperatingSystem.IsFreeBSD() && !System.OperatingSystem.IsWindows() ? 0 : 1;
}
