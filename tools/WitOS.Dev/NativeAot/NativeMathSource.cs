namespace WitOS.Dev.NativeAot;

/// <summary>
/// One pinned native math source file and its SHA-256.
/// </summary>
internal sealed record NativeMathSource(string Path, string Sha256);
