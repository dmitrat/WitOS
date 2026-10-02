namespace WitOS.Dev.Pe;

internal sealed record CoffObjectInfo(int SectionCount, int SymbolRecords, CoffSectionInfo[] Sections,
    Dictionary<string, int> RelocationKinds, string[] DefinedExports, string[] UndefinedExternals,
    uint? RequiredCpuFeatures, CoffExternalReference[]? ExternalReferences);
