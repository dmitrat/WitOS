namespace WitOS.Dev.NativeAot;

/// <summary>
/// One recorded native compile command of the source build and its output.
/// </summary>
internal sealed record RuntimeSourceBuildCommand(string Directory, string Command, string File, string Output);
