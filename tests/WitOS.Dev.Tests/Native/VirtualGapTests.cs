using WitOS.Dev.Host;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Native;

/// <summary>
/// Virtual gap property cases of the common kernel (hosted).
/// </summary>
[TestFixture]
public sealed class VirtualGapTests
{
    #region Functions

    [Test]
    public Task VirtualGapPropertyCasesTest() => RunAsync(TestEnvironment.Root, TestEnvironment.Scratch());

    #endregion

    #region Tools

    private static async Task RunAsync(string root, string output)
    {
        var exe = await NativeHarness.BuildAsync(root, output, "virtual-gap",
            ["src/Kernel/virtual_gap.c", "tests/WitOS.Dev.Tests/Native/VirtualGap.c"], ["src/Kernel/include"]);
        var run = await Processes.RunAsync(exe, [], output, 30);
        await File.WriteAllTextAsync(Path.Combine(output, "virtual-gap.log"), run.Output + run.Error);
        if (run.TimedOut || run.ExitCode != 0 || !run.Output.StartsWith("PASS: ", StringComparison.Ordinal))
            throw new InvalidDataException("Virtual gap property cases failed: " + run.Output + run.Error);
        Console.Write(run.Output);
    }

    #endregion
}
