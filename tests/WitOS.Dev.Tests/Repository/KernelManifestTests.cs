using WitOS.Dev.Kernel;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Repository;

/// <summary>
/// Kernel build manifests: every listed file exists, every kernel C source belongs to exactly one layer, and the
/// ARM64 target reuses the architecture-independent boot layers.
/// </summary>
[TestFixture]
public sealed class KernelManifestTests
{
    #region Fields

    private static readonly string[] ARCHITECTURES = ["x64", "arm64"];

    private static readonly string[] KERNEL_SOURCE_DIRECTORIES =
    [
        "src/Boot.Uefi", "src/Kernel", "src/Kernel.Arch.X64", "src/Kernel.Arch.A64", "src/Kernel.Platform.Q35",
        "src/Kernel.Platform.QemuVirt", "tests/Kernel", "tests/Kernel.X64", "tests/Kernel.A64"
    ];

    private static readonly string[] SHARED_LAYERS = ["boot-uefi", "kernel-boot", "kernel-foundation"];

    #endregion

    #region Functions

    [Test]
    public void ManifestFilesExistTest()
    {
        var root = TestEnvironment.Root;
        foreach (var target in ARCHITECTURES.Select(architecture => KernelManifest.ReadTarget(root, architecture)))
        {
            foreach (var include in target.Includes)
            {
                Assert.That(Directory.Exists(Path.Combine(root, include)), Is.True, "Missing include directory: " + include);
            }
            foreach (var layer in KernelManifest.ReadLayers(root, target, selfTest: true))
            {
                foreach (var path in layer.Includes.Where(path => !Directory.Exists(Path.Combine(root, path))))
                {
                    Assert.Fail($"{layer.Name} names a missing include directory: {path}");
                }
                foreach (var path in layer.Sources.Concat(layer.Assembly).Where(path => !File.Exists(Path.Combine(root, path))))
                {
                    Assert.Fail($"{layer.Name} names a missing source: {path}");
                }
            }
        }
    }

    [Test]
    public void EveryKernelSourceHasOneLayerTest()
    {
        var root = TestEnvironment.Root;
        var layers = ARCHITECTURES
            .SelectMany(architecture => KernelManifest.ReadLayers(root, KernelManifest.ReadTarget(root, architecture), selfTest: true))
            .DistinctBy(layer => layer.Name)
            .ToList();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var layer in layers)
        {
            foreach (var source in layer.Sources)
            {
                Assert.That(owners.TryAdd(source, layer.Name), Is.True,
                    $"{source} is listed by both {owners.GetValueOrDefault(source)} and {layer.Name}");
            }
        }
        foreach (var directory in KERNEL_SOURCE_DIRECTORIES)
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, directory), "*.c"))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                Assert.That(owners.ContainsKey(relative), Is.True, "Kernel source without a layer: " + relative);
            }
        }
    }

    [Test]
    public void LayersKeepTheirIncludeBoundariesTest()
    {
        var root = TestEnvironment.Root;
        foreach (var target in ARCHITECTURES.Select(architecture => KernelManifest.ReadTarget(root, architecture)))
        {
            foreach (var layer in KernelManifest.ReadLayers(root, target, selfTest: false))
            {
                // Release layers reach other layers only through the target's shared include directories.
                Assert.That(layer.Includes, Is.Empty, $"{layer.Name} adds private include directories");
            }
            Assert.That(target.Includes.Count(include => include.Contains("Arch", StringComparison.Ordinal)), Is.EqualTo(1),
                "Only the architecture include directory may be shared with the common kernel");
        }
    }

    // ARM64 shares the loader, the kernel entry and the foundation services with x64; user-mode policy follows in A2.
    [Test]
    public void Arm64SharesTheFoundationLayersTest()
    {
        var root = TestEnvironment.Root;
        var x64 = KernelManifest.ReadTarget(root, "x64");
        var arm64 = KernelManifest.ReadTarget(root, "arm64");
        Assert.That(x64.Status, Is.EqualTo(KernelManifest.ACTIVE));
        Assert.That(arm64.Status, Is.EqualTo(KernelManifest.ACTIVE));
        foreach (var layer in SHARED_LAYERS)
        {
            Assert.That(x64.Layers, Does.Contain(layer));
            Assert.That(arm64.Layers, Does.Contain(layer));
        }
        Assert.That(arm64.Includes.Except(x64.Includes), Is.EqualTo(new[] { "src/Kernel.Arch.A64/include" }));
        Assert.That(x64.Layers, Does.Contain("kernel-common"));
        Assert.That(arm64.Layers, Does.Not.Contain("kernel-common"), "ARM64 links user-mode policy only from A2");
    }

    #endregion
}
