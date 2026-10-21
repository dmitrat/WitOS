using System.Diagnostics;

namespace DotNetCompatibility;

/// <summary>
/// Files through the BCL, in a directory of their own under the temporary path: text and async reads, streams, trees,
/// moves and replacements, times, small writes beside threads that open files (S8.1), and the watcher.
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
            var stamp = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, stamp);
            var set = File.GetLastWriteTimeUtc(path);
            return CheckResult.Now(skew < 10 && set == stamp, $"{written:O}, set to {set:O}");
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
        yield return new Check("FileIO", "LogicalDrives", () =>
        {
            // The root of the file system is a logical drive and a DriveInfo on every platform: the system drive on
            // Windows, / on Unix (C1.2: PowerShell builds its file system drives from DriveInfo.GetDrives(), and WitOS
            // reports its mount points through the libc's /etc/mtab where Linux has /proc/mounts).
            var root = OperatingSystem.IsWindows() ? Path.GetPathRoot(Environment.SystemDirectory)! : "/";
            var drives = Environment.GetLogicalDrives();
            var infos = DriveInfo.GetDrives();
            var observed = $"{drives.Length} logical drives ({string.Join(", ", drives)}), {infos.Length} DriveInfo";
            return CheckResult.Now(
                drives.Contains(root, StringComparer.OrdinalIgnoreCase) &&
                infos.Any(info => string.Equals(info.Name, root, StringComparison.OrdinalIgnoreCase)),
                observed);
        });
        yield return new Check("FileIO", "SmallWritesUnderContention", async () =>
        {
            // One thread writes a file 4 bytes a write, unbuffered, as CoreCLR writes a MultiCoreJit profile, while three
            // threads stat a file of the boot package without a pause (S8.1): a libc that held its tables through each
            // write's round trip with the namespace service left the three waiting for them — on several processors
            // spinning, so that the kernel lock's storm made each write 90 ms and the three managed a few stats a second —
            // while the tables let go through the round trip serve a stat of the package, which needs no service, in
            // microseconds. The judge is the three's rate in the guest's clock, not the writer's time, which the service's
            // round trips under TCG on four processors make tens of milliseconds a write on the slower ISAs; the file lies
            // under /tmp, the namespace service's file system in memory, so that no journal's flushes take part.
            const int WRITES = 500;
            const double STATS_PER_SECOND = 1000;
            var directory = Directory.CreateDirectory(OperatingSystem.IsWindows()
                ? Path.Combine(Path.GetTempPath(), $"witos-small-writes-{Environment.ProcessId}")
                : $"/tmp/witos-small-writes-{Environment.ProcessId}").FullName;
            var path = Path.Combine(directory, "small-writes.bin");
            var probe = File.Exists("/dotnet/dotnet") ? "/dotnet/dotnet" : Environment.ProcessPath!; // the package's, or the host's muxer
            using var stop = new CancellationTokenSource();
            using var started = new CountdownEvent(3); // the writer starts once the three run, which the hosts' writer would outrun
            var contenders = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
            {
                var stats = 0L;
                started.Signal();
                while (!stop.IsCancellationRequested)
                {
                    if (!File.Exists(probe))
                        return -1L;
                    stats++;
                }
                return stats;
            })).ToArray();
            started.Wait();
            var watch = Stopwatch.StartNew();
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 0))
                for (var i = 0; i < WRITES; i++)
                    file.Write(BitConverter.GetBytes(i));
            watch.Stop();
            stop.Cancel();
            var stats = (await Task.WhenAll(contenders)).Sum();
            var length = new FileInfo(path).Length;
            Directory.Delete(directory, recursive: true);
            var rate = stats / Math.Max(watch.Elapsed.TotalSeconds, 0.001);
            return CheckResult.That(length == 4L * WRITES && stats > 0 && rate >= STATS_PER_SECOND,
                $"{WRITES} writes in {watch.Elapsed.TotalSeconds:F1} s beside {stats} stats of {probe} ({rate:F0}/s), {length} bytes");
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
