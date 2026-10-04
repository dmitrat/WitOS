namespace WitOS.Dev.Kernel;

/// <summary>
/// Kernel exception that an ARM64 fault scenario must report.
/// </summary>
/// <param name="Class">Exception class, ESR_EL1.EC.</param>
/// <param name="Trigger">Test marker printed before the fault.</param>
/// <param name="Panic">Panic reason after the exception report.</param>
/// <param name="FaultAddress">Whether FAR must hold the fixed unmapped fault probe address.</param>
/// <param name="Probe">Whether FAR must hold the address the guest reports on its [FAULT-PROBE] line.</param>
internal sealed record A64FaultExpectation(int Class, string Trigger, string Panic, bool FaultAddress = false,
    bool Probe = false);
