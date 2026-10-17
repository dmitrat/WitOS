namespace WitOS.Dev.Kernel;

/// <summary>
/// Kernel exception that a RISC-V fault scenario must report (plan step V1.2).
/// </summary>
/// <param name="Cause">Exception code, scause without its interrupt bit.</param>
/// <param name="Trigger">Test marker printed before the fault.</param>
/// <param name="Panic">Panic reason after the exception report.</param>
/// <param name="FaultAddress">Whether stval must hold the fixed unmapped fault probe address.</param>
/// <param name="Probe">Whether stval must hold the address the guest reports on its [FAULT-PROBE] line.</param>
internal sealed record Rv64FaultExpectation(int Cause, string Trigger, string Panic, bool FaultAddress = false,
    bool Probe = false);
