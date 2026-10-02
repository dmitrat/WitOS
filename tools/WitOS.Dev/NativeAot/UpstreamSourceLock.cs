namespace WitOS.Dev.NativeAot;

/// <summary>
/// Pinned upstream runtime revision, packages and source files from the runtime lock file.
/// </summary>
internal sealed record UpstreamSourceLock(int SchemaVersion, string RuntimeVersion, string RuntimeRepository,
    string RuntimeTag, string RuntimeCommit, string PackageRepository, string PackageCommit,
    string SourceManifestPath, string SourceManifestSha256, UpstreamSourceFile[] Sources);
