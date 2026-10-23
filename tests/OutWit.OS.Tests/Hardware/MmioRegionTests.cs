using System.Linq;
using System.Reflection;
using OutWit.OS.Hardware;

namespace OutWit.OS.Tests.Hardware;

/// <summary>
/// A device register's access keeps its width (plan step H2.1): no read or write of <see cref="MmioRegion"/> may be
/// inlined, since the x64 JIT narrows an inlined volatile load tested against a byte's mask to a one-byte read, which
/// QEMU's xHCI answers with zero.
/// </summary>
[TestFixture]
public sealed class MmioRegionTests
{
    #region Tests

    [Test]
    public void NoRegisterAccessIsInlinedTest()
    {
        var accesses = typeof(MmioRegion).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.Name.StartsWith("Read") || method.Name.StartsWith("Write"))
            .ToList();
        Assert.That(accesses.Select(method => method.Name),
            Is.EquivalentTo(new[] { "Read8", "Read16", "Read32", "Write8", "Write16", "Write32", "Write64" }));
        foreach (var method in accesses)
        {
            Assert.That(method.MethodImplementationFlags.HasFlag(MethodImplAttributes.NoInlining), Is.True,
                $"{method.Name} may be inlined and its access narrowed.");
        }
    }

    #endregion
}
