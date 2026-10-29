// The runtime finds a startup hook as the type StartupHook outside any namespace.
#pragma warning disable CA1050

/// <summary>
/// The startup hook WitOS gives PowerShell (plan step E4.2). Before pwsh's Main runs its login-shell check, PowerShell
/// 7.6's AttemptExecPwshLogin reads /proc on Linux and calls sysctl on every other Unix, a call musl does not have, unless
/// __PWSH_LOGIN_CHECKED is set (C1.1). pwsh then removes the variable from its own environment, so a pwsh that another
/// pwsh starts — directly, or through Arlecchino.Commander, which runs its commands through SHELL — met the check
/// without it and died with EntryPointNotFoundException. The install's /etc/environment sets the variable for the first
/// pwsh; this hook, which the layer's pwsh.runtimeconfig.json names, sets it again for every pwsh where it is missing.
/// It goes once upstream runs that branch on macOS alone (PowerShell/PowerShell#28283).
/// </summary>
internal static class StartupHook
{
    #region Constants

    private const string LOGIN_CHECKED = "__PWSH_LOGIN_CHECKED";

    #endregion

    #region Functions

    /// <summary>
    /// Sets the variable when it is missing.
    /// </summary>
    public static void Initialize()
    {
        if (Environment.GetEnvironmentVariable(LOGIN_CHECKED) is null)
            Environment.SetEnvironmentVariable(LOGIN_CHECKED, "1");
    }

    #endregion
}
