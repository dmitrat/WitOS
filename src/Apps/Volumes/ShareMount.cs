using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WitOS.Volumes;

/// <summary>
/// What witos-volumes asks of mount(2) for a share (plan step E2.2): the source "//server/share", the target below
/// /media when a name is asked, the type "smb3", and the options of Linux's mount.cifs — username, password, domain, the
/// address the command resolved (ip), the port, ro — each value's commas doubled, as mount.cifs escapes them.
/// </summary>
/// <param name="Source">The share as //server/share.</param>
/// <param name="Server">The server's name or address.</param>
/// <param name="Name">The name asked for below /media, or null for the share's.</param>
/// <param name="User">The account.</param>
/// <param name="Domain">Its domain, or empty.</param>
/// <param name="Port">The server's port, or 0 for SMB's.</param>
/// <param name="ReadOnly">Whether the share mounts read-only.</param>
/// <param name="PasswordFile">Where the password is, or null to ask for it.</param>
internal sealed record ShareMount(string Source, string Server, string? Name, string User, string Domain, int Port, bool ReadOnly, string? PasswordFile)
{
    #region Constants

    /// <summary>
    /// The file system type the namespace mounts a share as.
    /// </summary>
    public const string TYPE = "smb3";

    /// <summary>
    /// Where every volume that is not the system's mounts.
    /// </summary>
    public const string MEDIA = "/media";

    #endregion

    #region Functions

    /// <summary>
    /// Reads the command's arguments after "mount".
    /// </summary>
    /// <param name="source">The share: //server/share or \\server\share.</param>
    /// <param name="options">--as NAME, --user USER, --domain DOMAIN, --port PORT, --password-file FILE, --read-only.</param>
    /// <returns>The mount.</returns>
    /// <exception cref="ArgumentException">An argument is malformed or missing.</exception>
    public static ShareMount Parse(string source, IReadOnlyList<string> options)
    {
        var unc = source.Replace('\\', '/');
        var parts = unc.StartsWith("//", StringComparison.Ordinal) ? unc[2..].Split('/', StringSplitOptions.RemoveEmptyEntries) : [];
        if (parts.Length != 2)
            throw new ArgumentException($"'{source}' is no share: //server/share.");
        string? name = null, user = null, passwordFile = null;
        var domain = "";
        var port = 0;
        var readOnly = false;
        for (var i = 0; i < options.Count; i++)
        {
            string Value() => i + 1 < options.Count ? options[++i] : throw new ArgumentException($"{options[i]} needs a value.");
            switch (options[i])
            {
                case "--as":
                    name = Value();
                    break;
                case "--user":
                    user = Value();
                    break;
                case "--domain":
                    domain = Value();
                    break;
                case "--port":
                    var text = Value();
                    if (!int.TryParse(text, out port) || port is < 1 or > 65535)
                        throw new ArgumentException($"'{text}' is no port.");
                    break;
                case "--password-file":
                    passwordFile = Value();
                    break;
                case "--read-only":
                    readOnly = true;
                    break;
                default:
                    throw new ArgumentException($"'{options[i]}' is no option of mount.");
            }
        }
        if (user is null)
            throw new ArgumentException("mount needs --user: a share takes no guest.");
        if (name is not null && (name.Length is 0 or > 31 || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')))
            throw new ArgumentException($"'{name}' is no volume name: ASCII letters, digits, '-' and '_', at most 31.");
        return new ShareMount($"//{parts[0]}/{parts[1]}", parts[0], name, user, domain, port, readOnly, passwordFile);
    }

    /// <summary>
    /// mount(2)'s options for the share, with the password and the server's address.
    /// </summary>
    /// <param name="password">The password.</param>
    /// <param name="address">The server's address, or null when the source names it by an address already.</param>
    /// <returns>The options.</returns>
    public string Options(string password, string? address)
    {
        var options = new List<string> { $"username={Escape(User)}", $"password={Escape(password)}" };
        if (Domain.Length > 0)
            options.Add($"domain={Escape(Domain)}");
        if (address is not null)
            options.Add($"ip={address}");
        if (Port != 0)
            options.Add($"port={Port}");
        if (ReadOnly)
            options.Add("ro");
        return string.Join(',', options);
    }

    // A comma in a value doubled, as mount.cifs reads it.
    private static string Escape(string value) => value.Replace(",", ",,", StringComparison.Ordinal);

    /// <inheritdoc />
    public override string ToString()
    {
        var text = new StringBuilder($"{Source} as {(Domain.Length == 0 ? "" : Domain + @"\")}{User}");
        if (ReadOnly)
            text.Append(", read-only");
        return text.ToString(); // never the password
    }

    #endregion

    #region Properties

    /// <summary>
    /// The target mount(2) takes: /media/NAME, or null for the share's own name.
    /// </summary>
    public string? Target => Name is null ? null : $"{MEDIA}/{Name}";

    #endregion
}
