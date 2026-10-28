using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace WitOS.Volumes;

/// <summary>
/// witos-volumes (plan step E2.2, ADR 0032): the volumes of the namespace's catalog as /etc/mtab lists them, with their
/// sizes; a share of a Windows or Samba server mounted below /media through the libc's mount(2), its server's name
/// resolved here as mount.cifs resolves it, its password read from a file or asked without echo; and a share unmounted
/// through umount2(2). WitOS's own command, in the boot package as /usr/bin/witos-volumes.
/// </summary>
internal static partial class Program
{
    #region Constants

    private const string MTAB = "/etc/mtab";

    private const ulong MS_RDONLY = 1;
    private const int MNT_DETACH = 2;

    private const string USAGE = """
        witos-volumes                         the volumes and their space
        witos-volumes mount //server/share --user USER [--domain DOMAIN] [--as NAME] [--port PORT]
                                              [--password-file FILE] [--read-only]
                                              a share of a Windows or Samba server below /media
        witos-volumes unmount NAME|/media/NAME [--detach]
                                              a share unmounted; --detach leaves its open files to fail
        """;

    #endregion

    #region Functions

    private static int Main(string[] args)
    {
        try
        {
            return args switch
            {
                [] or ["list"] => List(),
                ["mount", var source, .. var options] => Mount(ShareMount.Parse(source, options)),
                ["unmount" or "umount", var target, .. var options] => Unmount(target, options),
                ["--help" or "-h" or "help"] => Usage(Console.Out, 0),
                _ => Usage(Console.Error, 2)
            };
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"witos-volumes: {exception.Message}");
            return 2;
        }
        catch (Exception exception) when (exception is IOException or SocketException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"witos-volumes: {exception.Message}");
            return 1;
        }
    }

    // The volumes as /etc/mtab lists them, with their size and free space.
    private static int List()
    {
        Console.WriteLine($"{"Volume",-16} {"Mounted at",-24} {"Type",-6} {"Size",10} {"Free",10}  Access");
        foreach (var volume in Mounts(File.ReadAllLines(MTAB)).Where(volume => volume.Point != "/"))
        {
            string size = "", free = "";
            try
            {
                var drive = new DriveInfo(volume.Point);
                (size, free) = (Bytes(drive.TotalSize), Bytes(drive.AvailableFreeSpace));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
            Console.WriteLine($"{volume.Alias,-16} {volume.Point,-24} {volume.Type,-6} {size,10} {free,10}  {volume.Access}");
        }
        return 0;
    }

    // A share mounted: the server's address resolved unless the source names it by one, the password read, mount(2).
    private static int Mount(ShareMount share)
    {
        string? address = null;
        if (!IPAddress.TryParse(share.Server.Trim('[', ']'), out _))
        {
            var addresses = Dns.GetHostAddresses(share.Server);
            var chosen = addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()
                ?? throw new IOException($"{share.Server} has no address.");
            address = chosen.ToString();
        }
        var password = Password(share);
        var before = Mounts(File.ReadAllLines(MTAB)).Select(volume => volume.Point).ToHashSet(StringComparer.Ordinal);
        if (MountSystem(share.Source, share.Target, ShareMount.TYPE, share.ReadOnly ? MS_RDONLY : 0, share.Options(password, address)) != 0)
            return Failed($"mount of {share}", Marshal.GetLastPInvokeError());
        var mounted = Mounts(File.ReadAllLines(MTAB)).FirstOrDefault(volume => !before.Contains(volume.Point));
        Console.WriteLine($"Mounted {share.Source} at {mounted?.Point ?? share.Target ?? "?"}{(share.ReadOnly ? ", read-only" : "")}.");
        return 0;
    }

    private static int Unmount(string target, IReadOnlyList<string> options)
    {
        var detach = options switch
        {
            [] => false,
            ["--detach"] => true,
            _ => throw new ArgumentException($"'{string.Join(' ', options)}' is no option of unmount.")
        };
        var point = target.Contains('/') ? target : $"{ShareMount.MEDIA}/{target}";
        if (UnmountSystem(point, detach ? MNT_DETACH : 0) != 0)
            return Failed($"unmount of {point}", Marshal.GetLastPInvokeError());
        Console.WriteLine($"Unmounted {point}.");
        return 0;
    }

    // The password: the first line of its file, or asked without echo on a terminal, or the first line of the input.
    private static string Password(ShareMount share)
    {
        if (share.PasswordFile is { } file)
            return File.ReadLines(file).FirstOrDefault() ?? "";
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? "";
        Console.Write($"Password for {share}: ");
        var password = new StringBuilder();
        for (var key = Console.ReadKey(intercept: true); key.Key != ConsoleKey.Enter; key = Console.ReadKey(intercept: true))
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                    password.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
                password.Append(key.KeyChar);
        }
        Console.WriteLine();
        return password.ToString();
    }

    /// <summary>
    /// The volumes of /etc/mtab's lines: the alias, the mount point, the type and the access.
    /// </summary>
    /// <param name="lines">The lines.</param>
    /// <returns>The volumes.</returns>
    internal static IReadOnlyList<MountedVolume> Mounts(IEnumerable<string> lines) =>
    [
        .. lines.Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length >= 4)
            .Select(fields => new MountedVolume(fields[0], fields[1], fields[2], fields[3].Split(',').Contains("ro") ? "ro" : "rw"))
    ];

    /// <summary>
    /// A size as people read it: bytes, KiB, MiB, GiB, TiB.
    /// </summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The text.</returns>
    internal static string Bytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value.ToString("0.0", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    // A failure as Linux's mount.cifs words it, with what the errno means here.
    private static int Failed(string what, int error)
    {
        var meaning = error switch
        {
            13 => "the server refused the account or its password",
            2 => "the server has no such share",
            111 => "nothing listens on the server's SMB port",
            110 => "the server did not answer",
            113 or 101 => "the server cannot be reached",
            100 => "the system has no network",
            16 => "the name is taken, or files of the share are open",
            19 => "the namespace mounts no such type",
            22 => "the namespace refused the request",
            _ => Marshal.GetPInvokeErrorMessage(error)
        };
        Console.Error.WriteLine($"witos-volumes: {what} failed: {meaning} (errno {error}).");
        return 1;
    }

    private static int Usage(TextWriter writer, int code)
    {
        writer.Write(USAGE);
        return code;
    }

    [LibraryImport("libc", EntryPoint = "mount", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MountSystem(string source, string? target, string type, ulong flags, string data);

    [LibraryImport("libc", EntryPoint = "umount2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnmountSystem(string target, int flags);

    #endregion
}

/// <summary>
/// A volume of /etc/mtab.
/// </summary>
/// <param name="Alias">Its name, the line's first field.</param>
/// <param name="Point">Where it is mounted.</param>
/// <param name="Type">Its type as Linux names it.</param>
/// <param name="Access">"ro" or "rw".</param>
internal sealed record MountedVolume(string Alias, string Point, string Type, string Access);
