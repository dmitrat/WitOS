namespace WitOS.Dev.Host;

/// <summary>
/// Exit code, captured output and timeout state of one child process.
/// </summary>
internal sealed record ProcessResult(int ExitCode, string Output, string Error, bool TimedOut);
