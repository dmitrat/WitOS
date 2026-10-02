namespace WitOS.Dev.NativeAot;

internal sealed record UpstreamSourceLock(int SchemaVersion, string RuntimeVersion, string RuntimeRepository,
    string RuntimeTag, string RuntimeCommit, string PackageRepository, string PackageCommit,
    string SourceManifestPath, string SourceManifestSha256, UpstreamSourceFile[] Sources);
