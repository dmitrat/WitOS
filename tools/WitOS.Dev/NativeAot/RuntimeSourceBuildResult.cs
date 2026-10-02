namespace WitOS.Dev.NativeAot;

internal sealed record RuntimeSourceBuildResult(string Sdk, string[] Members, RuntimeSourceBuildCommand[] Commands, string ArchiveSha256, RuntimeSourceBuildArchive Minipal);
