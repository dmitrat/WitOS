namespace WitOS.Dev.NativeAot;

/// <summary>
/// Recorded outcome of a native runtime source build.
/// </summary>
internal sealed record RuntimeSourceBuildResult(string Sdk, string[] Members, RuntimeSourceBuildCommand[] Commands, string ArchiveSha256, RuntimeSourceBuildArchive Minipal);
