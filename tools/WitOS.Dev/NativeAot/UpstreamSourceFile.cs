namespace WitOS.Dev.NativeAot;

/// <summary>
/// One pinned upstream source file and its SHA-256 over the canonical download bytes.
/// </summary>
internal sealed record UpstreamSourceFile(string Path, string Sha256);
