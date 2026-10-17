namespace WitOS.Dev.Kernel;

/// <summary>
/// What the runner does to the running guest through QMP once a line appeared in its serial log or in the terminal's
/// transcript (plan steps C2.1, C2.2): a screendump of the display, or keys pressed on the keyboard. The actions of a
/// request run in their order, each once its line is there.
/// </summary>
/// <param name="AfterLine">The line of the serial log or the terminal's transcript the action waits for.</param>
/// <param name="Kind">What to do.</param>
/// <param name="Argument">The keys' QKeyCode names, separated by spaces, each pressed and released in turn (C2.2: a line
/// typed, such as "h i ret"); nothing for a screendump, whose file the runner names.</param>
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
