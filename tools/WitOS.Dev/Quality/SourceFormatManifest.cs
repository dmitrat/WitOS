namespace WitOS.Dev.Quality;

/// <summary>
/// Directories formatted as native and managed code, and paths excluded from formatting.
/// </summary>
internal sealed record SourceFormatManifest(string[] Native, string[] Managed, string[] Exclude);
