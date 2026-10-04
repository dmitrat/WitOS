namespace WitOS.Dev.NativeAot;

/// <summary>
/// Hash, members and compile-unit count of one source-built native archive.
/// </summary>
internal sealed record RuntimeSourceBuildArchive(string Sha256, string[] Members, int CompileUnits);
