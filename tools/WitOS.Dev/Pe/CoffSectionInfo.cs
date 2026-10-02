namespace WitOS.Dev.Pe;

internal sealed record CoffSectionInfo(string Name, uint Size, uint Characteristics, int Relocations);
