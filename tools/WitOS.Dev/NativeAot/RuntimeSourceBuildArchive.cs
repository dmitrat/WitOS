namespace WitOS.Dev.NativeAot;

internal sealed record RuntimeSourceBuildArchive(string Sha256, string[] Members, int CompileUnits);
