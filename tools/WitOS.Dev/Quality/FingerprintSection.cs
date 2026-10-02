namespace WitOS.Dev.Quality;

/// <summary>
/// Size and SHA-256 of one PE section with the debug data masked.
/// </summary>
internal sealed record FingerprintSection(string Name, int VirtualSize, int RawSize, string Sha256);
