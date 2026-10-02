namespace WitOS.Dev.Quality;

internal sealed record FingerprintReport(string BuildId, string[] Scenarios, FingerprintImage[] Images);
