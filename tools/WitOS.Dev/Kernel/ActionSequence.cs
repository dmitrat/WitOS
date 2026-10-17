namespace WitOS.Dev.Kernel;

/// <summary>
/// The actions of a boot request in their order (plan steps C2.1–C2.3): each is due once its line appeared in the serial
/// log or in the terminal's transcript after the previous action's line — as the terminal script's inputs are typed
/// (C1.2), so that a prompt the guest shows again after each command fires one action a time — or at once, right after
/// the previous action, when its line is empty.
/// </summary>
internal sealed class ActionSequence
{
    #region Fields

    private readonly IReadOnlyList<BootAction> m_actions;
    private int m_next;
    private int m_logFrom;
    private int m_transcriptFrom;

    #endregion

    #region Constructors

    /// <summary>
    /// The sequence of a request's actions.
    /// </summary>
    /// <param name="actions">The actions, in order.</param>
    public ActionSequence(IReadOnlyList<BootAction> actions)
    {
        m_actions = actions;
    }

    #endregion

    #region Functions

    /// <summary>
    /// The next action due for what the guest wrote so far, taken; null while its line is not there yet or every action
    /// was taken.
    /// </summary>
    /// <param name="log">The serial log so far.</param>
    /// <param name="transcript">The terminal's transcript so far.</param>
    /// <returns>The action, or null.</returns>
    public BootAction? Next(string log, string transcript)
    {
        if (Done)
            return null;
        var action = m_actions[m_next];
        if (action.AfterLine.Length > 0)
        {
            var at = log.IndexOf(action.AfterLine, Math.Min(m_logFrom, log.Length), StringComparison.Ordinal);
            if (at >= 0)
                m_logFrom = at + action.AfterLine.Length;
            else
            {
                at = transcript.IndexOf(action.AfterLine, Math.Min(m_transcriptFrom, transcript.Length), StringComparison.Ordinal);
                if (at < 0)
                    return null;
                m_transcriptFrom = at + action.AfterLine.Length;
            }
        }
        m_next++;
        return action;
    }

    #endregion

    #region Properties

    /// <summary>
    /// Whether every action was taken.
    /// </summary>
    public bool Done => m_next >= m_actions.Count;

    /// <summary>
    /// The actions taken so far.
    /// </summary>
    public int Taken => m_next;

    #endregion
}
