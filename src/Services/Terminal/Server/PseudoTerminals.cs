using System.Collections.Generic;
using OutWit.OS.Terminals;

namespace OutWit.OS.Terminal.Server;

/// <summary>
/// The service's pseudo-terminals (plan step E4.1): each takes the lowest free number below
/// <see cref="TerminalMessages.PSEUDO_MAXIMUM"/> and gives it back once its master and its last slave session went, as
/// Linux's devpts reuses its numbers.
/// </summary>
internal sealed class PseudoTerminals
{
    #region Fields

    private readonly object m_gate = new();
    private readonly PseudoTerminal?[] m_terminals = new PseudoTerminal?[TerminalMessages.PSEUDO_MAXIMUM];
    private bool m_stopped;

    #endregion

    #region Functions

    /// <summary>
    /// A new pseudo-terminal, or none when every number is taken or the service stops.
    /// </summary>
    public PseudoTerminal? Open()
    {
        lock (m_gate)
        {
            if (m_stopped)
                return null;
            for (var number = 0; number < m_terminals.Length; number++)
            {
                if (m_terminals[number] is not null)
                    continue;
                var terminal = new PseudoTerminal(number, Release);
                m_terminals[number] = terminal;
                return terminal;
            }
            return null;
        }
    }

    /// <summary>
    /// The pseudo-terminal of a number, or none.
    /// </summary>
    /// <param name="number">The number.</param>
    public PseudoTerminal? Find(long number)
    {
        lock (m_gate)
            return number >= 0 && number < m_terminals.Length ? m_terminals[number] : null;
    }

    /// <summary>
    /// Stops every pseudo-terminal: their readers and writers return, and no new one opens.
    /// </summary>
    /// <returns>The pseudo-terminals that were open.</returns>
    public PseudoTerminal[] Stop()
    {
        var open = new List<PseudoTerminal>();
        lock (m_gate)
        {
            m_stopped = true;
            foreach (var terminal in m_terminals)
                if (terminal is not null)
                    open.Add(terminal);
        }
        foreach (var terminal in open)
            terminal.Stop();
        return [.. open];
    }

    private void Release(PseudoTerminal terminal)
    {
        lock (m_gate)
            if (m_terminals[terminal.Number] == terminal)
                m_terminals[terminal.Number] = null;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The pseudo-terminals open now.
    /// </summary>
    public int Count
    {
        get
        {
            lock (m_gate)
            {
                var count = 0;
                foreach (var terminal in m_terminals)
                    count += terminal is null ? 0 : 1;
                return count;
            }
        }
    }

    #endregion
}
