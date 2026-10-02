namespace WitOS.Dev.Pe;

/// <summary>
/// Static TLS template, zero fill, index slot and callbacks of one PE image.
/// </summary>
internal sealed record NativeTlsInfo(ulong TemplateBytes, uint ZeroFillBytes, uint IndexRva, uint[] CallbackRvas);
