using WitOS.Dev.Images;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Repository;

/// <summary>
/// PAL fixture manifests: each one reads, names existing files and is built by the user image builder.
/// </summary>
[TestFixture]
public sealed class FixtureManifestTests
{
    #region Functions

    [Test]
    public void ManifestFilesExistTest()
    {
        var root = TestEnvironment.Root;
        var names = PalFixtureManifest.Names(root);
        Assert.That(names, Is.Not.Empty);
        foreach (var name in names)
        {
            var fixture = PalFixtureManifest.Read(root, name);
            var files = fixture.Sources.Concat(fixture.Assembly).Concat(fixture.Inputs);
            foreach (var path in files.Where(path => !File.Exists(Path.Combine(root, path))))
            {
                Assert.Fail($"{name} names a missing file: {path}");
            }
        }
    }

    [Test]
    public void EveryManifestIsBuiltTest()
    {
        var root = TestEnvironment.Root;
        var builder = File.ReadAllText(Path.Combine(root, "tools", "WitOS.Dev", "Images", "UserImage.cs"));
        foreach (var name in PalFixtureManifest.Names(root))
        {
            Assert.That(builder, Does.Contain($"PalFixtureImage.BuildAsync(root, output, msvc, \"{name}\")"),
                "Fixture manifest is not built: " + name);
        }
    }

    #endregion
}
