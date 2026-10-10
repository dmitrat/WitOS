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
    /// The native harnesses of the kernel and of its policy are built by MSVC and run on Windows: the policy's until plan
    /// step K8.4 removes it.
    /// </summary>
    public const string MSVC = "Builds or runs native harnesses with MSVC on Windows (the kernel policy's until plan step K8.4).";

    /// <summary>
    /// A contract of the Windows API itself: job objects, handle inheritance and command-line quoting.
    /// </summary>
    public const string WINDOWS_API = "Tests a contract of the Windows API.";

    #endregion
}
