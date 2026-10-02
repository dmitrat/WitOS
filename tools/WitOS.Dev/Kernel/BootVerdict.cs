namespace WitOS.Dev.Kernel;

/// <summary>
/// Result of classifying one boot.
/// </summary>
/// <param name="Passed">Whether the boot produced the required outcome.</param>
/// <param name="Diagnostics">Summary of the success check groups, reported on failure.</param>
internal sealed record BootVerdict(bool Passed, string Diagnostics);
