namespace WitOS.Dev.Tests.Support;

/// <summary>
/// The host platforms of tests that need Windows (plan step T2.1a): NUnit skips them elsewhere and reports the reason.
/// </summary>
internal static class TestPlatforms
{
    #region Constants

    /// <summary>
    /// NUnit's name of a Windows host.
    /// </summary>
    public const string WINDOWS = "Win";

    /// <summary>
    /// The frozen line's native images and their Windows references are built by MSVC and run on Windows until K8.
    /// </summary>
    public const string MSVC = "Builds or runs the frozen line's native images with MSVC on Windows until plan step K8.";

    /// <summary>
    /// A contract of the Windows API itself: job objects, handle inheritance and command-line quoting.
    /// </summary>
    public const string WINDOWS_API = "Tests a contract of the Windows API.";

    #endregion
}
