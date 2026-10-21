namespace WitOS.Dev.Kernel;

/// <summary>
/// What the runner does to the running guest once a line appeared in its serial log or in the terminal's transcript (plan
/// steps C2.1-C2.3, W1.2b, W3.2): through QMP a screendump of the display or keys pressed on the keyboard, or a connection
/// or an HTTP request to the guest's forwarded port. The actions of a request run in their order, each once its line is
/// there after the previous action's line (<see cref="ActionSequence"/>), so that a prompt shown again fires one action a
/// time.
/// </summary>
/// <param name="AfterLine">The line of the serial log or the terminal's transcript the action waits for; empty for an
/// action that follows the previous one at once.</param>
/// <param name="Kind">What to do.</param>
/// <param name="Argument">The keys' QKeyCode names, separated by spaces, each pressed and released in turn, a chord
/// joined by '+' (C2.2, C2.3: a line typed, such as "h i ret" or <see cref="Host.KeyTyping"/>'s keys of a text); the bytes
/// a connection sends (W1.2b); an HTTP request's method, path and expected text, separated by spaces (W3.2,
/// <see cref="Http"/>); nothing for a screendump, whose file the runner names.</param>
internal sealed record BootAction(string AfterLine, BootActionKind Kind, string Argument = "")
{
    #region Constants

    private static readonly string[] HTTP_METHODS = ["GET", "POST"];

    #endregion

    #region Functions

    /// <summary>
    /// An HTTP request to the guest's forwarded port (plan step W3.2): the runner requires an answer of 200 whose body holds
    /// the text; a POST sends the text as its body.
    /// </summary>
    /// <param name="afterLine">The line the request waits for; empty for one that follows the previous action at once.</param>
    /// <param name="method">GET or POST.</param>
    /// <param name="path">The request's path, from '/'.</param>
    /// <param name="text">The text the answer must hold, and a POST's body.</param>
    /// <returns>The action.</returns>
    public static BootAction Http(string afterLine, string method, string path, string text)
    {
        var action = new BootAction(afterLine, BootActionKind.Http, $"{method} {path} {text}");
        ParseHttp(action.Argument);
        return action;
    }

    /// <summary>
    /// Reads an HTTP action's argument.
    /// </summary>
    /// <param name="argument">The argument <see cref="Http"/> wrote.</param>
    /// <returns>The method, the path and the text.</returns>
    /// <exception cref="InvalidDataException">The argument is not a method, a path and a text.</exception>
    public static (string Method, string Path, string Text) ParseHttp(string argument)
    {
        var parts = argument.Split(' ', 3);
        if (parts.Length < 3 || !HTTP_METHODS.Contains(parts[0]) || !parts[1].StartsWith('/') || parts[1].Contains(' ') || parts[2].Length == 0)
            throw new InvalidDataException($"An HTTP action's argument is a method, a path and a text, not \"{argument}\".");
        return (parts[0], parts[1], parts[2]);
    }

    #endregion
}

/// <summary>
/// The kinds of <see cref="BootAction"/>.
/// </summary>
internal enum BootActionKind
{
    /// <summary>
    /// QEMU's screendump of the display to the boot's screen file (<see cref="BootScenarioRunner.ScreenPath"/>).
    /// </summary>
    Screendump,

    /// <summary>
    /// Keys pressed and released on the guest's keyboard, in turn.
    /// </summary>
    Key,

    /// <summary>
    /// A connection to the guest's port <see cref="BootRequest.NetworkForward"/> through QEMU's hostfwd (W1.2b): the runner
    /// sends the argument's bytes, ends its sending and requires them back whole before the guest ends its own.
    /// </summary>
    Connect,

    /// <summary>
    /// An HTTP request to the guest's port <see cref="BootRequest.NetworkForward"/> through QEMU's hostfwd (W3.2): the
    /// argument's method and path, answered with 200 and the argument's text (<see cref="BootAction.Http"/>).
    /// </summary>
    Http
}
