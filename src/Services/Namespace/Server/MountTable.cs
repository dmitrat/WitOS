using System;
using System.Collections.Generic;
using System.Linq;
using OutWit.OS.FileSystems;

namespace OutWit.OS.Namespace.Server;

/// <summary>
/// The volumes the namespace serves, each at a mount point: an absolute path of one component, such as "/data". A path
/// below a mount point resolves to its volume and the path within it.
/// </summary>
internal sealed class MountTable
{
    #region Fields

    private readonly List<Mount> m_mounts = [];

    #endregion

    #region Functions

    /// <summary>
    /// Mounts a volume.
    /// </summary>
    /// <param name="point">The mount point, "/" and one component.</param>
    /// <param name="volume">The volume.</param>
    /// <exception cref="ArgumentException">The point is no single component or is taken.</exception>
    public void Add(string point, IFileSystem volume)
    {
        if (point.Length < 2 || point[0] != '/' || point.IndexOf('/', 1) >= 0 || m_mounts.Any(mount => mount.Point == point))
            throw new ArgumentException($"'{point}' is no free mount point.", nameof(point));
        m_mounts.Add(new Mount(point, volume, (uint)m_mounts.Count));
    }

    /// <summary>
    /// The volume a path lies on and the path within it.
    /// </summary>
    /// <param name="path">An absolute path of the namespace.</param>
    /// <returns>The mount and the path in its volume, "/" for the mount point itself; null for a path on no volume.</returns>
    public (Mount Mount, string Path)? Resolve(string path)
    {
        foreach (var mount in m_mounts)
        {
            if (path == mount.Point)
                return (mount, "/");
            if (path.Length > mount.Point.Length && path.StartsWith(mount.Point, StringComparison.Ordinal) && path[mount.Point.Length] == '/')
                return (mount, path[mount.Point.Length..]);
        }
        return null;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The mounts, in the order they were added.
    /// </summary>
    public IReadOnlyList<Mount> Mounts => m_mounts;

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
