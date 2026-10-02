using System.Text.RegularExpressions;
using WitOS.Dev.Kernel;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Repository;

/// <summary>
/// Kernel layering: the common kernel reaches the processor only through witos/arch.h, includes no architecture
/// headers, and the policy no longer lives in the architecture directory.
/// </summary>
[TestFixture]
public sealed class KernelLayeringTests
{
    #region Constants

    private const string ARCH_DIRECTORY = "src/Kernel.Arch.X64";

    private const string COMMON_DIRECTORY = "src/Kernel";

    // The single header that embeds architecture types the common kernel does not read.
    private const string ARCH_TYPES_OWNER = "user.h";

    #endregion

    #region Fields

    // Policy moved out of the architecture directory in Q2.5, including the address-space accounting.
    private static readonly string[] POLICY_FILES =
    [
        "user.c", "user_apc.c", "user_code.c", "user_console.c", "user_exception.c", "user_files.c", "user_image.c",
        "user_library.c", "user_library_lifecycle.c", "user_library_readers.c", "user_library_tls.c", "user_objects.c",
        "user_pressure.c", "user_reference.c", "user_space.c", "user_stack_lease.c", "user_suspend.c", "user_thread.c",
        "user_thread_context.c", "user_thread_name.c", "user_wait.c", "user.h"
    ];

    private static readonly Regex ARCHITECTURE_NAME = new(
        @"\bwit_x64_\w+|\b(?:R[a-d]x|R[sd]i|Rbp|Rsp|Rip|R(?:8|9|1[0-5])|Rflags|RawRflags|Cs|Ss|FxState)\b|" +
        @"\b(?:__readmsr|__writemsr|__readcr\d|__writecr\d|_enable|_disable|__halt|__cpuid|__outbyte|__inbyte)\b|" +
        @"\b(?:WIT_USER_CS|WIT_USER_SS|WitInterruptContext|WitExceptionFrame)\b");

    private static readonly Regex ARCHITECTURE_INCLUDE = new(
        @"#include\s+""(?:[^""]*Arch[^""]*|[^""]*Platform\.[^""]*|x64\.h|q35\.h|cpu_cache\.h|minipal_cpu[^""]*)""");

    // Board devices of q35: port I/O, the UART, the HPET aperture and the QEMU exit port.
    private static readonly Regex BOARD_DEVICE = new(
        @"\b(?:__outbyte|__inbyte|__outword|__inword|__outdword|__indword|HPET|COM1)\b|0x3F8|0xFED00000|^\s+(?:out|in)\s",
        RegexOptions.IgnoreCase);

    private static readonly Regex ARCH_TYPES_INCLUDE = new(@"#include\s+""witos/arch_types\.h""");

    #endregion

    #region Functions

    [Test]
    public void PolicyLivesInCommonKernelTest()
    {
        foreach (var name in POLICY_FILES)
        {
            Assert.That(File.Exists(Path.Combine(TestEnvironment.Root, COMMON_DIRECTORY, name)), Is.True,
                "Policy file is missing from the common kernel: " + name);
            Assert.That(File.Exists(Path.Combine(TestEnvironment.Root, ARCH_DIRECTORY, name)), Is.False,
                "Policy file is back in the architecture directory: " + name);
        }
    }

    // ABI headers in include/witos describe per-ISA user layouts and are checked by the ABI reference instead.
    [Test]
    public void CommonKernelNamesNoArchitectureTest()
    {
        var common = Path.Combine(TestEnvironment.Root, COMMON_DIRECTORY);
        var files = Directory.EnumerateFiles(common, "*.c").Concat(Directory.EnumerateFiles(common, "*.h")).ToList();
        Assert.That(files, Is.Not.Empty);
        foreach (var path in files)
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; ++i)
            {
                var match = ARCHITECTURE_NAME.Match(lines[i]);
                Assert.That(match.Success, Is.False,
                    $"{Path.GetFileName(path)}:{i + 1} names the architecture: {match.Value}");
            }
        }
    }

    [Test]
    public void CommonKernelIncludesNoArchitectureTest()
    {
        var common = Path.Combine(TestEnvironment.Root, COMMON_DIRECTORY);
        var files = Directory.EnumerateFiles(common, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".c", StringComparison.Ordinal) || path.EndsWith(".h", StringComparison.Ordinal))
            .ToList();
        Assert.That(files, Is.Not.Empty);
        foreach (var path in files)
        {
            var text = File.ReadAllText(path);
            var relative = Path.GetRelativePath(TestEnvironment.Root, path);
            var match = ARCHITECTURE_INCLUDE.Match(text);
            Assert.That(match.Success, Is.False, $"{relative} includes an architecture header: {match.Value}");
            if (Path.GetFileName(path) != ARCH_TYPES_OWNER)
            {
                Assert.That(ARCH_TYPES_INCLUDE.IsMatch(text), Is.False,
                    $"{relative} includes witos/arch_types.h; only {ARCH_TYPES_OWNER} embeds architecture types");
            }
        }
    }

    [Test]
    public void ArchitectureHasNoBoardDevicesTest()
    {
        var architecture = Path.Combine(TestEnvironment.Root, ARCH_DIRECTORY);
        var files = Directory.EnumerateFiles(architecture)
            .Where(path => path.EndsWith(".c", StringComparison.Ordinal) || path.EndsWith(".h", StringComparison.Ordinal) ||
                path.EndsWith(".cpp", StringComparison.Ordinal) || path.EndsWith(".asm", StringComparison.Ordinal))
            .ToList();
        Assert.That(files, Is.Not.Empty);
        foreach (var path in files)
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; ++i)
            {
                var match = BOARD_DEVICE.Match(lines[i]);
                Assert.That(match.Success, Is.False, $"{Path.GetFileName(path)}:{i + 1} drives a board device: {match.Value}");
            }
        }
    }

    // User-mode bindings, runtime helpers and fixtures left in Q2.8: every source here links into the kernel.
    [Test]
    public void ArchitectureHoldsOnlyKernelSourcesTest()
    {
        var layer = KernelManifest.ReadLayer(TestEnvironment.Root, "kernel-arch-x64");
        var linked = layer.Sources.Concat(layer.Assembly).ToHashSet(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(TestEnvironment.Root, ARCH_DIRECTORY)))
        {
            var relative = Path.GetRelativePath(TestEnvironment.Root, path).Replace('\\', '/');
            if (relative.EndsWith(".h", StringComparison.Ordinal))
            {
                continue;
            }
            Assert.That(linked, Does.Contain(relative), "Architecture file is not linked into the kernel: " + relative);
        }
    }

    // Kernel self-tests live in tests/Kernel.X64 and link only into WITOS_SELFTEST kernels.
    [Test]
    public void SelfTestsLiveOutsideKernelSourcesTest()
    {
        var kernelSources = Directory.EnumerateFiles(Path.Combine(TestEnvironment.Root, "src"), "*_tests.c", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(TestEnvironment.Root, path))
            .ToList();
        Assert.That(kernelSources, Is.Empty, "Self-test sources inside src");
        Assert.That(Directory.EnumerateFiles(Path.Combine(TestEnvironment.Root, "tests", "Kernel.X64"), "*_tests.c"), Is.Not.Empty);
    }

    #endregion
}
