namespace WitOS.Dev.Images;

/// <summary>
/// One pinned microsoft/STL file and the SHA-256 of its canonical bytes.
/// </summary>
internal sealed record StlFile(string Path, string Sha256);
