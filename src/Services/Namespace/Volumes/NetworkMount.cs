using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using OutWit.OS.FileSystems.Smb;
using OutWit.OS.Namespace.Server;

namespace OutWit.OS.Namespace.Volumes;

/// <summary>
/// A request to mount a share (plan step E2.2), as mount(2) gives it: the source "//server/share", the target — empty,
/// or /media/&lt;alias&gt; — the type "smb3" or "cifs", and the options of Linux's mount.cifs, comma-separated with a
/// comma in a value doubled: username, password, domain, ro or rw, ip (the address the mount command resolved, as
/// mount.cifs passes it to the kernel), port, and vers, which names a dialect the client speaks. The service resolves no
/// name: a source that names its server by a name needs ip.
/// </summary>
/// <param name="Server">The server as the source names it, which the share paths and the service's principal name use.</param>
/// <param name="Share">The share.</param>
/// <param name="Address">The server's address: an IPv4 or IPv6 literal.</param>
/// <param name="Port">The server's port.</param>
/// <param name="Credentials">The account.</param>
/// <param name="ReadOnly">Whether the mount refuses every change.</param>
/// <param name="Alias">The alias the target asks for, or null for the share's name.</param>
internal sealed record NetworkMount(string Server, string Share, string Address, ushort Port, SmbCredentials Credentials, bool ReadOnly, string? Alias)
{
    #region Constants

    /// <summary>
    /// The port of SMB over TCP.
    /// </summary>
    public const ushort SMB_PORT = 445;

    private static readonly string[] TYPES = ["smb3", "cifs", "smb"];

    private static readonly string[] DIALECTS = ["3", "3.0", "3.02", "3.0.2", "3.1.1", "3.11", "default"];

    #endregion

    #region Functions

    /// <summary>
    /// Reads a request.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="target">The target, or empty.</param>
    /// <param name="type">The file system type.</param>
    /// <param name="options">The options.</param>
    /// <param name="readOnly">Whether mount(2)'s flags said MS_RDONLY.</param>
    /// <returns>The request.</returns>
    /// <exception cref="NotSupportedException">The type is not SMB's (ENODEV, as Linux answers an unknown type).</exception>
    /// <exception cref="ArgumentException">The source, the target or an option is malformed (EINVAL).</exception>
    public static NetworkMount Parse(string source, string target, string type, string options, bool readOnly)
    {
        if (Array.IndexOf(TYPES, type) < 0)
            throw new NotSupportedException($"The file system type '{type}' is not one the namespace mounts over the network (smb3, cifs).");
        var unc = source.Replace('\\', '/');
        if (!unc.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException($"The source '{source}' is no share: //server/share.");
        var parts = unc[2..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            throw new ArgumentException($"The source '{source}' names {(parts.Length < 2 ? "no share" : "a directory below its share, which the namespace does not mount")}.");
        var (server, share) = (parts[0], parts[1]);

        string? user = null, password = null, domain = null, address = null;
        var port = SMB_PORT;
        foreach (var (key, value) in Options(options))
        {
            switch (key)
            {
                case "username" or "user":
                    user = value;
                    break;
                case "password" or "pass":
                    password = value;
                    break;
                case "domain" or "dom" or "workgroup":
                    domain = value;
                    break;
                case "ro":
                    readOnly = true;
                    break;
                case "rw":
                    readOnly = false;
                    break;
                case "ip" or "addr":
                    address = value;
                    break;
                case "port":
                    if (!ushort.TryParse(value, out port) || port == 0)
                        throw new ArgumentException($"The port '{value}' is no port.");
                    break;
                case "vers":
                    if (Array.IndexOf(DIALECTS, value) < 0)
                        throw new ArgumentException($"vers={value} is no dialect of SMB 3, the one the client speaks.");
                    break;
                default:
                    break; // as Linux's cifs, an option it does not know is left
            }
        }
        address ??= server.Trim('[', ']');
        if (!IPAddress.TryParse(address, out _))
            throw new ArgumentException($"The server '{server}' is a name, and the namespace resolves none: the mount command passes its address as ip=.");
        if (string.IsNullOrEmpty(user))
            throw new ArgumentException("The mount names no username: a share takes no guest, which cannot sign.");
        return new NetworkMount(server, share, address, port, new SmbCredentials(user, password ?? "", domain ?? ""), readOnly, AliasOf(target));
    }

    // mount.cifs's options: comma-separated, a comma in a value doubled, each a key or a key=value.
    private static IEnumerable<(string Key, string Value)> Options(string options)
    {
        var current = new StringBuilder();
        for (var i = 0; i <= options.Length; i++)
        {
            if (i < options.Length && options[i] == ',' && i + 1 < options.Length && options[i + 1] == ',')
            {
                current.Append(',');
                i++;
                continue;
            }
            if (i < options.Length && options[i] != ',')
            {
                current.Append(options[i]);
                continue;
            }
            var option = current.ToString();
            current.Clear();
            if (option.Length == 0)
                continue;
            var equals = option.IndexOf('=');
            yield return equals < 0 ? (option.ToLowerInvariant(), "") : (option[..equals].ToLowerInvariant(), option[(equals + 1)..]);
        }
    }

    // The alias a target asks for: none for an empty target, the name of /media/<name> otherwise.
    private static string? AliasOf(string target)
    {
        if (target.Length == 0)
            return null;
        var prefix = MountTable.MEDIA + "/";
        var name = target.TrimEnd('/');
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length == prefix.Length || name.IndexOf('/', prefix.Length) >= 0)
            throw new ArgumentException($"The target '{target}' is not below {MountTable.MEDIA}, where a network volume mounts.");
        name = name[prefix.Length..];
        if (VolumeCatalog.Stem(name) != name)
            throw new ArgumentException($"'{name}' is no alias: letters, digits, '-' and '_', at most 31.");
        return name;
    }

    /// <inheritdoc />
    public override string ToString() => $"//{Server}/{Share} at {Address}:{Port} as {Credentials}"; // never the password

    #endregion
}
