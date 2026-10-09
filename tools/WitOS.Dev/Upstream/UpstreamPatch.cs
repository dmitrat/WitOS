namespace WitOS.Dev.Upstream;

/// <summary>
/// A WitOS change to one pinned upstream file: the upstream path, the output file name, the SHA-256 of the LF text
/// before and after the change, why it exists and the unified-diff hunks.
/// </summary>
internal sealed record UpstreamPatch(
    string Source, string Output, string Before, string After, string Purpose, UpstreamPatchHunk[] Hunks);
