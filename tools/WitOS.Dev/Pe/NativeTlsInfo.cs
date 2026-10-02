namespace WitOS.Dev.Pe;

internal sealed record NativeTlsInfo(ulong TemplateBytes, uint ZeroFillBytes, uint IndexRva, uint[] CallbackRvas);
