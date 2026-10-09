namespace WitOS.Dev.Host;

/// <summary>
/// Git in a checkout of a pinned upstream source under .tools; never overwrites local changes.
/// </summary>
internal static class Git
{
    #region Functions

    /// <summary>
    /// Runs Git in the checkout and throws on failure or timeout.
    /// </summary>
    /// <param name="checkout">Checkout directory.</param>
    /// <param name="args">Git arguments.</param>
    /// <param name="timeout">Timeout in seconds.</param>
    /// <returns>Standard output.</returns>
    internal static async Task<string> RunAsync(string checkout, string[] args, int timeout = 60)
    {
        var result = await Processes.RunAsync("git", ["-c", $"safe.directory={checkout.Replace('\\', '/')}", .. args], checkout,
            timeout);
        if (result.TimedOut || result.ExitCode != 0)
            throw new InvalidOperationException($"Git {args[0]} failed in {checkout}. {result.Output}\n{result.Error}");
        return result.Output;
    }

    /// <summary>
    /// Throws when the checkout has local changes or untracked files.
    /// </summary>
    /// <param name="checkout">Checkout directory.</param>
    internal static async Task RequireCleanAsync(string checkout)
    {
        if (!string.IsNullOrWhiteSpace(await RunAsync(checkout, ["status", "--porcelain", "--untracked-files=normal"])))
            throw new InvalidDataException($"Checkout {checkout} has local changes; refusing to build or overwrite them.");
    }

    #endregion
}
