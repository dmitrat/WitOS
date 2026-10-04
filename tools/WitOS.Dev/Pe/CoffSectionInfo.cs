namespace WitOS.Dev.Pe;

/// <summary>
/// Name, size, characteristics and relocation count of one COFF section.
/// </summary>
internal sealed record CoffSectionInfo(string Name, uint Size, uint Characteristics, int Relocations);
