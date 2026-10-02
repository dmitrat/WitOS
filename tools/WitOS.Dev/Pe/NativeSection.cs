namespace WitOS.Dev.Pe;

/// <summary>
/// Placement and protection of one PE section.
/// </summary>
internal sealed record NativeSection(string Name, int Rva, int VirtualSize, int RawSize, string Protection);
