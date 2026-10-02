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
    EntropyUnavailable
}
