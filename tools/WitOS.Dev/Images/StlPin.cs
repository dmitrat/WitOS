namespace WitOS.Dev.Images;

/// <summary>
/// Pinned microsoft/STL tag and commit and the verified files the guest C++ runtime uses.
/// </summary>
internal sealed record StlPin(string Repository, string Tag, string Revision, string License, StlFile[] Files);
