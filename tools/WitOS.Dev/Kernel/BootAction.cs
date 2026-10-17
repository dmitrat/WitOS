namespace WitOS.Dev.Kernel;

/// <summary>
/// What the runner does to the running guest through QMP once a line appeared in its serial log or in the terminal's
/// transcript (plan steps C2.1-C2.3): a screendump of the display, or keys pressed on the keyboard. The actions of a
/// request run in their order, each once its line is there after the previous action's line (<see cref="ActionSequence"/>),
/// so that a prompt shown again fires one action a time.
/// </summary>
/// <param name="AfterLine">The line of the serial log or the terminal's transcript the action waits for; empty for an
/// action that follows the previous one at once.</param>
/// <param name="Kind">What to do.</param>
/// <param name="Argument">The keys' QKeyCode names, separated by spaces, each pressed and released in turn, a chord
/// joined by '+' (C2.2, C2.3: a line typed, such as "h i ret" or <see cref="Host.KeyTyping"/>'s keys of a text); nothing
/// for a screendump, whose file the runner names.</param>
internal sealed record BootAction(string AfterLine, BootActionKind Kind, string Argument = "");

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
    Key
}
