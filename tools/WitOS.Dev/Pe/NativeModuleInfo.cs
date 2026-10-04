namespace WitOS.Dev.Pe;

/// <summary>
/// Layout, sections, unwind and TLS facts of one PE image.
/// </summary>
internal sealed record NativeModuleInfo(ulong PreferredBase, int ImageBytes, int EntryRva, int UnwindEntries,
    NativeSection[] Sections, NativeTlsInfo? Tls, Dictionary<string, int> BaseRelocations);
