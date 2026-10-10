namespace WitOS.Dev.Upstream;

/// <summary>
/// One hunk of an upstream patch: 1-based start lines, line counts and the lines with their ' ', '-' or '+' prefix.
/// </summary>
internal sealed record UpstreamPatchHunk(int OldStart, int OldCount, int NewStart, int NewCount, string[] Lines);
