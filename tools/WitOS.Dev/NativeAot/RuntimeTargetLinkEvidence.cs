namespace WitOS.Dev.NativeAot;

/// <summary>
/// Link roots, inputs, exit code and unresolved symbols of one strict link-boundary check.
/// </summary>
internal sealed record RuntimeTargetLinkEvidence(string[] Roots, string[] Inputs, int ExitCode, string[] Unresolved);
