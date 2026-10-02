using System.Text.RegularExpressions;

namespace WitOS.Dev.NativeAot.Acceptance;

/// <summary>
/// Forward-only cursor over the non-empty runtime protocol lines; every expectation consumes exactly one line.
/// </summary>
internal sealed class RuntimeBootEnvelopeCursor
{
    #region Fields

    private readonly string[] m_lines;

    #endregion

    #region Constructors

    public RuntimeBootEnvelopeCursor(string[] lines, int position)
    {
        m_lines = lines;
        Position = position;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Consumes the current line when it equals <paramref name="value"/>.
    /// </summary>
    public bool Exact(string value)
    {
        if (Position >= m_lines.Length || Peek != value)
        {
            Error = "Expected " + value;
            return false;
        }
        ++Position;
        return true;
    }

    /// <summary>
    /// Consumes the current line when it starts with <paramref name="value"/>.
    /// </summary>
    public bool Prefix(string value)
    {
        if (!Peek.StartsWith(value, StringComparison.Ordinal))
        {
            Error = "Expected prefix " + value;
            return false;
        }
        ++Position;
        return true;
    }

    /// <summary>
    /// Consumes the four native startup lines of one runtime execution.
    /// </summary>
    public bool Startup()
        => Exact(RuntimeBootEnvelope.USER + "image published") &&
            Exact(RuntimeBootEnvelope.USER + "native TLS ready") &&
            Exact(RuntimeBootEnvelope.USER + "native initializers ready") &&
            Exact(RuntimeBootEnvelope.USER + "entering upstream wmain");

    /// <summary>
    /// Consumes an optional, well-formed GC bookkeeping commit failure line.
    /// </summary>
    public bool BookkeepingFailure()
    {
        if (!Peek.StartsWith("[USER] Committing ", StringComparison.Ordinal))
        {
            return true;
        }
        if (!Regex.IsMatch(Peek,
                @"^\[USER\] Committing [1-9][0-9]* bytes \([0-9]+\.[0-9]+ mb\) for GC bookkeeping element#[0-9]+ failed\[USER\] $"))
        {
            return false;
        }
        ++Position;
        return true;
    }

    /// <summary>
    /// Consumes one illegal-instruction fail-fast report whose address equals its RIP.
    /// </summary>
    public bool Fatal()
    {
        if (!Regex.IsMatch(Peek, @"^\[USER\] \[NATIVE-FAIL-FAST\] code=0xC000001D address=0x([0-9A-F]{16}) rip=0x\1$"))
        {
            return false;
        }
        ++Position;
        return true;
    }

    #endregion

    #region Properties

    public int Position { get; private set; }

    public string Error { get; private set; } = "Invalid block count";

    public string Peek => Position < m_lines.Length ? m_lines[Position] : "";

    #endregion
}
