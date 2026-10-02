namespace WitOS.Dev.NativeAot;

internal sealed record RuntimeTargetLinkEvidence(string[] Roots, string[] Inputs, int ExitCode, string[] Unresolved);
