namespace WitOS.Dev.Kernel;

/// <summary>
/// Kernel exception a fault scenario must report before panicking.
/// </summary>
/// <param name="Vector">Expected CPU exception vector.</param>
/// <param name="Error">Expected error code.</param>
/// <param name="Trigger">Test name printed before the fault is triggered.</param>
/// <param name="Panic">Panic reason printed after the exception frame.</param>
/// <param name="Probe">Whether the faulting address comes from a fault probe line.</param>
internal sealed record FaultExpectation(int Vector, ulong Error, string Trigger, string Panic, bool Probe = false);
