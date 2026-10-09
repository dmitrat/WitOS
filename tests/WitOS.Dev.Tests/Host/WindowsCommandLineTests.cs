using WitOS.Dev.Host;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Host;

/// <summary>
/// Windows command-line quoting and parsing round trips.
/// </summary>
[TestFixture]
[Platform(Include = TestPlatforms.WINDOWS, Reason = TestPlatforms.WINDOWS_API)]
public sealed class WindowsCommandLineTests
{
    #region Functions

    [Test]
    public void ArgumentRoundtripTest()
    {
        string[] values = ["cl.exe", "", "-IC:\\with spaces\\", "-DNAME=\"two words\"", "\\\"", "a\"\"b", "tab\tvalue"];
        Assert.That(WindowsCommandLine.Parse(string.Join(' ', values.Select(WindowsCommandLine.Quote))).SequenceEqual(values), Is.True, "Quote/parse mismatch");
    }

    #endregion
}
