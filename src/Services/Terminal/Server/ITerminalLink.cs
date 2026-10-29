using System;

namespace OutWit.OS.Terminal.Server;

/// <summary>
/// What a terminal writes to its device (E4.1): a console's serial channel (<see cref="ConsoleLink"/>) or a
/// pseudo-terminal's master (<see cref="PseudoTerminal"/>).
/// </summary>
internal interface ITerminalLink
{
    /// <summary>
    /// A program's output, after output processing; it may wait until the device has room.
    /// </summary>
    /// <param name="bytes">The bytes.</param>
    void Write(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// The discipline's echo of what the device brought; it never waits, since the device's own thread writes it.
    /// </summary>
    /// <param name="bytes">The bytes.</param>
    void Echo(ReadOnlySpan<byte> bytes);
}

/// <summary>
/// A session a terminal tells of input, signals, its size and its end (E4.1: a console's session, a pseudo-terminal's
/// slave session).
/// </summary>
internal interface ITerminalSession
{
    /// <summary>
    /// Tells the session something; a session that cannot take it loses it.
    /// </summary>
    /// <param name="kind">EVENT_INPUT, EVENT_INTERRUPT, EVENT_QUIT, EVENT_SIZE or EVENT_HANGUP.</param>
    void Notify(uint kind);
}
