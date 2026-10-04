namespace WitOS.Dev.Quality;

/// <summary>
/// Section hashes of one native image in a build fingerprint.
/// </summary>
internal sealed record FingerprintImage(string Key, FingerprintSection[] Sections);
