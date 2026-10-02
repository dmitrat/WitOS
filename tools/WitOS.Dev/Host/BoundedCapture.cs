using System.Text;

namespace WitOS.Dev.Host;

internal sealed class BoundedCapture
{
    #region Constants

    internal const int LIMIT = 8 * 1024 * 1024;

    #endregion

    #region Fields

    private readonly StringBuilder m_text = new();

    private readonly TaskCompletionSource m_overflow = new(TaskCreationOptions.RunContinuationsAsynchronously);

    #endregion

    #region Functions

    internal void Append(ReadOnlySpan<char> value)
    {
        lock (m_text)
        {
            var room = LIMIT - m_text.Length;
            m_text.Append(value[..Math.Min(room, value.Length)]);
            if (value.Length > room)
                m_overflow.TrySetResult();
        }
    }

    internal string Snapshot() { lock (m_text) return m_text.ToString(); }

    internal static async Task<string> ReadFileAsync(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > LIMIT)
            throw new InvalidDataException($"Evidence file exceeds {LIMIT} bytes: {path}");
        using var reader = new StreamReader(file);
        var result = new BoundedCapture();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
        {
            result.Append(buffer.AsSpan(0, count));
            if (result.Truncated)
                throw new InvalidDataException($"Evidence file exceeds capture limit: {path}");
        }
        return result.Snapshot();
    }

    #endregion

    #region Properties

    internal Task Overflow => m_overflow.Task;

    internal bool Truncated { get { lock (m_text) return m_overflow.Task.IsCompleted; } }

    #endregion
}
