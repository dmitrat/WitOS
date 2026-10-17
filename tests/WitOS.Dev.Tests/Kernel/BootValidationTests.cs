using WitOS.Dev.Kernel;

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

    // The loader suite (plan step V1.1): the port's lines in their order, the success exit and no panic, without the
    // kernel's banner or markers, which that port does not print yet.
    [Test]
    public void LoaderSuiteRequiresTheLinesInOrderAndTheExitTest()
    {
        var request = new BootRequest("loader", 256, 60, ExpectedOutcome.Success)
        {
            Suite = BootSuite.Loader,
            RequiredLines = ["[BOOT] UEFI riscv64 adapter", "[BOOT] ExitBootServices OK", "[RV64] entered the kernel"]
        };
        const string log = "OpenSBI v1.8\n[BOOT] UEFI riscv64 adapter\n[BOOT] Device tree published\n[BOOT] ExitBootServices OK\n" +
            "[RV64] entered the kernel after ExitBootServices: 20 memory regions\n";
        static WitOS.Dev.Host.ProcessResult Result(string output, int exit, bool timedOut = false) => new(exit, output, "", timedOut);
        Assert.That(BootValidation.Evaluate("", request, Result(log, 33)).Passed, Is.True, "A loader boot with its lines rejected");
        Assert.That(BootValidation.Evaluate("", request, Result(log, 35)).Passed, Is.False, "A failure exit accepted");
        Assert.That(BootValidation.Evaluate("", request, Result(log, 33, timedOut: true)).Passed, Is.False, "A timeout accepted");
        Assert.That(BootValidation.Evaluate("", request, Result(log.Replace("[RV64] entered the kernel", "[PANIC] Invalid native image headers"), 33)).Passed,
            Is.False, "A panic accepted");
        Assert.That(BootValidation.Evaluate("", request, Result("[RV64] entered the kernel\n[BOOT] UEFI riscv64 adapter\n[BOOT] ExitBootServices OK\n", 33)).Passed,
            Is.False, "Lines out of order accepted");
        Assert.That(BootValidation.Evaluate("", request with { RequiredLines = [] }, Result(log, 33)).Passed, Is.False, "A request without lines accepted");
    }

    #endregion
}
