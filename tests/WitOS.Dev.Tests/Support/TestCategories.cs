namespace WitOS.Dev.Tests.Support;

/// <summary>
/// Categories of explicit host tests; select one with dotnet test --filter TestCategory=Name.
/// </summary>
internal static class TestCategories
{
    #region Constants

    /// <summary>
    /// Repeated QEMU timeout cleanup through QMP; needs the pinned QEMU.
    /// </summary>
    public const string QEMU_CLEANUP = "QemuCleanup";

    #endregion
}
