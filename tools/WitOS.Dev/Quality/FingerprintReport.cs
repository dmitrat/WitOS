namespace WitOS.Dev.Quality;

/// <summary>
/// Section hashes of every native image produced by the fingerprinted scenarios.
/// </summary>
internal sealed record FingerprintReport(string BuildId, string[] Scenarios, FingerprintImage[] Images);
