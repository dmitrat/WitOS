using WitOS.Dev.Host;
using WitOS.Dev.Kernel;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Kernel;

/// <summary>
/// Boot log validation: the guest summary accounts for every contained user fault.
/// </summary>
[TestFixture]
public sealed class BootValidationTests
{
    #region Constants

    private const string FAULT =
        "[USER-FAULT] id=1 vector=14 error=0x0000000000000004 address=0x0000008000014E10 rip=0x0000008000101234 cs=0x0000000000000033\n";

    #endregion

    #region Functions

    [Test]
    public void SummaryAccountsForEveryFaultTest()
    {
        static string Summary(int faults, int checkedFaults) => $"[TEST-SUMMARY] faults={faults} checked={checkedFaults}\n";
        Assert.That(BootValidation.ValidateUserFaults(FAULT + FAULT + Summary(2, 2)), Is.True, "Accounted faults rejected");
        Assert.That(BootValidation.ValidateUserFaults(Summary(0, 0)), Is.True, "Fault-free boot rejected");
        Assert.That(BootValidation.ValidateUserFaults(FAULT + FAULT), Is.False, "Missing summary accepted");
        Assert.That(BootValidation.ValidateUserFaults(FAULT + FAULT + Summary(2, 1)), Is.False, "Unexpected fault accepted");
        Assert.That(BootValidation.ValidateUserFaults(FAULT + Summary(2, 2)), Is.False, "Lost fault line accepted");
        Assert.That(BootValidation.ValidateUserFaults(FAULT + Summary(1, 1) + FAULT), Is.False, "Fault after summary accepted");
        Assert.That(BootValidation.ValidateUserFaults(FAULT + Summary(1, 1) + Summary(1, 1)), Is.False, "Second summary accepted");
        Assert.That(BootValidation.ValidateUserFaults(FAULT + "[USER-FAULT] malformed\n" + Summary(2, 2)), Is.False,
            "Malformed fault accepted");
        Assert.That(BootValidation.ValidateUserFaults((FAULT + Summary(1, 1)).Replace("0033", "0008")), Is.False,
            "Supervisor fault accepted");
    }

    // A release scenario's user faults (plan step P5): the kernel's fault lines are forbidden unless the scenario provokes
    // that many on purpose.
    [Test]
    public void ReleaseJudgeCountsTheUserFaultsAScenarioProvokesTest()
    {
        var root = TestEnvironment.Root;
        var log = KernelAbi.Banner(root) + "\n[TEST-PASS] Boot.Contract\n[TEST-PASS] Cpu.ExceptionTables\n" +
            "[TEST-PASS] Memory.KernelPaging\n[TEST-PASS] Clock.IrqIndependent\nKernel initialized.\n[TEST-PASS] Boot.Hello\n" +
            "[LIBC] SIGSEGV ends the process: pc 0x1000\n" + FAULT + "[TEST-PASS] Boot.RootTask\n";
        var result = new ProcessResult(33, log, "", false);
        var request = new BootRequest("spawn", 256, 60, ExpectedOutcome.Success) { Suite = BootSuite.Release };
        Assert.That(BootValidation.EvaluateRelease(root, request, result).Passed, Is.False, "An unexpected fault accepted");
        Assert.That(BootValidation.EvaluateRelease(root, request with { UserFaults = 1 }, result).Passed, Is.True,
            "The provoked fault refused");
        Assert.That(BootValidation.EvaluateRelease(root, request with { UserFaults = 2 }, result).Passed, Is.False,
            "A missing fault accepted");
    }

    // The riscv64 fault judge (plan step V1.2): the foundation markers, the trigger, an exception taken in S-mode on the
    // kernel stack with the expected cause and the probe's address in stval, then the panic, with the failure exit.
    [Test]
    public void Riscv64ExceptionJudgeRequiresTheCauseTheModeAndTheStackTest()
    {
        var root = TestEnvironment.Root;
        var request = new BootRequest("riscv64-write-code", 256, 60, ExpectedOutcome.Exception)
        {
            Architecture = KernelArchitecture.Riscv64,
            Suite = BootSuite.Foundation,
            Rv64Fault = new Rv64FaultExpectation(15, "Memory.WriteCode", "Store page fault", Probe: true)
        };
        var markers = BootExpectations.Read(root, BootExpectations.RISCV64_FOUNDATION).Markers;
        const string frame = "[EXCEPTION] cause=0x000000000000000F sepc=0x0000000080012345 stval=0x0000000080010000 " +
            "sstatus=0x0000000200006100 sp=0x0000000080210F00 stack=kernel";
        var log = string.Join("\n",
        [
            markers[0], KernelAbi.Banner(root), "Build: abc | riscv64 | Debug", .. markers[1..],
            "Kernel stack: 0x0000000080201000-0x0000000080211000", "Usable memory: 100 MiB", "Counter frequency: 10000000",
            "[TEST-BEGIN] Memory.WriteCode", "[FAULT-PROBE] address=0x0000000080010000", frame, "[PANIC] Store page fault", ""
        ]);
        static WitOS.Dev.Host.ProcessResult Result(string output, int exit) => new(exit, output, "", false);
        Assert.That(BootValidation.Evaluate(root, request, Result(log, 35)).Passed, Is.True, "A reported fault rejected");
        Assert.That(BootValidation.Evaluate(root, request, Result(log, 33)).Passed, Is.False, "A success exit accepted");
        Assert.That(BootValidation.Evaluate(root, request, Result(log.Replace("cause=0x000000000000000F", "cause=0x000000000000000D"), 35)).Passed,
            Is.False, "Another cause accepted");
        Assert.That(BootValidation.Evaluate(root, request, Result(log.Replace("sstatus=0x0000000200006100", "sstatus=0x0000000200006000"), 35)).Passed,
            Is.False, "A fault from U-mode accepted");
        Assert.That(BootValidation.Evaluate(root, request, Result(log.Replace("stack=kernel", "stack=unknown"), 35)).Passed,
            Is.False, "A fault off the kernel stack accepted");
        Assert.That(BootValidation.Evaluate(root, request, Result(log.Replace("stval=0x0000000080010000", "stval=0x0000000080010008"), 35)).Passed,
            Is.False, "A fault beside the probe accepted");
        Assert.That(BootValidation.Evaluate(root, request, Result(log.Replace("[TEST-PASS] Smp.Ipi\n", ""), 35)).Passed,
            Is.False, "A missing foundation marker accepted");
    }

    #endregion
}
