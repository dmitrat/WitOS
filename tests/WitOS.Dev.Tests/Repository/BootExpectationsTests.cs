using System.Reflection;
using System.Text.RegularExpressions;
using WitOS.Dev.Kernel;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Repository;

/// <summary>
/// Boot expectations in tests/Expectations: each file is one the tool reads, its markers are unique, and every
/// expected marker is emitted somewhere in the guest sources.
/// </summary>
[TestFixture]
public sealed class BootExpectationsTests
{
    #region Fields

    private static readonly string[] SOURCE_DIRECTORIES = ["src", "tests"];

    private static readonly string[] SOURCE_EXTENSIONS = [".c", ".cpp", ".h", ".asm"];

    private static readonly Regex TEST_MARKER = new(@"^\[TEST-PASS\] (?<area>[A-Za-z0-9]+)\.(?<name>[A-Za-z0-9]+)$");

    #endregion

    #region Functions

    // The tool names exactly the files that exist; none is unread and none is missing.
    [Test]
    public void ExpectationFilesMatchTheToolTest()
    {
        var root = TestEnvironment.Root;
        var named = typeof(BootExpectations).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .OrderBy(name => name, StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(BootExpectations.Directory(root), "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(name => name, StringComparer.Ordinal);
        Assert.That(files, Is.EqualTo(named));
    }

    [Test]
    public void ExpectationsAreWellFormedTest()
    {
        var root = TestEnvironment.Root;
        foreach (var file in Directory.EnumerateFiles(BootExpectations.Directory(root), "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var expectation = BootExpectations.Read(root, name);
            var markers = expectation.Markers.Concat(expectation.Required).ToList();
            Assert.That(markers, Is.Unique, $"{name} repeats a marker");
            // A trailing space is meaningful: "Free physical pages: " matches the line before its number.
            Assert.That(markers.All(marker => marker.Length > 0 && !char.IsWhiteSpace(marker[0])), Is.True,
                $"{name} has an empty or indented marker");
        }
    }

    // A test marker "[TEST-PASS] Area.Name" is emitted literally, or its name comes from a fault or suite table;
    // any other marker appears literally.
    [Test]
    public void ExpectedMarkersAreEmittedTest()
    {
        var root = TestEnvironment.Root;
        var sources = string.Join("\n", SOURCE_DIRECTORIES
            .SelectMany(directory => Directory.EnumerateFiles(Path.Combine(root, directory), "*", SearchOption.AllDirectories))
            .Where(path => SOURCE_EXTENSIONS.Contains(Path.GetExtension(path)))
            .Select(File.ReadAllText));
        foreach (var file in Directory.EnumerateFiles(BootExpectations.Directory(root), "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var expectation = BootExpectations.Read(root, name);
            foreach (var marker in expectation.Markers.Concat(expectation.Required))
            {
                var test = TEST_MARKER.Match(marker);
                var emitted = test.Success
                    ? sources.Contains($"{test.Groups["area"].Value}.{test.Groups["name"].Value}", StringComparison.Ordinal) ||
                        sources.Contains($"\"{test.Groups["name"].Value}\"", StringComparison.Ordinal)
                    : sources.Contains(marker, StringComparison.Ordinal);
                Assert.That(emitted, Is.True, $"{name} expects {marker}, which no guest source emits");
            }
        }
    }

    #endregion
}
