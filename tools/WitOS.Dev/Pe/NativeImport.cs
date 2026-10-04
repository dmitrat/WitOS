namespace WitOS.Dev.Pe;

/// <summary>
/// Symbols that a PE image imports from one library.
/// </summary>
internal sealed record NativeImport(string Library, string[] Symbols);
