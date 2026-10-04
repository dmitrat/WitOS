namespace WitOS.Dev.Tests.Support;

/// <summary>
/// Repository root, per-test scratch directories and the child process used by the host tests.
/// </summary>
internal static class TestEnvironment
{
    #region Constants

    private const string CHILD_ASSEMBLY = "WitOS.Dev.Tests.Child.dll";

    #endregion

    #region Fields

    private static readonly Lazy<string> RUN_DIRECTORY = new(CreateRunDirectory);

    #endregion

    #region Functions

    /// <summary>
    /// Creates the scratch directory of the current test under artifacts/q0-tests/&lt;run&gt;.
    /// </summary>
    /// <returns>Absolute directory path; the same path for repeated calls within one test.</returns>
    public static string Scratch()
    {
        var test = TestContext.CurrentContext.Test;
        var path = Path.Combine(RUN_DIRECTORY.Value, test.MethodName ?? test.Name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Builds dotnet arguments that start the child process in one mode.
    /// </summary>
    /// <param name="arguments">Mode name followed by its arguments.</param>
    /// <returns>The child assembly path followed by the arguments.</returns>
    public static string[] Child(params string[] arguments) => [ChildAssembly, .. arguments];

    #endregion

    #region Tools

    private static string CreateRunDirectory()
    {
        var path = Path.Combine(Root, "artifacts", "q0-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    #endregion

    #region Properties

    /// <summary>
    /// Repository root, five levels above the test output directory.
    /// </summary>
    public static string Root { get; } = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    /// <summary>
    /// Child process assembly copied next to the tests.
    /// </summary>
    public static string ChildAssembly { get; } = Path.Combine(AppContext.BaseDirectory, CHILD_ASSEMBLY);

    #endregion
}
