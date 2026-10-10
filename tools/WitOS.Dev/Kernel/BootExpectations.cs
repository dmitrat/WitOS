using System.Text.Json;

namespace WitOS.Dev.Kernel;

/// <summary>
/// Reads the expected boot markers and fault counts from tests/Expectations; the lists live there, not in the tool.
/// </summary>
internal static class BootExpectations
{
    #region Constants

    /// <summary>
    /// x64 kernel foundation of every self-test boot.
    /// </summary>
    public const string X64_FOUNDATION = "x64-foundation";

    /// <summary>
    /// x64 user-mode suites between the isolation markers.
    /// </summary>
    public const string X64_USERS = "x64-users";

    /// <summary>
    /// ARM64 kernel foundation.
    /// </summary>
    public const string ARM64_FOUNDATION = "arm64-foundation";

    /// <summary>
    /// ARM64 isolation, thread, wait and image suites.
    /// </summary>
    public const string ARM64_USERS = "arm64-users";

    #endregion

    #region Fields

    private static readonly JsonSerializerOptions JSON = new(JsonSerializerDefaults.Web);

    #endregion

    #region Functions

    /// <summary>
    /// Directory of the expectation files.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The directory.</returns>
    public static string Directory(string root) => Path.Combine(root, "tests", "Expectations");

    /// <summary>
    /// Reads tests/Expectations/&lt;name&gt;.json.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="name">Expectation name.</param>
    /// <returns>The expectation; Required is empty when the file has none.</returns>
    /// <exception cref="InvalidDataException">The file is missing a description or markers.</exception>
    public static BootExpectation Read(string root, string name)
    {
        var path = Path.Combine(Directory(root), name + ".json");
        var expectation = JsonSerializer.Deserialize<BootExpectation>(File.ReadAllText(path), JSON);
        if (expectation is null || string.IsNullOrWhiteSpace(expectation.Description) || expectation.Markers is null ||
            expectation.Markers.Length == 0)
        {
            throw new InvalidDataException($"Invalid boot expectation: {path}");
        }
        return expectation with { Required = expectation.Required ?? [] };
    }

    #endregion
}
