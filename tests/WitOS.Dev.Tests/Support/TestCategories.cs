namespace WitOS.Dev.Tests.Support;

/// <summary>
/// Categories of explicit host tests; select one with dotnet test --filter TestCategory=Name.
/// </summary>
internal static class TestCategories
{
    #region Constants

    /// <summary>
    /// Hosted PE corpus and virtual gap cases; need the runtime-source image.
    /// </summary>
    public const string PE = "Pe";

    /// <summary>
    /// PE corpus with LLVM branch coverage and AddressSanitizer.
    /// </summary>
    public const string PE_COVERAGE = "PeCoverage";

    /// <summary>
    /// Bounded libFuzzer/ASan smoke run of the PE parsers.
    /// </summary>
    public const string PE_FUZZ = "PeFuzz";

    /// <summary>
    /// Import descriptor cases built with AddressSanitizer.
    /// </summary>
    public const string PE_IMPORTS_ASAN = "PeImportsAsan";

    /// <summary>
    /// Repeated QEMU timeout cleanup through QMP; needs the pinned QEMU.
    /// </summary>
    public const string QEMU_CLEANUP = "QemuCleanup";

    #endregion
}
