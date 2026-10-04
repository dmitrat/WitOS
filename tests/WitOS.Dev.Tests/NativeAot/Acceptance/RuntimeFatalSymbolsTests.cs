using WitOS.Dev.NativeAot.Acceptance;

namespace WitOS.Dev.Tests.NativeAot.Acceptance;

/// <summary>
/// Naming the code addresses of a guest runtime fatal report from the image's linker map.
/// </summary>
[TestFixture]
public sealed class RuntimeFatalSymbolsTests
{
    #region Constants

    private const string MAP = """
         WitOS.NativeAotBoot

         Preferred load address is 0000000140000000

          Address         Publics by Value              Rva+Base               Lib:Object

         0000:00000000       __AbsoluteZero             0000000000000000     <absolute>
         0001:00042bf0       ?mark_object_simple@gc_heap@WKS@@CAXPEAPEAE@Z 0000000140043bf0 f   Runtime.WorkstationGC:gcwks.witos.cpp.obj
         0001:00043000       ?mark_object_simple1@gc_heap@WKS@@AEAAXPEAE0@Z 0000000140044000 f   Runtime.WorkstationGC:gcwks.witos.cpp.obj
         0002:00000010       ?data@@3HA                 0000000140090010     data.obj

         Static symbols

         0001:000027b0       wit_runtime_invalid_instruction 00000001400037b0 f   runtime_fault_fixture.obj
        """;

    #endregion

    #region Functions

    [Test]
    public void FaultAndStackNameTheirFunctionsTest()
    {
        const string serial = "Runtime boot state/exit/rip/owned: 3/0x00000000C0000005/0x0000000000000000/488\n" +
            "Runtime fatal image base/size/rip RVA: 0x0000008000100000 0x00000000000F4000 0x0000000000043C92\n" +
            "Runtime fatal stack RVAs: 0x0000000000044010 0x00000000000037B0 0x0000000000000400\n";
        var lines = RuntimeFatalSymbols.Describe(serial, MAP);
        Assert.That(lines, Is.EqualTo(new[]
        {
            "Runtime fatal symbol rip: 0x43C92 ?mark_object_simple@gc_heap@WKS@@CAXPEAPEAE@Z+0xA2 " +
                "(Runtime.WorkstationGC:gcwks.witos.cpp.obj)",
            "Runtime fatal symbol stack: 0x44010 ?mark_object_simple1@gc_heap@WKS@@AEAAXPEAE0@Z+0x10 " +
                "(Runtime.WorkstationGC:gcwks.witos.cpp.obj)",
            "Runtime fatal symbol stack: 0x37B0 wit_runtime_invalid_instruction+0x0 (runtime_fault_fixture.obj)",
            "Runtime fatal symbol stack: 0x400 (no symbol)"
        }));
    }

    [Test]
    public void LogWithoutFatalReportNamesNothingTest()
    {
        Assert.That(RuntimeFatalSymbols.Describe("PASS: runtime-boot-128\n", MAP), Is.Empty);
        Assert.Throws<InvalidDataException>(() => RuntimeFatalSymbols.Describe(
            "Runtime fatal image base/size/rip RVA: 0x0000008000100000 0x0000000000001000 0x0000000000000010\n", "no map"));
    }

    #endregion
}
