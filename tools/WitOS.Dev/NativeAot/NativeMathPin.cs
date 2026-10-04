namespace WitOS.Dev.NativeAot;

/// <summary>
/// Pinned upstream revision and verified source files of the native math library.
/// </summary>
internal sealed record NativeMathPin(string Repository, string Revision, string Tag, NativeMathSource[] Sources);
