using System.Text.RegularExpressions;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Repository;

/// <summary>
/// Kernel layering: policy reaches the processor only through witos/arch.h, and the common kernel includes no
/// architecture headers.
/// </summary>
[TestFixture]
public sealed class KernelLayeringTests
{
    #region Constants

    private const string ARCH_DIRECTORY = "src/Kernel.Arch.X64";

    #endregion

    #region Fields

    // Policy that Q2.5 moves into src/Kernel; until then it lives next to the x64 code.
    private static readonly string[] POLICY_FILES =
    [
        "user.c", "user_apc.c", "user_code.c", "user_console.c", "user_exception.c", "user_files.c", "user_image.c",
        "user_library.c", "user_library_lifecycle.c", "user_library_readers.c", "user_library_tls.c", "user_objects.c",
        "user_pressure.c", "user_reference.c", "user_stack_lease.c", "user_suspend.c", "user_thread.c",
        "user_thread_context.c", "user_thread_name.c", "user_wait.c"
    ];

    private static readonly Regex ARCHITECTURE_NAME = new(
        @"\bwit_x64_\w+|\b(?:R[a-d]x|R[sd]i|Rbp|Rsp|Rip|R(?:8|9|1[0-5])|Rflags|RawRflags|Cs|Ss|FxState)\b|" +
        @"\b(?:__readmsr|__writemsr|__readcr\d|__writecr\d|_enable|_disable|__halt|__cpuid|__outbyte|__inbyte)\b|" +
        @"\b(?:WIT_USER_CS|WIT_USER_SS|WitInterruptContext|WitExceptionFrame)\b|""x64\.h""");

    private static readonly Regex ARCHITECTURE_INCLUDE = new(
        @"#include\s+""(?:[^""]*Arch[^""]*|x64\.h|user\.h|user_layout\.h|cpu_cache\.h|witos/arch_types\.h)""");

    #endregion

    #region Functions

    [Test]
    public void PolicyUsesArchitectureInterfaceTest()
    {
        foreach (var name in POLICY_FILES)
        {
            var path = Path.Combine(TestEnvironment.Root, ARCH_DIRECTORY, name);
            Assert.That(File.Exists(path), Is.True, "Policy file is missing: " + name);
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; ++i)
            {
                var match = ARCHITECTURE_NAME.Match(lines[i]);
                Assert.That(match.Success, Is.False, $"{name}:{i + 1} names the architecture: {match.Value}");
            }
        }
    }

    [Test]
    public void CommonKernelIncludesNoArchitectureTest()
    {
        var common = Path.Combine(TestEnvironment.Root, "src", "Kernel");
        var files = Directory.EnumerateFiles(common, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".c", StringComparison.Ordinal) || path.EndsWith(".h", StringComparison.Ordinal))
            .ToList();
        Assert.That(files, Is.Not.Empty);
        foreach (var path in files)
        {
            var match = ARCHITECTURE_INCLUDE.Match(File.ReadAllText(path));
            Assert.That(match.Success, Is.False,
                $"{Path.GetRelativePath(TestEnvironment.Root, path)} includes an architecture header: {match.Value}");
        }
    }

    #endregion
}
