// System.IO over the namespace service (plan step D5.2): an ordinary .NET program's file APIs on the volumes the
// namespace service mounts, through System.Native and the libc's files: text and bytes written and read, a stream
// seeked, written, cut and flushed to the disk, directories made, listed and removed recursively, files moved, replaced
// and deleted, a relative path from the current directory, the exceptions a missing path and a read-only volume give,
// a file of a megabyte, and memory maps of a file: copy-on-write and read-only, which the libc makes copies, and
// writable shared, which it refuses. Each area prints one line; the last counts. /data/dotnet-kept.txt stays for the
// host.
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

internal static class Files
{
    private const string ROOT = "/data/dotnet";
    private const string KEPT = "Written by a .NET program through System.IO and the namespace service.";

    private static int m_passed, m_failed;

    private static int Main()
    {
        Area("text", Text);
        Area("stream", Stream);
        Area("directories", Directories);
        Area("moves", Moves);
        Area("current directory", CurrentDirectory);
        Area("exceptions", Exceptions);
        Area("megabyte", Megabyte);
        Area("memory map", MemoryMap);
        Area("fat", Fat);
        Area("cleanup", Cleanup);
        Console.WriteLine($"[FILES] System.IO over the namespace service under NativeAOT on {RuntimeInformation.ProcessArchitecture}: " +
            $"{m_passed} areas passed, {m_failed} failed");
        return m_failed == 0 ? 0 : 1;
    }

    private static void Area(string name, Func<string?> check)
    {
        string? failure;
        try
        {
            failure = check();
        }
        catch (Exception exception)
        {
            failure = $"threw {exception.GetType().Name}: {exception.Message}";
        }
        if (failure is null)
            ++m_passed;
        else
            ++m_failed;
        Console.WriteLine($"[FILES] {name}: {failure ?? "ok"}");
    }

    private static string? Text()
    {
        Directory.CreateDirectory(ROOT);
        var path = Path.Combine(ROOT, "hello.txt");
        File.WriteAllText(path, "hello");
        File.AppendAllText(path, ", world");
        if (File.ReadAllText(path) != "hello, world")
            return $"ReadAllText: {File.ReadAllText(path)}";
        File.WriteAllLines(path, ["first", "second", "third"]);
        var lines = File.ReadAllLines(path);
        if (lines.Length != 3 || lines[2] != "third")
            return "ReadAllLines";
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != "first\nsecond\nthird\n".Length)
            return $"FileInfo: {info.Length}";
        if (Math.Abs((info.LastWriteTimeUtc - DateTime.UtcNow).TotalMinutes) > 10)
            return $"LastWriteTimeUtc: {info.LastWriteTimeUtc:O} against {DateTime.UtcNow:O}";
        File.WriteAllText("/data/dotnet-kept.txt", KEPT);
        return null;
    }

    private static string? Stream()
    {
        var path = Path.Combine(ROOT, "stream.bin");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
        {
            stream.Write(Encoding.ASCII.GetBytes("0123456789"));
            stream.Seek(4, SeekOrigin.Begin);
            stream.Write("abc"u8);
            stream.SetLength(8);
            stream.Flush(flushToDisk: true);
            stream.Seek(0, SeekOrigin.Begin);
            var buffer = new byte[16];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read != 8 || Encoding.ASCII.GetString(buffer, 0, read) != "0123abc7")
                return $"read back {Encoding.ASCII.GetString(buffer, 0, read)}";
            if (stream.Length != 8 || stream.Position != 8)
                return $"Length {stream.Length}, Position {stream.Position}";
        }
        using (var writer = new StreamWriter(path, append: true))
            writer.Write("XY");
        using (var reader = new StreamReader(path))
            return reader.ReadToEnd() == "0123abc7XY" ? null : "StreamWriter with append";
    }

    private static string? Directories()
    {
        Directory.CreateDirectory(Path.Combine(ROOT, "tree", "a", "b"));
        File.WriteAllText(Path.Combine(ROOT, "tree", "a", "one.txt"), "1");
        File.WriteAllText(Path.Combine(ROOT, "tree", "a", "b", "two.txt"), "2");
        var files = Directory.EnumerateFiles(Path.Combine(ROOT, "tree"), "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(ROOT, file)).Order(StringComparer.Ordinal).ToArray();
        if (string.Join(",", files) != "tree/a/b/two.txt,tree/a/one.txt")
            return $"EnumerateFiles: {string.Join(",", files)}";
        var directories = Directory.GetDirectories(Path.Combine(ROOT, "tree", "a"));
        if (directories.Length != 1 || Path.GetFileName(directories[0]) != "b")
            return $"GetDirectories: {string.Join(",", directories)}";
        Directory.Delete(Path.Combine(ROOT, "tree"), recursive: true);
        return Directory.Exists(Path.Combine(ROOT, "tree")) ? "Directory.Delete" : null;
    }

    private static string? Moves()
    {
        var first = Path.Combine(ROOT, "first.txt");
        var second = Path.Combine(ROOT, "second.txt");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");
        File.Move(first, Path.Combine(ROOT, "moved.txt"));
        if (File.Exists(first) || File.ReadAllText(Path.Combine(ROOT, "moved.txt")) != "first")
            return "File.Move";
        File.Move(Path.Combine(ROOT, "moved.txt"), second, overwrite: true);
        if (File.ReadAllText(second) != "first")
            return "File.Move with overwrite";
        File.Delete(second);
        return File.Exists(second) ? "File.Delete" : null;
    }

    private static string? CurrentDirectory()
    {
        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(ROOT);
        try
        {
            File.WriteAllText("relative.txt", "relative");
            if (Path.GetFullPath("relative.txt") != Path.Combine(ROOT, "relative.txt") || Environment.CurrentDirectory != ROOT)
                return $"GetFullPath {Path.GetFullPath("relative.txt")}, CurrentDirectory {Environment.CurrentDirectory}";
            return File.ReadAllText(Path.Combine(ROOT, "relative.txt")) == "relative" ? null : "the relative file";
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    private static string? Exceptions()
    {
        try
        {
            File.ReadAllText("/data/missing.txt");
            return "no FileNotFoundException";
        }
        catch (FileNotFoundException)
        {
        }
        try
        {
            Directory.GetFiles("/data/missing");
            return "no DirectoryNotFoundException";
        }
        catch (DirectoryNotFoundException)
        {
        }
        try
        {
            File.WriteAllText("/boot/refused.txt", "x");
            return "a write to the read-only boot volume succeeded";
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
        }
        try
        {
            using var stream = new FileStream(Path.Combine(ROOT, "relative.txt"), FileMode.CreateNew);
            return "no IOException for CreateNew of an existing file";
        }
        catch (IOException)
        {
        }
        return null;
    }

    private static string? Megabyte()
    {
        var path = Path.Combine(ROOT, "megabyte.bin");
        var bytes = Enumerable.Range(0, 1 << 20).Select(i => (byte)(i * 31 ^ i >> 12)).ToArray();
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536))
        {
            for (var at = 0; at < bytes.Length; at += 65536)
                stream.Write(bytes, at, 65536);
        }
        var back = File.ReadAllBytes(path);
        return back.AsSpan().SequenceEqual(bytes) ? null : $"read back {back.Length} other bytes";
    }

    private static string? MemoryMap()
    {
        var path = Path.Combine(ROOT, "mapped.bin");
        var bytes = Enumerable.Range(0, 6000).Select(i => (byte)(i * 7)).ToArray();
        File.WriteAllBytes(path, bytes);
        using (var map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.CopyOnWrite))
        using (var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.CopyOnWrite))
        {
            var seen = new byte[bytes.Length];
            view.ReadArray(0, seen, 0, seen.Length);
            if (!seen.AsSpan().SequenceEqual(bytes))
                return "the view's bytes";
            view.Write(0, (byte)0xFF);
            if (view.ReadByte(0) != 0xFF)
                return "a write to the view";
        }
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            return "a copy-on-write view changed the file";
        using (var map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
        using (var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
        {
            var seen = new byte[bytes.Length];
            view.ReadArray(0, seen, 0, seen.Length);
            if (!seen.AsSpan().SequenceEqual(bytes))
                return "the read-only view's bytes";
        }
        try
        {
            using var map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite);
            using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            return "a writable shared view of a volume's file was made";
        }
        catch (IOException)
        {
            return null; // a writable shared mapping of a volume's file is ENODEV
        }
    }

    private static string? Fat()
    {
        var path = "/fat/dotnet.txt";
        File.WriteAllText(path, "on FAT");
        var text = File.ReadAllText(path);
        File.Delete(path);
        return text == "on FAT" && !File.Exists(path) ? null : $"FAT read back {text}";
    }

    private static string? Cleanup()
    {
        Directory.Delete(ROOT, recursive: true);
        return Directory.Exists(ROOT) ? "the root stayed" : File.ReadAllText("/data/dotnet-kept.txt") == KEPT ? null : "the kept file";
    }
}
