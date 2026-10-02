using WitOS.Dev.Kernel;

namespace WitOS.Dev.Tests.Kernel;

/// <summary>
/// Boot log validation: the split between legacy and runtime user faults.
/// </summary>
[TestFixture]
public sealed class BootValidationTests
{
    #region Functions

    [Test]
    public void LegacyAndRuntimeFaultBoundariesTest()
    {
        const string fault = "[USER-FAULT] id=1 vector=14 error=0x0000000000000004 address=0x0000008000014E10 cs=0x0000000000000033\n";
        const string boundary = "[TEST-PASS] User.Isolation\n";
        Assert.That(BootValidation.ValidateUserFaults(fault + fault + boundary + fault, 2, 1), Is.True, "Valid legacy/runtime fault split rejected");
        Assert.That(!BootValidation.ValidateUserFaults(fault + boundary + fault + fault, 2, 1), Is.True, "Extra runtime fault hid missing legacy fault");
        Assert.That(!BootValidation.ValidateUserFaults(fault + fault + fault + boundary, 2, 1), Is.True, "Legacy fault hid missing runtime fault");
        Assert.That(!BootValidation.ValidateUserFaults(fault + fault + boundary + fault + "[USER-FAULT] malformed\n", 2, 1), Is.True, "Malformed fault ignored");
        Assert.That(!BootValidation.ValidateUserFaults((fault + fault + boundary + fault).Replace("0033", "0008"), 2, 1), Is.True, "Supervisor fault accepted");
    }

    #endregion
}
