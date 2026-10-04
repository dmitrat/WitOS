namespace WitOS.Dev.Pe;

/// <summary>
/// Sections, symbols and external references of one COFF object.
/// </summary>
internal sealed record CoffObjectInfo(int SectionCount, int SymbolRecords, CoffSectionInfo[] Sections,
    Dictionary<string, int> RelocationKinds, string[] DefinedExports, string[] UndefinedExternals,
    uint? RequiredCpuFeatures, CoffExternalReference[]? ExternalReferences);
