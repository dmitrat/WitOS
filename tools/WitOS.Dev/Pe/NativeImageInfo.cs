namespace WitOS.Dev.Pe;

/// <summary>
/// Machine, subsystem and imports of one PE image.
/// </summary>
internal sealed record NativeImageInfo(string Machine, string Subsystem, bool HasClrHeader, int DelayImportDirectorySize, NativeImport[] DirectImports, NativeImport[] DelayImports);
