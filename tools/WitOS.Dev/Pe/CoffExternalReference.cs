namespace WitOS.Dev.Pe;

/// <summary>
/// One relocation from a COFF section to an external symbol.
/// </summary>
internal sealed record CoffExternalReference(string Target, string Section, uint Offset, string? ContainingSymbol, ushort Kind);
