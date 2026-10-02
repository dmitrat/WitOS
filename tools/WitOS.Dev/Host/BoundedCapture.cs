using System.Text;

namespace WitOS.Dev.Host;

internal sealed class BoundedCapture
{
    internal const int Limit = 8 * 1024 * 1024;
    private readonly StringBuilder text = new();
    private readonly TaskCompletionSource overflow = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Overflow => overflow.Task;
    internal bool Truncated { get { lock (text) return overflow.Task.IsCompleted; } }
    internal void Append(ReadOnlySpan<char> value)
    {
        lock (text)
        {
            var room = Limit - text.Length;
            text.Append(value[..Math.Min(room, value.Length)]);
            if (value.Length > room)
                overflow.TrySetResult();
        }
    }
    internal string Snapshot() { lock (text) return text.ToString(); }

    internal static async Task<string> ReadFileAsync(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > Limit)
            throw new InvalidDataException($"Evidence file exceeds {Limit} bytes: {path}");
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
}
