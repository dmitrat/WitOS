using WitOS.Dev.Substrate;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Substrate;

/// <summary>
/// ABI-2 (1.0 at plan step N3.3, 1.1 since V2.3 with riscv64's names; RFC 0011 section 10.2): the record names the libraries a runtime links and libc.so's
/// exported names on each ISA, sorted, without repeats and without the system layer's private names; the C library's own
/// interface is there.
/// </summary>
[TestFixture]
public sealed class SystemLayerAbiTests
{
    #region Functions

    [Test]
    public void RecordIsTheFrozenLibraryInterfaceTest()
    {
        var record = SystemLayerAbi.Read(TestEnvironment.Root);
        Assert.That(record.Version, Does.Match(@"^1\.\d+$"));
        Assert.That(record.Libraries, Is.EqualTo(new[] { "libc.so", "libc++.so.1", "libc++abi.so.1", "libunwind.so.1" }));
        Assert.That(record.Libc.Keys, Is.EquivalentTo(new[] { "x64", "arm64", "riscv64" }), "every ISA with a system layer (riscv64 since V2.3, ABI-2 1.1)");
        foreach (var (isa, names) in record.Libc)
        {
            Assert.That(names, Is.Ordered.Using((IComparer<string>)StringComparer.Ordinal).And.Unique, $"{isa}: sorted, once each");
            Assert.That(names, Has.None.StartsWith(SystemLayerAbi.PRIVATE_PREFIX), $"{isa}: the system layer's private names stay out");
            Assert.That(names, Is.SupersetOf(new[] { "malloc", "pthread_create", "dlopen", "posix_spawn", "__cxa_finalize", "membarrier" }),
                $"{isa}: the C library's interface");
        }
    }

    #endregion
}
