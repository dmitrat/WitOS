using System.Text.Json;

namespace WitOS.Dev.Images;

/// <summary>
/// Reads PAL fixture manifests from build/fixtures; their source lists live there, not in the tool.
/// </summary>
internal static class PalFixtureManifest
{
    #region Fields

    private static readonly JsonSerializerOptions JSON = new(JsonSerializerDefaults.Web);

    #endregion

    #region Functions

    /// <summary>
    /// Reads build/fixtures/&lt;name&gt;.json.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="name">Fixture name.</param>
    /// <returns>The fixture.</returns>
    /// <exception cref="InvalidDataException">The manifest is missing required fields.</exception>
    public static PalFixture Read(string root, string name)
    {
        var path = Path.Combine(root, "build", "fixtures", name + ".json");
        var fixture = JsonSerializer.Deserialize<PalFixture>(File.ReadAllText(path), JSON);
        if (fixture is null || fixture.Name != name || string.IsNullOrEmpty(fixture.Image) ||
            string.IsNullOrEmpty(fixture.ObjectPrefix) || fixture.Defines is null || fixture.Assembly is null ||
            fixture.Sources is not { Length: > 0 } || fixture.FunctionSections is null || fixture.Shared is null ||
            string.IsNullOrEmpty(fixture.Header) || string.IsNullOrEmpty(fixture.Symbol) || string.IsNullOrEmpty(fixture.Report) ||
            fixture.Inputs is null || fixture.FunctionSections.Except(fixture.Sources).Any())
        {
            throw new InvalidDataException($"Invalid PAL fixture manifest: {path}");
        }
        return fixture;
    }

    /// <summary>
    /// Names of all PAL fixture manifests.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The names, sorted.</returns>
    public static IReadOnlyList<string> Names(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "build", "fixtures"), "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal)
            .ToList()!;

    #endregion
}
