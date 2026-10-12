namespace DotNetCompatibility;

/// <summary>
/// Files through the BCL, in a directory of their own under the temporary path: text and async reads, streams, trees,
/// moves and replacements, times, and the watcher.
/// </summary>
internal static class FileChecks
{
    private static readonly Lazy<string> m_root = new(() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"witos-compatibility-{Environment.ProcessId}")).FullName);

    /// <summary>
    /// The area's checks.
    /// </summary>
    /// <returns>The checks.</returns>
    public static IEnumerable<Check> All()
    {
        yield return new Check("FileIO", "TextAsync", async () =>
        {
            var path = Path.Combine(m_root.Value, "test.txt");
            await File.WriteAllTextAsync(path, "written asynchronously");
            var text = await File.ReadAllTextAsync(path);
            return CheckResult.That(text == "written asynchronously", text);
        });
        yield return new Check("FileIO", "Stream", () =>
        {
            var path = Path.Combine(m_root.Value, "stream.bin");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
            {
                stream.Write("0123456789"u8);
                stream.Seek(3, SeekOrigin.Begin);
                stream.Write("abc"u8);
                stream.SetLength(8);
                stream.Flush(flushToDisk: true);
            }
            var text = File.ReadAllText(path);
            return CheckResult.Now(text == "012abc67", text);
        });
        yield return new Check("FileIO", "Tree", () =>
        {
            var tree = Path.Combine(m_root.Value, "tree");
            Directory.CreateDirectory(Path.Combine(tree, "a", "b"));
            File.WriteAllText(Path.Combine(tree, "a", "one.txt"), "1");
            File.WriteAllText(Path.Combine(tree, "a", "b", "two.txt"), "2");
            var files = Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Order(StringComparer.Ordinal);
            var listed = string.Join(",", files);
            Directory.Delete(tree, recursive: true);
            return CheckResult.Now(listed == "one.txt,two.txt" && !Directory.Exists(tree), listed);
        });
        yield return new Check("FileIO", "MoveReplace", () =>
        {
            var first = Path.Combine(m_root.Value, "first.txt");
            var second = Path.Combine(m_root.Value, "second.txt");
            File.WriteAllText(first, "first");
            File.WriteAllText(second, "second");
            File.Move(first, second, overwrite: true);
            var text = File.ReadAllText(second);
            File.Delete(second);
            return CheckResult.Now(text == "first" && !File.Exists(first) && !File.Exists(second), text);
        });
        yield return new Check("FileIO", "Times", () =>
        {
            var path = Path.Combine(m_root.Value, "times.txt");
            File.WriteAllText(path, "t");
            var written = File.GetLastWriteTimeUtc(path);
            var skew = Math.Abs((written - DateTime.UtcNow).TotalMinutes);
            return CheckResult.Now(skew < 10, $"{written:O}");
        });
        yield return new Check("FileIO", "Watcher", async () =>
        {
            var directory = Directory.CreateDirectory(Path.Combine(m_root.Value, "watched")).FullName;
            var created = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FileSystemWatcher(directory);
            watcher.Created += (_, change) => created.TrySetResult(change.Name ?? "");
            watcher.EnableRaisingEvents = true;
            await File.WriteAllTextAsync(Path.Combine(directory, "new.txt"), "n");
            var name = await created.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return CheckResult.That(name == "new.txt", name);
        });
    }

    /// <summary>
    /// Removes what the checks left.
    /// </summary>
    public static void Clean()
    {
        if (m_root.IsValueCreated && Directory.Exists(m_root.Value))
            Directory.Delete(m_root.Value, recursive: true);
    }
}
