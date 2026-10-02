namespace WitOS.Dev.Pe;

internal sealed record NativeModuleInfo(ulong PreferredBase, int ImageBytes, int EntryRva, int UnwindEntries,
    NativeSection[] Sections, NativeTlsInfo? Tls, Dictionary<string, int> BaseRelocations);
