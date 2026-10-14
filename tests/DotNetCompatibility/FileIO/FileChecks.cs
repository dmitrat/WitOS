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
        yield return new Check("FileIO", "Copy", () =>
        {
            var source = Path.Combine(m_root.Value, "copy-source.bin");
            var target = Path.Combine(m_root.Value, "copy-target.bin");
            var bytes = Enumerable.Range(0, 20_000).Select(i => (byte)(i * 11)).ToArray();
            File.WriteAllBytes(source, bytes);
            File.Copy(source, target);
            File.Copy(source, target, overwrite: true);
            var same = File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes);
            File.Delete(source);
            File.Delete(target);
            return CheckResult.Now(same, $"{bytes.Length} bytes copied");
        });
        yield return new Check("FileIO", "Modes", () =>
        {
            var path = Path.Combine(m_root.Value, "modes.txt");
            File.WriteAllText(path, "m");
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(path, FileAttributes.ReadOnly);
                var readOnly = (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
                File.SetAttributes(path, FileAttributes.Normal);
                return CheckResult.Now(readOnly, $"read-only {readOnly}");
            }
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var first = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            var second = File.GetUnixFileMode(path);
            return CheckResult.Now(first == (UnixFileMode.UserRead | UnixFileMode.UserWrite) &&
                second == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead), $"{first}, then {second}");
        });
        yield return new Check("FileIO", "Temp", () =>
        {
            var probe = Path.Combine(Path.GetTempPath(), $"witos-compatibility-temp-{Environment.ProcessId}.txt");
            File.WriteAllText(probe, "t");
            var temp = File.Exists(probe);
            File.Delete(probe);
            var fixedTemp = true;
            if (!OperatingSystem.IsWindows())
            {
                // The fixed /tmp, which .NET's named synchronization objects use whatever TMPDIR says.
                var directory = Path.Combine("/tmp", $"witos-compatibility-{Environment.ProcessId}");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "f.txt"), "f");
                fixedTemp = File.Exists(Path.Combine(directory, "f.txt"));
                Directory.Delete(directory, recursive: true);
            }
            return CheckResult.Now(temp && fixedTemp, $"{Path.GetTempPath()}{(OperatingSystem.IsWindows() ? "" : " and /tmp")}");
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
