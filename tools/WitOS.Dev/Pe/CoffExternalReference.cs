namespace WitOS.Dev.Pe;

internal sealed record CoffExternalReference(string Target, string Section, uint Offset, string? ContainingSymbol, ushort Kind);
