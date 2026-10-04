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
        "[USER-FAULT] id=1 vector=14 error=0x0000000000000004 address=0x0000008000014E10 cs=0x0000000000000033\n";

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

    #endregion
}
