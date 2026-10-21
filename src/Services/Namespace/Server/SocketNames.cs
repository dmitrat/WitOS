using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OutWit.OS.Resources;

namespace OutWit.OS.Namespace.Server;

/// <summary>
/// A listening socket's end of the channel its connections arrive on (plan step W4.1), which the service keeps and
/// duplicates for each client that connects.
/// </summary>
internal interface IListenerEnd : IDisposable
{
    #region Functions

    /// <summary>
    /// A duplicate of the end for a connecting client: SEND and TRANSFER.
    /// </summary>
    /// <returns>The handle, the client's once it moves.</returns>
    KernelHandle Share();

    #endregion
}

/// <summary>
/// Where the service's listener ends come from: the handles clients move in the guest, fakes in a host test.
/// </summary>
internal interface IListenerEndSource
{
    #region Functions

    /// <summary>
    /// Takes a handle a client moved to the service as a listener's end.
    /// </summary>
    /// <param name="handle">The handle, the end's from now on.</param>
    /// <returns>The end.</returns>
    IListenerEnd Adopt(KernelHandle handle);

    #endregion
}

/// <summary>
/// Listener ends over the handles clients move to the service.
/// </summary>
internal sealed class KernelListenerEndSource : IListenerEndSource
{
    #region Functions

    /// <inheritdoc />
    public IListenerEnd Adopt(KernelHandle handle) => new KernelListenerEnd(handle);

    #endregion

    #region Nested Types

    private sealed class KernelListenerEnd(KernelHandle handle) : IListenerEnd
    {
        public KernelHandle Share() => handle.Duplicate(Rights.Send | Rights.Transfer);

        public void Dispose() => handle.Dispose();
    }

    #endregion
}

/// <summary>
/// The names local sockets bind (plan step W4.1, witos/namespace.h): a path below a mount point, which stays until it is
/// unlinked as a socket's file does on Linux, or an abstract name, which goes with its socket. Each binding belongs to
/// the session that made it; a listening one holds the end connections arrive on. Every session shares the table, so
/// each call takes its lock.
/// </summary>
internal sealed class SocketNames
{
    #region Constants

    /// <summary>
    /// The numbers a stat reports for socket names, apart from every volume's inodes and path hashes.
    /// </summary>
    private const long ID_BASE = 1L << 62;

    /// <summary>
    /// SOCK_STREAM, SOCK_SEQPACKET: the types a name may be bound with.
    /// </summary>
    public const uint STREAM = 1;

    /// <inheritdoc cref="STREAM" />
    public const uint SEQUENCED_PACKET = 5;

    #endregion

    #region Fields

    private readonly object m_gate = new();
    private readonly Dictionary<string, Binding> m_names = new(StringComparer.Ordinal);
    private readonly Dictionary<long, Binding> m_bindings = [];
    private long m_next = 1;

    #endregion

    #region Functions

    /// <summary>
    /// The key of an abstract name: a zero byte, which no path starts with, then the name's bytes.
    /// </summary>
    /// <param name="name">The name's bytes.</param>
    /// <returns>The key.</returns>
    public static string AbstractKey(ReadOnlySpan<byte> name) => "\0" + Encoding.Latin1.GetString(name);

    /// <summary>
    /// What a stat reports of a name, or null when no socket has it.
    /// </summary>
    /// <param name="key">The name's path in the namespace, or an abstract key.</param>
    /// <returns>The name.</returns>
    public NameInfo? Find(string key)
    {
        lock (m_gate)
            return m_names.TryGetValue(key, out var binding) ? Info(binding) : null;
    }

    /// <summary>
    /// Binds a name.
    /// </summary>
    /// <param name="key">The name's path in the namespace, or an abstract key.</param>
    /// <param name="type">The socket's type.</param>
    /// <param name="owner">The session that binds it.</param>
    /// <param name="now">The binding's time.</param>
    /// <returns>The binding's id; zero when the name is taken.</returns>
    public long Bind(string key, uint type, object owner, DateTimeOffset now)
    {
        lock (m_gate)
        {
            if (m_names.ContainsKey(key))
                return 0;
            var binding = new Binding(key, type, m_next++, owner, now);
            m_names[key] = binding;
            m_bindings[binding.Id] = binding;
            return binding.Id;
        }
    }

    /// <summary>
    /// A binding's socket listens: connections to its name go to the end given, which the table owns from now on.
    /// </summary>
    /// <param name="id">The binding.</param>
    /// <param name="owner">The session that asks, which must be the binding's.</param>
    /// <param name="end">The end.</param>
    /// <returns>Zero, or EBADF for a binding this session does not hold, when the end is the caller's still.</returns>
    public int Listen(long id, object owner, IListenerEnd end)
    {
        IListenerEnd? previous;
        lock (m_gate)
        {
            if (!m_bindings.TryGetValue(id, out var binding) || binding.Owner != owner)
                return Errno.BAD_HANDLE;
            previous = binding.Listener;
            binding.Listener = end;
        }
        previous?.Dispose();
        return 0;
    }

    /// <summary>
    /// The listener's end for a client that connects to a name.
    /// </summary>
    /// <param name="key">The name.</param>
    /// <param name="type">The client's socket type.</param>
    /// <returns>Zero and the end's duplicate, or ENOENT, ECONNREFUSED or EPROTOTYPE.</returns>
    public (int Error, KernelHandle? End) Connect(string key, uint type)
    {
        lock (m_gate)
        {
            if (!m_names.TryGetValue(key, out var binding))
                return (Errno.NOT_FOUND, null);
            if (binding.Listener is null)
                return (Errno.CONNECTION_REFUSED, null);
            if (binding.Type != type)
                return (Errno.PROTOCOL_TYPE, null);
            return (0, binding.Listener.Share());
        }
    }

    /// <summary>
    /// A binding's socket closed: nobody listens on its name any more, and an abstract name goes; a path stays until it
    /// is unlinked, as a socket's file does.
    /// </summary>
    /// <param name="id">The binding.</param>
    /// <param name="owner">The session that asks, which must be the binding's.</param>
    /// <returns>Zero, or EBADF for a binding this session does not hold.</returns>
    public int Close(long id, object owner)
    {
        IListenerEnd? listener;
        lock (m_gate)
        {
            if (!m_bindings.TryGetValue(id, out var binding) || binding.Owner != owner)
                return Errno.BAD_HANDLE;
            End(binding);
            listener = binding.Listener;
            binding.Listener = null;
        }
        listener?.Dispose();
        return 0;
    }

    /// <summary>
    /// Unlinks a name: no later client finds it, while its socket goes on with the connections it has.
    /// </summary>
    /// <param name="key">The name.</param>
    /// <returns>Whether a socket had it.</returns>
    public bool Remove(string key)
    {
        IListenerEnd? listener = null;
        lock (m_gate)
        {
            if (!m_names.Remove(key, out var binding))
                return false;
            binding.Named = false;
            listener = binding.Listener;
            binding.Listener = null;
        }
        listener?.Dispose();
        return true;
    }

    /// <summary>
    /// Moves a name to another path of its volume, replacing a name there.
    /// </summary>
    /// <param name="from">The name.</param>
    /// <param name="to">Its new path.</param>
    /// <returns>Whether a socket had the name.</returns>
    public bool Move(string from, string to)
    {
        IListenerEnd? replaced = null;
        lock (m_gate)
        {
            if (!m_names.Remove(from, out var binding))
                return false;
            if (m_names.Remove(to, out var previous))
            {
                previous.Named = false;
                replaced = previous.Listener;
                previous.Listener = null;
            }
            binding.Key = to;
            m_names[to] = binding;
        }
        replaced?.Dispose();
        return true;
    }

    /// <summary>
    /// Moves every name below a directory that was renamed.
    /// </summary>
    /// <param name="from">The directory's old path.</param>
    /// <param name="to">Its new path.</param>
    public void MoveBelow(string from, string to)
    {
        lock (m_gate)
        {
            foreach (var binding in m_names.Values.Where(binding => binding.Key.StartsWith(from + "/", StringComparison.Ordinal)).ToList())
            {
                m_names.Remove(binding.Key);
                binding.Key = to + binding.Key[from.Length..];
                m_names[binding.Key] = binding;
            }
        }
    }

    /// <summary>
    /// The names a directory holds, for its listing.
    /// </summary>
    /// <param name="directory">The directory's path in the namespace.</param>
    /// <returns>Each name's last component and what a stat reports of it.</returns>
    public IReadOnlyList<(string Name, NameInfo Info)> In(string directory)
    {
        lock (m_gate)
        {
            return [.. m_names.Values
                .Where(binding => binding.Key.Length > directory.Length + 1 && binding.Key[0] == '/' &&
                    binding.Key.StartsWith(directory + "/", StringComparison.Ordinal) &&
                    binding.Key.IndexOf('/', directory.Length + 1) < 0)
                .OrderBy(binding => binding.Key, StringComparer.Ordinal)
                .Select(binding => (binding.Key[(directory.Length + 1)..], Info(binding)))];
        }
    }

    /// <summary>
    /// Whether any name lies below a directory, which then is not empty.
    /// </summary>
    /// <param name="directory">The directory's path in the namespace.</param>
    /// <returns>Whether one does.</returns>
    public bool AnyBelow(string directory)
    {
        lock (m_gate)
            return m_names.Keys.Any(key => key.StartsWith(directory + "/", StringComparison.Ordinal));
    }

    /// <summary>
    /// Sets the permission bits a stat reports of a name.
    /// </summary>
    /// <param name="key">The name.</param>
    /// <param name="mode">The bits.</param>
    /// <returns>What a stat reports now, or null when no socket has the name.</returns>
    public NameInfo? SetMode(string key, uint mode)
    {
        lock (m_gate)
        {
            if (!m_names.TryGetValue(key, out var binding))
                return null;
            binding.Mode = mode & 0xFFF;
            return Info(binding);
        }
    }

    /// <summary>
    /// A session ended: its sockets listen no more and its abstract names go; its paths stay until they are unlinked.
    /// </summary>
    /// <param name="owner">The session.</param>
    public void Release(object owner)
    {
        List<IListenerEnd> ends = [];
        lock (m_gate)
        {
            foreach (var binding in m_bindings.Values.Where(binding => binding.Owner == owner).ToList())
            {
                End(binding);
                if (binding.Listener is { } listener)
                    ends.Add(listener);
                binding.Listener = null;
            }
        }
        foreach (var end in ends)
            end.Dispose();
    }

    // A binding leaves its session: an abstract name goes with it, a path stays.
    private void End(Binding binding)
    {
        m_bindings.Remove(binding.Id);
        if (binding.Named && binding.Key[0] == '\0')
        {
            m_names.Remove(binding.Key);
            binding.Named = false;
        }
    }

    private static NameInfo Info(Binding binding) => new(ID_BASE | binding.Id, binding.Mode, binding.Bound, binding.Listener is not null);

    #endregion

    #region Nested Types

    /// <summary>
    /// What a stat reports of a socket's name.
    /// </summary>
    /// <param name="Id">The entry's number.</param>
    /// <param name="Mode">The permission bits.</param>
    /// <param name="Bound">When it was bound.</param>
    /// <param name="Listening">Whether a socket listens on it.</param>
    public sealed record NameInfo(long Id, uint Mode, DateTimeOffset Bound, bool Listening);

    private sealed class Binding(string key, uint type, long id, object owner, DateTimeOffset bound)
    {
        public string Key { get; set; } = key;

        public uint Type { get; } = type;

        public long Id { get; } = id;

        public object Owner { get; } = owner;

        public DateTimeOffset Bound { get; } = bound;

        public IListenerEnd? Listener { get; set; }

        public uint Mode { get; set; } = 0x1ED; // 0755: 0777 less Linux's usual umask, as bind makes a socket's file

        public bool Named { get; set; } = true;
    }

    #endregion
}
