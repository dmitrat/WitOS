using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace OutWit.OS.Tests.FileSystems;

/// <summary>
/// Reference ext images made by e2fsprogs (storage document §73: compatibility is the format's actual semantics):
/// <c>mkfs -d</c> lays a known tree into a fresh volume of the variant asked. Hosts without e2fsprogs (Windows) skip
/// the tests that need an image.
/// </summary>
internal static class Ext4Images
{
    #region Constants

    /// <summary>
    /// The text of /hello.txt.
    /// </summary>
    public const string HELLO = "Hello from mkfs -d.\n";

    /// <summary>
    /// The files of /many: an htree, with index nodes below its root in blocks of 1024 bytes.
    /// </summary>
    public const int MANY = 3000;

    /// <summary>
    /// The target of /longlink, too long for a fast link.
    /// </summary>
    public static readonly string LONG_TARGET = "docs/" + new string('x', 95);

    private static readonly string[] MKFS_PLACES = ["/sbin/mkfs.ext4", "/usr/sbin/mkfs.ext4", "/usr/bin/mkfs.ext4"];

    #endregion

    #region Functions

    /// <summary>
    /// Skips the test when the host has no e2fsprogs.
    /// </summary>
    /// <returns>The path of mke2fs's mkfs.ext4.</returns>
    public static string RequireMkfs()
    {
        var mkfs = MKFS_PLACES.FirstOrDefault(File.Exists);
        if (mkfs is null || !OperatingSystem.IsLinux())
            Assert.Ignore("The host has no e2fsprogs (mkfs.ext4).");
        return mkfs!;
    }

    /// <summary>
    /// A fresh image of a variant holding the reference tree.
    /// </summary>
    /// <param name="variant">ext4, ext4-1k, ext4-nocsum, ext3 or ext2.</param>
    /// <param name="source">The tree laid in, which the caller compares against.</param>
    /// <returns>The image's bytes, 16 MiB.</returns>
    public static byte[] Create(string variant, out string source)
    {
        var mkfs = RequireMkfs();
        var work = Path.Combine(Path.GetTempPath(), $"witos-ext4-{Guid.NewGuid():N}");
        source = Path.Combine(work, "tree");
        WriteTree(source);
        var image = Path.Combine(work, "volume.img");
        string[] options = variant switch
        {
            "ext4" => ["-t", "ext4", "-b", "4096", "-I", "256"],
            "ext4-1k" => ["-t", "ext4", "-b", "1024", "-I", "256"],
            "ext4-nocsum" => ["-t", "ext4", "-b", "4096", "-I", "256", "-O", "^metadata_csum"],
            "ext4-small-inodes" => ["-t", "ext4", "-b", "4096", "-I", "128"],
            "ext3" => ["-t", "ext3", "-b", "4096", "-I", "256"],
            "ext2" => ["-t", "ext2", "-b", "4096", "-I", "256"],
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        Run(mkfs, [.. options, "-F", "-q", "-T", "default", "-N", "4096", "-L", "WITOS", "-U", "6d3b1f2a-9c4e-4b7d-8a21-5749544f5345", "-d", source, image, "16M"]);
        // mkfs -d writes directories linearly; e2fsck -D indexes those of more than a block as htrees.
        Run(Path.Combine(Path.GetDirectoryName(mkfs)!, "e2fsck"), ["-f", "-y", "-D", image], 1);
        return File.ReadAllBytes(image);
    }

    /// <summary>
    /// Removes what Create left beside an image.
    /// </summary>
    /// <param name="source">The tree Create returned.</param>
    public static void Delete(string source)
    {
        var work = Path.GetDirectoryName(source)!;
        if (Directory.Exists(work))
            Directory.Delete(work, recursive: true);
    }

    /// <summary>
    /// The byte of /big.bin's data at an offset of its chunk.
    /// </summary>
    /// <param name="chunk">The chunk.</param>
    /// <param name="offset">The offset in the chunk.</param>
    /// <returns>The byte, never zero, so that mkfs keeps every data block.</returns>
    public static byte Big(int chunk, int offset) => (byte)(1 + (chunk * 7 + offset) % 250);

    // The tree: a text file, an empty one, nested directories, a sparse file of ten chunks between holes (more
    // extents than i_block holds, so the tree has an index), an htree directory, a fast and a slow link, and modes.
    private static void WriteTree(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "docs", "nested", "deep"));
        Directory.CreateDirectory(Path.Combine(root, "many"));
        File.WriteAllText(Path.Combine(root, "hello.txt"), HELLO);
        File.WriteAllBytes(Path.Combine(root, "empty.txt"), []);
        File.WriteAllBytes(Path.Combine(root, "docs", "readme.md"), Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Readme line.\n", 160))));
        File.WriteAllBytes(Path.Combine(root, "docs", "nested", "deep", "file.bin"), [.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 13))]);
        using (var big = File.Create(Path.Combine(root, "big.bin")))
        {
            for (var chunk = 0; chunk < 10; chunk++)
            {
                big.Position = chunk * 65536L;
                big.Write([.. Enumerable.Range(0, 6000).Select(i => Big(chunk, i))]);
            }
        }
        for (var i = 0; i < MANY; i++)
            File.WriteAllBytes(Path.Combine(root, "many", $"an-entry-with-a-rather-long-name-{i:0000}.txt"), []); // no data block
        File.CreateSymbolicLink(Path.Combine(root, "link"), "hello.txt");
        File.CreateSymbolicLink(Path.Combine(root, "longlink"), LONG_TARGET);
        File.WriteAllText(Path.Combine(root, "run.sh"), "#!/bin/sh\necho witos\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(root, "run.sh"), (UnixFileMode)Convert.ToInt32("755", 8));
            File.SetUnixFileMode(Path.Combine(root, "hello.txt"), (UnixFileMode)Convert.ToInt32("644", 8));
        }
    }

    /// <summary>
    /// An inode's byte offset in an image, from its group's descriptor.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="inode">The inode's number.</param>
    /// <returns>The offset of its record.</returns>
    public static long InodeOffset(byte[] image, long inode)
    {
        var superblock = image.AsSpan(1024, 1024);
        var blockSize = 1024 << BinaryPrimitives.ReadInt32LittleEndian(superblock[0x18..]);
        var perGroup = BinaryPrimitives.ReadUInt32LittleEndian(superblock[0x28..]);
        var inodeSize = BinaryPrimitives.ReadUInt16LittleEndian(superblock[0x58..]);
        var descriptorSize = (BinaryPrimitives.ReadUInt32LittleEndian(superblock[0x60..]) & 0x80) != 0
            ? BinaryPrimitives.ReadUInt16LittleEndian(superblock[0xFE..]) : 32;
        var group = (inode - 1) / perGroup;
        var table = (blockSize == 1024 ? 2 : 1) * (long)blockSize + group * descriptorSize;
        var inodeTable = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)table + 8));
        return inodeTable * (long)blockSize + (inode - 1) % perGroup * inodeSize;
    }

    private static void Run(string program, IEnumerable<string> arguments, int acceptedExit = 0)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 && process.ExitCode != acceptedExit)
            throw new InvalidOperationException($"{program} exited with {process.ExitCode}: {error}");
    }

    #endregion
}
