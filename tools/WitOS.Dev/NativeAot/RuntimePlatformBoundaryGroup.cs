namespace WitOS.Dev.NativeAot;

/// <summary>
/// One group of unresolved platform symbols with its plan item and porting decision.
/// </summary>
internal sealed record RuntimePlatformBoundaryGroup(string Id, string Plan, string Decision, string Symbols);
