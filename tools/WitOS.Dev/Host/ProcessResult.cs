namespace WitOS.Dev.Host;

internal sealed record ProcessResult(int ExitCode, string Output, string Error, bool TimedOut);
