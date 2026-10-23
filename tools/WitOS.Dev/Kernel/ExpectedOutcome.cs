namespace WitOS.Dev.Kernel;

/// <summary>
/// Outcome a QEMU boot must produce; each value has its own exit code and log criteria.
/// </summary>
internal enum ExpectedOutcome
{
    Success,
    InvalidBootInfo,
    InvalidMap,
    Exception,
    Timeout,
    ClockUnavailable,
    EntropyUnavailable,
    Cut,

    /// <summary>
    /// The guest turned the board off at its end (plan step H1.2a): QEMU exits with 0 after the kernel's power line.
    /// </summary>
    PoweredOff,

    /// <summary>
    /// The guest reset the board at the root task's request (plan step H1.2a): QEMU, started with -no-reboot, exits with 0
    /// after the kernel's reset line.
    /// </summary>
    Reset
}
