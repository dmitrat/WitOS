using WitOS.Dev.NativeAot;

namespace WitOS.Dev.Tests.NativeAot;

/// <summary>
/// Compile profile extraction from recorded compiler command lines.
/// </summary>
[TestFixture]
public sealed class RuntimeGuestDriverTests
{
    #region Functions

    [Test]
    public void QuotedCompileProfileTest()
    {
        const string input = "cl.exe -DNO_STRESS_LOG -DFEATURE_SUSPEND_REDIRECTION -I\"C:\\Workspace With Spaces\\src\" -DNAME=\\\"value\\\"";
        var parsed = RuntimeGuestDriver.CompileProfile(input);
        Assert.That(parsed.Contains("-IC:\\Workspace With Spaces\\src"), Is.True, "Quoted include truncated");
        Assert.That(parsed.Contains("-DNAME=\"value\""), Is.True, "Escaped define quotes changed");
    }

    #endregion
}
