namespace WitOS.Dev.NativeAot;

/// <summary>
/// One recorded GC compile command and its output.
/// </summary>
internal sealed record RuntimeGcPolicyCommand(string Directory, string CommandLine, string File, string Output);
