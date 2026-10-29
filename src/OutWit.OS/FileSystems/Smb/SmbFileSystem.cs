using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.FileSystems.Smb;

/// <summary>
/// A share of an SMB 3 server as a filesystem (plan step E2, ADR 0032: a network volume of the catalog): paths below the
/// share's root with '\' between names, each operation a CREATE of what it acts on, its request and a CLOSE, as Windows'
/// own redirector does without caching (MS-SMB2 3.2.4.3). The server keeps the volume's consistency, so a check finds
/// nothing and a flush of the volume has nothing to write; a handle's flush is SMB's FLUSH. A share records the
/// read-only attribute and the times; a mode says read-only or not, as on FAT. Another client's changes are seen at the
/// next request: nothing is cached but an open handle's length, which its own writes and cuts keep.
/// </summary>
public sealed class SmbFileSystem : IFileSystem
{
    #region Constants

    /// <summary>
    /// The format a mounted share names: SMB 3 of any of its dialects, which the mount line names.
    /// </summary>
    public const string FORMAT = "SMB3";

    // Access rights of MS-SMB2 2.2.13.1 (MS-DTYP 2.4.3).
    internal const uint FILE_READ_DATA = 0x00000001;
    internal const uint FILE_WRITE_DATA = 0x00000002;
    internal const uint FILE_READ_ATTRIBUTES = 0x00000080;
    internal const uint FILE_WRITE_ATTRIBUTES = 0x00000100;
    internal const uint DELETE = 0x00010000;
    internal const uint SYNCHRONIZE = 0x00100000;
    internal const uint GENERIC_READ_DATA = 0x00120089; // FILE_GENERIC_READ
    internal const uint GENERIC_WRITE_DATA = 0x00120116; // FILE_GENERIC_WRITE

    private const uint SHARE_ALL = 0x7; // read, write and delete: POSIX opens share everything

    private const uint DISPOSITION_OPEN = 1;
    private const uint DISPOSITION_CREATE = 2;
    private const uint DISPOSITION_OPEN_IF = 3;
    private const uint DISPOSITION_OVERWRITE = 4;
    private const uint DISPOSITION_OVERWRITE_IF = 5;

    private const uint OPTION_DIRECTORY = 0x00000001;
    private const uint OPTION_NON_DIRECTORY = 0x00000040;

    internal const uint ATTRIBUTE_READONLY = 0x00000001;
    private const uint ATTRIBUTE_HIDDEN = 0x00000002;
    private const uint ATTRIBUTE_SYSTEM = 0x00000004;
    private const uint ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint ATTRIBUTE_ARCHIVE = 0x00000020;
    private const uint ATTRIBUTE_NORMAL = 0x00000080;

    private const byte INFO_FILE = 1;
    private const byte INFO_FILESYSTEM = 2;
    private const byte CLASS_DIRECTORY_INFORMATION = 1;
    private const byte CLASS_BASIC = 4;
    private const byte CLASS_RENAME = 10;
    private const byte CLASS_DISPOSITION = 13;
    internal const byte CLASS_END_OF_FILE = 20;
    private const byte CLASS_FS_FULL_SIZE = 7;

    private const byte RESTART_SCANS = 0x01;
    private const int LISTING_BYTES = 65536;

    #endregion

    #region Fields

    private readonly SmbConnection m_connection;
    private readonly uint m_tree;
    private int m_disposed;

    #endregion

    #region Constructors

    private SmbFileSystem(SmbConnection connection, uint tree, string share, FileSystemSpace space, int unit, bool readOnly)
    {
        m_connection = connection;
        m_tree = tree;
        Share = share;
        // A share's identity: its server's GUID and its name, which another mount of the same share repeats.
        var name = Encoding.UTF8.GetBytes(share.ToUpperInvariant());
        var digest = SHA256.HashData([.. connection.ServerGuid.ToByteArray(), .. name]);
        Volume = new FileSystemVolume(FORMAT, share, space.TotalBytes, unit, readOnly, new Guid(digest.AsSpan(0, 16)).ToString());
    }

    #endregion

    #region Functions

    /// <summary>
    /// Logs on to a server over a connected stream and mounts one of its shares.
    /// </summary>
    /// <param name="stream">The stream to the server's port 445, which the filesystem owns from now on.</param>
    /// <param name="server">The server's name or address, as share paths name it.</param>
    /// <param name="share">The share.</param>
    /// <param name="credentials">The account.</param>
    /// <param name="readOnly">Whether to refuse every change, whatever the account may do.</param>
    /// <param name="cancellationToken">Cancels the mount.</param>
    /// <returns>The filesystem.</returns>
    /// <exception cref="FileSystemException">The server refused the account (AccessDenied) or has no such share (NotFound).</exception>
    /// <exception cref="IOException">The server or the stream failed, or the server does not speak SMB 3 with signing.</exception>
    public static async ValueTask<SmbFileSystem> MountAsync(Stream stream, string server, string share, SmbCredentials credentials, bool readOnly = false,
        CancellationToken cancellationToken = default)
    {
        var connection = await SmbConnection.ConnectAsync(stream, server, credentials, cancellationToken).ConfigureAwait(false);
        try
        {
            var (tree, access) = await connection.TreeConnectAsync(share, cancellationToken).ConfigureAwait(false);
            var (space, unit) = await SpaceAsync(connection, tree, cancellationToken).ConfigureAwait(false);
            return new SmbFileSystem(connection, tree, share, space, unit, readOnly || (access & FILE_WRITE_DATA) == 0);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default)
    {
        Opened opened;
        try
        {
            opened = await CreateAsync(path, FILE_READ_ATTRIBUTES | SYNCHRONIZE, DISPOSITION_OPEN, 0, 0, cancellationToken).ConfigureAwait(false);
        }
        catch (FileSystemException exception) when (exception.Error is FileSystemError.NotFound or FileSystemError.NotADirectory)
        {
            return null;
        }
        await CloseAsync(opened.FileId, cancellationToken).ConfigureAwait(false);
        return opened.Entry;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<FileSystemEntry>> EnumerateAsync(string path, CancellationToken cancellationToken = default)
    {
        var opened = await CreateAsync(path, FILE_READ_DATA | FILE_READ_ATTRIBUTES | SYNCHRONIZE, DISPOSITION_OPEN, OPTION_DIRECTORY, 0, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var entries = new List<FileSystemEntry>();
            var pattern = Encoding.Unicode.GetBytes("*");
            for (var first = true; ; first = false)
            {
                var body = new byte[32 + pattern.Length];
                BinaryPrimitives.WriteUInt16LittleEndian(body, 33);
                body[2] = CLASS_DIRECTORY_INFORMATION;
                body[3] = first ? RESTART_SCANS : (byte)0;
                opened.FileId.CopyTo(body, 8);
                BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(24), SmbConnection.HEADER + 32);
                BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(26), (ushort)pattern.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(28), (uint)Math.Min(LISTING_BYTES, m_connection.MaxTransactSize));
                pattern.CopyTo(body, 32);
                var response = await m_connection.RequestAsync(SmbConnection.QUERY_DIRECTORY, body, m_tree, 1, cancellationToken, SmbStatus.NO_MORE_FILES)
                    .ConfigureAwait(false);
                if (response.Status == SmbStatus.NO_MORE_FILES)
                    return entries;
                var listing = response.Buffer(BinaryPrimitives.ReadUInt16LittleEndian(response.Body[2..]), (int)BinaryPrimitives.ReadUInt32LittleEndian(response.Body[4..]));
                for (var at = 0; ;)
                {
                    var record = listing[at..];
                    if (record.Length < 64)
                        throw new SmbException(0, "A directory listing's record is short.");
                    var nameLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[60..]);
                    var name = Encoding.Unicode.GetString(record.Slice(64, nameLength));
                    if (name is not "." and not "..")
                    {
                        entries.Add(Entry(Join(path, name), BinaryPrimitives.ReadUInt32LittleEndian(record[56..]), BinaryPrimitives.ReadInt64LittleEndian(record[8..]),
                            BinaryPrimitives.ReadInt64LittleEndian(record[16..]), BinaryPrimitives.ReadInt64LittleEndian(record[24..]),
                            BinaryPrimitives.ReadInt64LittleEndian(record[40..])));
                    }
                    var next = (int)BinaryPrimitives.ReadUInt32LittleEndian(record);
                    if (next == 0)
                        break;
                    at += next;
                }
            }
        }
        finally
        {
            await CloseAsync(opened.FileId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask<string> ReadLinkAsync(string path, CancellationToken cancellationToken = default) =>
        throw new FileSystemException(FileSystemError.NotALink, $"{path} is no link: a share shows a link as what it points to.");

    /// <inheritdoc />
    public async ValueTask<IFileHandle> OpenAsync(string path, FileMode mode, FileAccess access, CancellationToken cancellationToken = default)
    {
        var disposition = mode switch
        {
            FileMode.CreateNew => DISPOSITION_CREATE,
            FileMode.Create => DISPOSITION_OVERWRITE_IF,
            FileMode.Open => DISPOSITION_OPEN,
            FileMode.OpenOrCreate => DISPOSITION_OPEN_IF,
            FileMode.Truncate => DISPOSITION_OVERWRITE,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "A share's file opens by offset; Append is the caller's.")
        };
        var writes = (access & FileAccess.Write) != 0 || disposition is not DISPOSITION_OPEN;
        if (writes && Volume.IsReadOnly)
            throw ReadOnly(path);
        var desired = FILE_READ_ATTRIBUTES | SYNCHRONIZE | ((access & FileAccess.Read) != 0 ? GENERIC_READ_DATA : 0) |
            ((access & FileAccess.Write) != 0 ? GENERIC_WRITE_DATA : 0);
        var opened = await CreateAsync(path, desired, disposition, OPTION_NON_DIRECTORY, ATTRIBUTE_NORMAL, cancellationToken).ConfigureAwait(false);
        return new SmbFileHandle(this, opened.FileId, opened.Entry.Length, (access & FileAccess.Read) != 0, (access & FileAccess.Write) != 0);
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemEntry> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        if (Volume.IsReadOnly)
            throw ReadOnly(path);
        // Each missing directory from the root down, as mkdir -p makes them.
        var current = "";
        Opened? opened = null;
        foreach (var name in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + name;
            opened = await CreateAsync(current, FILE_READ_ATTRIBUTES | SYNCHRONIZE, DISPOSITION_OPEN_IF, OPTION_DIRECTORY, ATTRIBUTE_DIRECTORY, cancellationToken)
                .ConfigureAwait(false);
            await CloseAsync(opened.FileId, cancellationToken).ConfigureAwait(false);
        }
        return opened?.Entry ?? await GetEntryAsync("/", cancellationToken).ConfigureAwait(false) ?? throw new SmbException(0, "The share's root is gone.");
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        if (Volume.IsReadOnly)
            throw ReadOnly(path);
        if (Name(path).Length == 0)
            throw new FileSystemException(FileSystemError.AccessDenied, "The share's root is not deleted.");
        var opened = await CreateAsync(path, DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE, DISPOSITION_OPEN, 0, 0, cancellationToken).ConfigureAwait(false);
        try
        {
            await SetInfoAsync(opened.FileId, CLASS_DISPOSITION, [1], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await CloseAsync(opened.FileId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemEntry> RenameAsync(string source, string destination, CancellationToken cancellationToken = default)
    {
        if (Volume.IsReadOnly)
            throw ReadOnly(source);
        var target = Encoding.Unicode.GetBytes(Name(destination));
        var opened = await CreateAsync(source, DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE, DISPOSITION_OPEN, 0, 0, cancellationToken).ConfigureAwait(false);
        try
        {
            // FILE_RENAME_INFORMATION for SMB 2 (MS-FSCC 2.4.37.2): no replacing, no root directory, the share's path.
            var information = new byte[20 + target.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(information.AsSpan(16), (uint)target.Length);
            target.CopyTo(information, 20);
            await SetInfoAsync(opened.FileId, CLASS_RENAME, information, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await CloseAsync(opened.FileId, CancellationToken.None).ConfigureAwait(false);
        }
        return await GetEntryAsync(destination, cancellationToken).ConfigureAwait(false)
            ?? throw new SmbException(0, $"{destination} is not there after the rename.");
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemEntry> SetModeAsync(string path, int mode, CancellationToken cancellationToken = default)
    {
        if (Volume.IsReadOnly)
            throw ReadOnly(path);
        var opened = await CreateAsync(path, FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES | SYNCHRONIZE, DISPOSITION_OPEN, 0, 0, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            // A share records read-only alone, as FAT does: 0444/0644 for a file, 0555/0755 for a directory.
            var readOnly = (mode & 0x92) == 0;
            var kept = opened.Entry.IsDirectory ? (readOnly ? 0x16D : 0x1ED) : readOnly ? 0x124 : 0x1A4;
            if ((mode & 0xFFF) != kept)
                throw new FileSystemException(FileSystemError.NotPermitted,
                    $"A share records read-only alone: the mode {Convert.ToString(mode & 0xFFF, 8)} is not {Convert.ToString(kept, 8)}.");
            var attributes = readOnly ? opened.Attributes | ATTRIBUTE_READONLY : opened.Attributes & ~ATTRIBUTE_READONLY;
            if (attributes != opened.Attributes)
                await SetInfoAsync(opened.FileId, CLASS_BASIC, Basic(0, 0, attributes == 0 ? ATTRIBUTE_NORMAL : attributes), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await CloseAsync(opened.FileId, CancellationToken.None).ConfigureAwait(false);
        }
        return await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileSystemException(FileSystemError.NotFound, $"{path} is gone.");
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemEntry> SetTimesAsync(string path, DateTimeOffset? accessed, DateTimeOffset? modified,
        CancellationToken cancellationToken = default)
    {
        if (Volume.IsReadOnly)
            throw ReadOnly(path);
        var opened = await CreateAsync(path, FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES | SYNCHRONIZE, DISPOSITION_OPEN, 0, 0, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            // A time of zero leaves the server's.
            await SetInfoAsync(opened.FileId, CLASS_BASIC, Basic(accessed?.UtcDateTime.ToFileTimeUtc() ?? 0, modified?.UtcDateTime.ToFileTimeUtc() ?? 0, 0),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await CloseAsync(opened.FileId, CancellationToken.None).ConfigureAwait(false);
        }
        return await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileSystemException(FileSystemError.NotFound, $"{path} is gone.");
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask; // the handles flush what they wrote

    /// <inheritdoc />
    public ValueTask<FileSystemCheckReport> CheckAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new FileSystemCheckReport([], 0, 0)); // the server keeps its volume's structures

    /// <inheritdoc />
    public async ValueTask<FileSystemSpace> GetSpaceAsync(CancellationToken cancellationToken = default) =>
        (await SpaceAsync(m_connection, m_tree, cancellationToken).ConfigureAwait(false)).Space;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref m_disposed, 1) != 0)
            return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await m_connection.TreeDisconnectAsync(m_tree, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // A server gone already leaves nothing to disconnect from.
        }
        await m_connection.DisposeAsync().ConfigureAwait(false);
    }

    internal Task<SmbResponse> RequestAsync(ushort command, byte[] body, int creditCharge, CancellationToken cancellationToken, params uint[] accepted) =>
        m_connection.RequestAsync(command, body, m_tree, creditCharge, cancellationToken, accepted);

    internal async Task SetInfoAsync(byte[] fileId, byte informationClass, byte[] information, CancellationToken cancellationToken)
    {
        var body = new byte[32 + information.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 33);
        body[2] = INFO_FILE;
        body[3] = informationClass;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (uint)information.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8), SmbConnection.HEADER + 32);
        fileId.CopyTo(body, 16);
        information.CopyTo(body, 32);
        await RequestAsync(SmbConnection.SET_INFO, body, 1, cancellationToken).ConfigureAwait(false);
    }

    internal async Task CloseAsync(byte[] fileId, CancellationToken cancellationToken)
    {
        var body = new byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 24);
        fileId.CopyTo(body, 8);
        try
        {
            await RequestAsync(SmbConnection.CLOSE, body, 1, cancellationToken, SmbStatus.FILE_CLOSED).ConfigureAwait(false);
        }
        catch (SmbException)
        {
            // The share went first (unmounted or dropped): the server closed the handle with the session.
        }
    }

    // CREATE (MS-SMB2 2.2.13) of a path with the access, disposition, options and attributes asked; no oplock, sharing
    // everything; its answer's times, length and attributes become the entry.
    private async Task<Opened> CreateAsync(string path, uint access, uint disposition, uint options, uint attributes, CancellationToken cancellationToken)
    {
        var name = Encoding.Unicode.GetBytes(Name(path));
        var body = new byte[56 + Math.Max(1, name.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 57);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), 2); // impersonation
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(24), access);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(28), attributes);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(32), SHARE_ALL);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(36), disposition);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(40), options);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(44), SmbConnection.HEADER + 56);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(46), (ushort)name.Length);
        name.CopyTo(body, 56);
        var response = await RequestAsync(SmbConnection.CREATE, body, 1, cancellationToken).ConfigureAwait(false);
        var answer = response.Body;
        if (answer.Length < 88)
            throw new SmbException(0, "A CREATE response is short.");
        var fileAttributes = BinaryPrimitives.ReadUInt32LittleEndian(answer[56..]);
        var entry = Entry(Normalized(path), fileAttributes, BinaryPrimitives.ReadInt64LittleEndian(answer[8..]), BinaryPrimitives.ReadInt64LittleEndian(answer[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(answer[24..]), BinaryPrimitives.ReadInt64LittleEndian(answer[48..]));
        return new Opened(answer.Slice(64, 16).ToArray(), entry, fileAttributes);
    }

    // The share's size and free space (FileFsFullSizeInformation of its root) and its allocation unit.
    private static async Task<(FileSystemSpace Space, int Unit)> SpaceAsync(SmbConnection connection, uint tree, CancellationToken cancellationToken)
    {
        var create = new byte[57];
        BinaryPrimitives.WriteUInt16LittleEndian(create, 57);
        BinaryPrimitives.WriteUInt32LittleEndian(create.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(create.AsSpan(24), FILE_READ_ATTRIBUTES | SYNCHRONIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(create.AsSpan(32), SHARE_ALL);
        BinaryPrimitives.WriteUInt32LittleEndian(create.AsSpan(36), DISPOSITION_OPEN);
        BinaryPrimitives.WriteUInt32LittleEndian(create.AsSpan(40), OPTION_DIRECTORY);
        BinaryPrimitives.WriteUInt16LittleEndian(create.AsSpan(44), SmbConnection.HEADER + 56);
        var opened = await connection.RequestAsync(SmbConnection.CREATE, create, tree, 1, cancellationToken).ConfigureAwait(false);
        var fileId = opened.Body.Slice(64, 16).ToArray();
        try
        {
            var query = new byte[41];
            BinaryPrimitives.WriteUInt16LittleEndian(query, 41);
            query[2] = INFO_FILESYSTEM;
            query[3] = CLASS_FS_FULL_SIZE;
            BinaryPrimitives.WriteUInt32LittleEndian(query.AsSpan(4), 32);
            fileId.CopyTo(query, 24);
            var response = await connection.RequestAsync(SmbConnection.QUERY_INFO, query, tree, 1, cancellationToken).ConfigureAwait(false);
            var size = response.Buffer(BinaryPrimitives.ReadUInt16LittleEndian(response.Body[2..]), (int)BinaryPrimitives.ReadUInt32LittleEndian(response.Body[4..]));
            if (size.Length < 32)
                throw new SmbException(0, "The share's size information is short.");
            var unit = (long)BinaryPrimitives.ReadUInt32LittleEndian(size[24..]) * BinaryPrimitives.ReadUInt32LittleEndian(size[28..]);
            var total = BinaryPrimitives.ReadInt64LittleEndian(size) * unit;
            var free = BinaryPrimitives.ReadInt64LittleEndian(size[8..]) * unit; // what this account may still use
            return (new FileSystemSpace(total, free), (int)Math.Clamp(unit, 512, 1 << 20));
        }
        finally
        {
            var close = new byte[24];
            BinaryPrimitives.WriteUInt16LittleEndian(close, 24);
            fileId.CopyTo(close, 8);
            await connection.RequestAsync(SmbConnection.CLOSE, close, tree, 1, CancellationToken.None, SmbStatus.FILE_CLOSED).ConfigureAwait(false);
        }
    }

    // FILE_BASIC_INFORMATION (MS-FSCC 2.4.7): the creation and change times left, the access and write times and the
    // attributes as given, zero leaving each.
    private static byte[] Basic(long accessed, long written, uint attributes)
    {
        var information = new byte[40];
        BinaryPrimitives.WriteInt64LittleEndian(information.AsSpan(8), accessed);
        BinaryPrimitives.WriteInt64LittleEndian(information.AsSpan(16), written);
        BinaryPrimitives.WriteUInt32LittleEndian(information.AsSpan(32), attributes);
        return information;
    }

    private static FileSystemEntry Entry(string path, uint attributes, long created, long accessed, long written, long length)
    {
        var directory = (attributes & ATTRIBUTE_DIRECTORY) != 0;
        var name = path == "/" ? "" : path[(path.LastIndexOf('/') + 1)..];
        var kept = (attributes & ATTRIBUTE_READONLY) != 0 ? FileSystemAttributes.ReadOnly : FileSystemAttributes.None;
        kept |= (attributes & ATTRIBUTE_HIDDEN) != 0 ? FileSystemAttributes.Hidden : FileSystemAttributes.None;
        kept |= (attributes & ATTRIBUTE_SYSTEM) != 0 ? FileSystemAttributes.System : FileSystemAttributes.None;
        kept |= (attributes & ATTRIBUTE_ARCHIVE) != 0 ? FileSystemAttributes.Archive : FileSystemAttributes.None;
        return new FileSystemEntry(path, name, directory ? FileSystemEntryKind.Directory : FileSystemEntryKind.File, directory ? 0 : length, kept,
            Time(created), Time(written), Time(accessed));
    }

    private static DateTimeOffset? Time(long fileTime) => fileTime <= 0 ? null : new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime), TimeSpan.Zero);

    // A volume's path as the share names it: below its root, '\' between names; a name may not hold '\'.
    private static string Name(string path)
    {
        if (!path.StartsWith('/'))
            throw new FileSystemException(FileSystemError.InvalidName, $"{path} is not absolute within the volume.");
        var name = path.Trim('/');
        if (name.Contains('\\'))
            throw new FileSystemException(FileSystemError.InvalidName, $"{path} holds a '\\', which a share takes for a separator.");
        return name.Replace('/', '\\');
    }

    private static string Normalized(string path) => "/" + path.Trim('/');

    private static string Join(string directory, string name) => directory.TrimEnd('/') + "/" + name;

    private FileSystemException ReadOnly(string path) => new(FileSystemError.ReadOnly, $"//{m_connection.Server}/{Share} is mounted read-only: {path}.");

    #endregion

    #region Properties

    /// <inheritdoc />
    public FileSystemVolume Volume { get; }

    /// <summary>
    /// The share's name.
    /// </summary>
    public string Share { get; }

    /// <summary>
    /// The server's name or address.
    /// </summary>
    public string Server => m_connection.Server;

    /// <summary>
    /// The dialect the connection speaks, as "3.1.1".
    /// </summary>
    public string Dialect => m_connection.Dialect switch
    {
        SmbConnection.DIALECT_311 => "3.1.1",
        SmbConnection.DIALECT_302 => "3.0.2",
        _ => "3.0"
    };

    internal SmbConnection Connection => m_connection;

    #endregion

    #region Types

    // An open of CREATE: its file id, its entry and its attributes as the server keeps them.
    private sealed record Opened(byte[] FileId, FileSystemEntry Entry, uint Attributes);

    #endregion
}
