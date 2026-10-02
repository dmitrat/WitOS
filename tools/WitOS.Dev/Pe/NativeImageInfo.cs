namespace WitOS.Dev.Pe;

internal sealed record NativeImageInfo(string Machine, string Subsystem, bool HasClrHeader, int DelayImportDirectorySize, NativeImport[] DirectImports, NativeImport[] DelayImports);
