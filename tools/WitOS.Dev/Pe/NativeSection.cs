namespace WitOS.Dev.Pe;

internal sealed record NativeSection(string Name, int Rva, int VirtualSize, int RawSize, string Protection);
