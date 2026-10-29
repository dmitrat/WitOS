using System;
using System.Collections.Generic;
using System.Linq;
using OutWit.OS.FileSystems;

namespace OutWit.OS.Namespace.Server;

/// <summary>
/// The volumes the namespace serves, each at a mount point: an absolute path of one component, such as "/data", or a
/// name below /media (E1, ADR 0032), where every volume that is not the system's mounts. A path resolves to the volume
/// of the longest mount point that holds it and the path within it. Volumes come and go while sessions read the table,
/// which they see as a snapshot. The files clients map are the table's too, shared by every session (R4.2), and so are
/// the names local sockets bind (W4.1).
/// </summary>
internal sealed class MountTable
{
    #region Constants

    /// <summary>
    /// The directory below which every volume that is not the system's mounts (E1, ADR 0032 decision 2).
    /// </summary>
    public const string MEDIA = "/media";

    #endregion

    #region Fields

    private readonly object m_gate = new();
    private Mount[] m_mounts = [];
    private uint m_nextIndex;

    #endregion

    #region Constructors

    /// <summary>
    /// A table whose mapped files' chunks are memory objects.
    /// </summary>
    public MountTable()
        : this(new MemoryChunkSource())
    {
    }

    /// <summary>
    /// A table whose mapped files' chunks come from the source given, and whose listening sockets' ends from the other.
    /// </summary>
    /// <param name="chunks">Where the chunks come from.</param>
    /// <param name="listenerEnds">Where the listening sockets' ends come from; the handles clients move by default.</param>
    public MountTable(IChunkSource chunks, IListenerEndSource? listenerEnds = null)
    {
        Mapped = new MappedFiles(chunks);
        ListenerEnds = listenerEnds ?? new KernelListenerEndSource();
        Catalog = new Volumes.VolumeCatalog(this);
    }

    #endregion

    #region Functions

    /// <summary>
    /// Mounts a volume.
    /// </summary>
    /// <param name="point">The mount point: "/" and one component, or /media/ and one (E1).</param>
    /// <param name="volume">The volume.</param>
    /// <returns>The mount.</returns>
    /// <exception cref="ArgumentException">The point is of another form or is taken.</exception>
    public Mount Add(string point, IFileSystem volume)
    {
        if (!IsPoint(point))
            throw new ArgumentException($"'{point}' is no mount point.", nameof(point));
        lock (m_gate)
        {
            if (m_mounts.Any(mount => mount.Point == point))
                throw new ArgumentException($"'{point}' is taken.", nameof(point));
            var mount = new Mount(point, volume, m_nextIndex++);
            m_mounts = [.. m_mounts, mount];
            return mount;
        }
    }

    /// <summary>
    /// Unmounts a volume (E1: a volume that leaves while the service runs); what clients hold of it fails from then on.
    /// </summary>
    /// <param name="point">The mount point.</param>
    /// <returns>The mount, or null when nothing is mounted there.</returns>
    public Mount? Remove(string point)
    {
        lock (m_gate)
        {
            var mount = m_mounts.FirstOrDefault(item => item.Point == point);
            if (mount is not null)
                m_mounts = [.. m_mounts.Where(item => item != mount)];
            return mount;
        }
    }

    /// <summary>
    /// The volume a path lies on and the path within it: the mount with the longest point that holds the path, so that
    /// /media/stick is not /media's.
    /// </summary>
    /// <param name="path">An absolute path of the namespace.</param>
    /// <returns>The mount and the path in its volume, "/" for the mount point itself; null for a path on no volume.</returns>
    public (Mount Mount, string Path)? Resolve(string path)
    {
        (Mount Mount, string Path)? found = null;
        foreach (var mount in m_mounts)
        {
            if (found is { } longer && longer.Mount.Point.Length >= mount.Point.Length)
                continue;
            if (path == mount.Point)
                found = (mount, "/");
            else if (path.Length > mount.Point.Length && path.StartsWith(mount.Point, StringComparison.Ordinal) && path[mount.Point.Length] == '/')
                found = (mount, path[mount.Point.Length..]);
        }
        return found;
    }

    // "/" and one component, or /media/ and one.
    private static bool IsPoint(string point)
    {
        if (point.Length < 2 || point[0] != '/' || point.EndsWith('/'))
            return false;
        var second = point.IndexOf('/', 1);
        return second < 0 || (point[..second] == MEDIA && second + 1 < point.Length && point.IndexOf('/', second + 1) < 0);
    }

    #endregion

    #region Properties

    /// <summary>
    /// The mounts now, in the order they were added: a snapshot the service's sessions read while a volume comes or goes.
    /// </summary>
    public IReadOnlyList<Mount> Mounts => m_mounts;

    /// <summary>
    /// The catalog of the volumes mounted (E1, ADR 0032), which VOLUMES and STATFS report.
    /// </summary>
    public Volumes.VolumeCatalog Catalog { get; }

    /// <summary>
    /// The files clients map (R4.2).
    /// </summary>
    public MappedFiles Mapped { get; }

    /// <summary>
    /// The names local sockets bind (W4.1), every session's.
    /// </summary>
    public SocketNames Sockets { get; } = new();

    /// <summary>
    /// Where the listening sockets' ends come from (W4.1).
    /// </summary>
    public IListenerEndSource ListenerEnds { get; }

    #endregion

    #region Nested Types

    /// <summary>
    /// A volume at its mount point.
    /// </summary>
    /// <param name="Point">The mount point.</param>
    /// <param name="Volume">The volume.</param>
    /// <param name="Index">Its index in the table, which a stat reports as its volume.</param>
    public sealed record Mount(string Point, IFileSystem Volume, uint Index);

    #endregion
}
