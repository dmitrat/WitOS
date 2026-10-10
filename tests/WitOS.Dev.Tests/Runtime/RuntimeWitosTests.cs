using WitOS.Dev.Runtime;
using WitOS.Dev.Tests.Support;
using WitOS.Dev.Upstream;

namespace WitOS.Dev.Tests.Runtime;

/// <summary>
/// The witos patch set of the Unix-form runtime (plan step R1.1): one patch per pinned upstream file, named by its path,
/// made from the pinned bytes, and measured by area against the FreeBSD and Haiku ports; and the try_run measurement of
/// the native configure (plan step R1.2b), which pairs each probe CMake kept with its variable and reads the guest's
/// answers.
/// </summary>
[TestFixture]
public sealed class RuntimeWitosTests
{
    #region Functions

    // The tool's list and the pin name the same files, and every patch is the file's own.
    [Test]
    public void PatchSetMatchesThePinTest()
    {
        var root = TestEnvironment.Root;
        var pin = RuntimeWitos.ReadLock(root);
        Assert.That(pin.Sources.Select(source => source.Path).Order(StringComparer.Ordinal),
            Is.EqualTo(RuntimeWitos.PATCHES.Keys.Order(StringComparer.Ordinal)));
        foreach (var (path, sha256) in pin.Sources)
        {
            var output = RuntimeWitos.PATCHES[path];
            Assert.That(output, Is.EqualTo(path.Replace('/', '.')), $"{path}: a patch is named by its path");
            var patch = UpstreamPatches.Read(root, "runtime", output);
            Assert.That(patch.Source, Is.EqualTo(path));
            Assert.That(patch.Before, Is.EqualTo(sha256), $"{path}: the patch is not made from the pinned bytes");
        }
    }

    // The pin is a commit of a tag, and the budget is the ports' size the plan records.
    [Test]
    public void PinNamesTheReleaseAndTheBudgetTest()
    {
        var pin = RuntimeWitos.ReadLock(TestEnvironment.Root);
        Assert.That(pin.Repository, Is.EqualTo("https://github.com/dotnet/runtime"));
        Assert.That(pin.Tag, Is.EqualTo("v" + pin.Version));
        Assert.That(pin.Commit, Does.Match("^[0-9a-f]{40}$"));
        Assert.That(pin.Budget, Is.EqualTo(new RuntimeBudget(31, 62, 18, 11)));
    }

    [Test]
    public void AreasFollowTheBudgetTest()
    {
        Assert.That(RuntimeWitos.Area("src/coreclr/pal/src/thread/process.cpp"), Is.EqualTo("coreclr"));
        Assert.That(RuntimeWitos.Area("src/native/libs/System.Native/pal_process.c"), Is.EqualTo("nativeLibraries"));
        Assert.That(RuntimeWitos.Area("src/native/corehost/hostmisc/pal.unix.cpp"), Is.EqualTo("hosts"));
        Assert.That(RuntimeWitos.Area("src/libraries/System.Private.CoreLib/src/System/OperatingSystem.cs"), Is.EqualTo("libraries"));
        Assert.That(RuntimeWitos.Area("eng/build.sh"), Is.EqualTo("build"));
    }

    // TryRunResults.cmake as CMake 3.28 writes it: a comment block naming the executable, then the set() of its variable.
    [Test]
    public void TryRunResultsPairEachProbeWithItsExecutableTest()
    {
        const string results = """
            # HAVE_CLOCK_MONOTONIC_EXITCODE
            #    indicates whether the executable would have been able to run on its
            #    target platform. If so, set HAVE_CLOCK_MONOTONIC_EXITCODE to
            #    the exit code (in many cases 0 for success), otherwise enter "FAILED_TO_RUN".
            # Source file   : /obj/CMakeFiles/CMakeScratch/TryCompile-abc/src.c
            # Executable    : /obj/CMakeFiles/cmTC_e8754-HAVE_CLOCK_MONOTONIC_EXITCODE
            # Run arguments :
            #    Called from: [3]     /usr/share/cmake-3.28/Modules/Internal/CheckSourceRuns.cmake

            set( HAVE_CLOCK_MONOTONIC_EXITCODE
                 "PLEASE_FILL_OUT-FAILED_TO_RUN"
                 CACHE STRING "Result from try_run" FORCE)

            # Executable    : /obj/CMakeFiles/cmTC_20042-HAS_POSIX_SEMAPHORES_EXITCODE
            set( HAS_POSIX_SEMAPHORES_EXITCODE
                 "PLEASE_FILL_OUT-FAILED_TO_RUN"
                 CACHE STRING "Result from try_run" FORCE)
            """;
        Assert.That(RuntimeWitos.ParseTryRunResults(results), Is.EqualTo(new[]
        {
            ("HAVE_CLOCK_MONOTONIC_EXITCODE", "/obj/CMakeFiles/cmTC_e8754-HAVE_CLOCK_MONOTONIC_EXITCODE"),
            ("HAS_POSIX_SEMAPHORES_EXITCODE", "/obj/CMakeFiles/cmTC_20042-HAS_POSIX_SEMAPHORES_EXITCODE")
        }));
        Assert.Throws<InvalidDataException>(() => RuntimeWitos.ParseTryRunResults(
            "# Executable    : /obj/CMakeFiles/cmTC_1-OTHER_EXITCODE\nset( HAVE_CLOCK_MONOTONIC_EXITCODE\n"));
        Assert.Throws<InvalidDataException>(() => RuntimeWitos.ParseTryRunResults("set( HAVE_CLOCK_MONOTONIC_EXITCODE\n"));
    }

    // A probe that exited answers its status, one that a signal ended FAILED_TO_RUN, and other lines are not answers.
    [Test]
    public void GuestAnswersFollowHowEachProbeEndedTest()
    {
        var answers = RuntimeWitos.ParseGuestAnswers(
        [
            "[USER] [ROOT-TASK] starting /bin/init", "[USER] [TRYRUN] HAVE_CLOCK_MONOTONIC_EXITCODE exit 0",
            "[TRYRUN] HAS_POSIX_SEMAPHORES_EXITCODE exit 1", "[TRYRUN] HAVE_PROCFS_CTL_EXITCODE signal 11",
            "[TRYRUN] 3 probes on x86_64: 3 ran"
        ]);
        Assert.That(answers, Is.EqualTo(new Dictionary<string, string>
        {
            ["HAVE_CLOCK_MONOTONIC_EXITCODE"] = "0",
            ["HAS_POSIX_SEMAPHORES_EXITCODE"] = "1",
            ["HAVE_PROCFS_CTL_EXITCODE"] = "FAILED_TO_RUN"
        }));
        Assert.Throws<InvalidDataException>(() => RuntimeWitos.ParseGuestAnswers(
            ["[TRYRUN] HAVE_CLOCK_MONOTONIC_EXITCODE exit 0", "[TRYRUN] HAVE_CLOCK_MONOTONIC_EXITCODE exit 1"]));
    }

    #endregion
}
