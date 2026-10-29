using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OutWit.OS.FileSystems;
using OutWit.OS.Namespace.Server;
using OutWit.OS.Namespaces;

namespace OutWit.OS.Namespace.Volumes;

/// <summary>
/// The catalog of the volumes the namespace mounts (plan step E1, ADR 0032 decisions 1–3): each with its identity, its
/// alias and its origin, mounted at its point. The system's volumes take their own aliases and points (Boot, Data, Fat,
/// Tmp; /media itself); every other one takes an alias from its label, unique among the attached volumes without regard
/// to case (PowerShell's drive names ignore it), and mounts at /media/&lt;alias&gt;. A volume that leaves and comes back in
/// the same boot takes its alias again if it is free.
/// </summary>
internal sealed class VolumeCatalog
{
    #region Constants

    /// <summary>The alias of a volume without a label.</summary>
    public const string UNLABELLED = "Volume";

    private const int ALIAS_CHARACTERS = 31;

    // The system's aliases, which no other volume takes, and media's own name.
    private static readonly string[] SYSTEM_ALIASES = ["Boot", "Data", "Fat", "Tmp", "Media"];

    #endregion

    #region Fields

    private readonly MountTable m_mounts;
    private readonly object m_gate = new();
    private readonly List<CatalogVolume> m_volumes = [];
    private readonly Dictionary<string, string> m_aliasesSeen = new(StringComparer.Ordinal);

    #endregion

    #region Constructors

    /// <summary>
    /// The catalog over the namespace's mounts.
    /// </summary>
    /// <param name="mounts">The mounts the catalog's volumes take.</param>
    public VolumeCatalog(MountTable mounts)
    {
        m_mounts = mounts;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Attaches a volume: its alias, its point, its mount.
    /// </summary>
    /// <param name="volume">The volume.</param>
    /// <param name="origin">NamespaceMessages.ORIGIN_*.</param>
    /// <param name="systemAlias">The system's alias for one of its own volumes (Boot, Data, Fat, Tmp), which mounts at its
    /// name in lower case; null for any other volume, which mounts below /media.</param>
    /// <param name="identity">The volume's identity: the format's, or the partition's where the format keeps none or a weak
    /// one; null for none.</param>
    /// <param name="weakIdentity">Whether the identity is a FAT serial number, which copies share.</param>
    /// <returns>The attached volume.</returns>
    /// <exception cref="ArgumentException">The system's alias is taken.</exception>
    public CatalogVolume Attach(IFileSystem volume, uint origin, string? systemAlias, string? identity, bool weakIdentity)
    {
        lock (m_gate)
        {
            var alias = systemAlias ?? Alias(volume.Volume.Label, identity);
            var point = systemAlias is null ? $"{MountTable.MEDIA}/{alias}" : "/" + systemAlias.ToLowerInvariant();
            var collision = identity is not null && m_volumes.Any(other => other.Identity == identity);
            var mount = m_mounts.Add(point, volume);
            var attached = new CatalogVolume(alias, point, origin, identity, weakIdentity, collision, mount);
            m_volumes.Add(attached);
            if (identity is not null)
                m_aliasesSeen[identity] = alias;
            return attached;
        }
    }

    /// <summary>
    /// Detaches a volume (a drive pulled, a share unmounted): its mount goes; the provider stays its holder's to close.
    /// </summary>
    /// <param name="alias">The volume's alias.</param>
    /// <returns>The volume, or null for no such alias.</returns>
    public CatalogVolume? Detach(string alias)
    {
        lock (m_gate)
        {
            var volume = m_volumes.FirstOrDefault(item => item.Alias == alias);
            if (volume is null)
                return null;
            m_volumes.Remove(volume);
            m_mounts.Remove(volume.Point);
            return volume;
        }
    }

    /// <summary>
    /// The attached volume of a mount, or null for a mount the catalog does not hold.
    /// </summary>
    /// <param name="mount">The mount.</param>
    /// <returns>The volume.</returns>
    public CatalogVolume? Of(MountTable.Mount mount)
    {
        lock (m_gate)
            return m_volumes.FirstOrDefault(volume => volume.Mount == mount);
    }

    /// <summary>
    /// A label made an alias (ADR 0032 decision 1): every character outside letters, digits, '-' and '_' a '-', at most
    /// 31 characters, the dashes at its ends dropped; "Volume" when nothing is left.
    /// </summary>
    /// <param name="label">The label, or null.</param>
    /// <returns>The alias's stem.</returns>
    public static string Stem(string? label)
    {
        var text = new StringBuilder();
        foreach (var character in (label ?? "").Trim())
            text.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-');
        var stem = text.ToString().Trim('-');
        if (stem.Length > ALIAS_CHARACTERS)
            stem = stem[..ALIAS_CHARACTERS].TrimEnd('-');
        return stem.Length == 0 ? UNLABELLED : stem;
    }

    // A unique alias: the one this identity had in this boot if it is free, else the label's stem, then -2, -3…
    private string Alias(string? label, string? identity)
    {
        if (identity is not null && m_aliasesSeen.TryGetValue(identity, out var seen) && Free(seen))
            return seen;
        var stem = Stem(label);
        if (Free(stem))
            return stem;
        for (var n = 2; ; n++)
        {
            var suffix = $"-{n}";
            var alias = (stem.Length + suffix.Length > ALIAS_CHARACTERS ? stem[..(ALIAS_CHARACTERS - suffix.Length)] : stem) + suffix;
            if (Free(alias))
                return alias;
        }
    }

    // Free among the attached volumes and the system's own aliases, whatever the case.
    private bool Free(string alias) =>
        !m_volumes.Any(volume => string.Equals(volume.Alias, alias, StringComparison.OrdinalIgnoreCase)) &&
        !SYSTEM_ALIASES.Contains(alias, StringComparer.OrdinalIgnoreCase);

    #endregion

    #region Properties

    /// <summary>
    /// The attached volumes now, in the order they came.
    /// </summary>
    public IReadOnlyList<CatalogVolume> Volumes
    {
        get
        {
            lock (m_gate)
                return [.. m_volumes];
        }
    }

    #endregion
}

/// <summary>
/// A volume of the catalog.
/// </summary>
/// <param name="Alias">Its unique name.</param>
/// <param name="Point">Its mount point.</param>
/// <param name="Origin">NamespaceMessages.ORIGIN_*.</param>
/// <param name="Identity">Its identity, or null.</param>
/// <param name="WeakIdentity">Whether the identity is one that copies share.</param>
/// <param name="Collision">Whether another volume had its identity when it came.</param>
/// <param name="Mount">Its mount.</param>
internal sealed record CatalogVolume(string Alias, string Point, uint Origin, string? Identity, bool WeakIdentity, bool Collision,
    MountTable.Mount Mount)
{
    #region Functions

    /// <summary>
    /// The volume's record for VOLUMES and STATFS (E1), with its space when given.
    /// </summary>
    /// <param name="space">Its space, or null when not asked.</param>
    /// <returns>The record.</returns>
    public NamespaceVolume Record(FileSystemSpace? space)
    {
        var volume = Mount.Volume.Volume;
        var flags = (volume.IsReadOnly ? NamespaceMessages.VOLUME_READ_ONLY : 0) | (WeakIdentity ? NamespaceMessages.VOLUME_WEAK_IDENTITY : 0) |
            (Collision ? NamespaceMessages.VOLUME_COLLISION : 0) | (space is null ? 0 : NamespaceMessages.VOLUME_SPACE);
        return new NamespaceVolume(flags, Origin, space?.TotalBytes ?? 0, space?.FreeBytes ?? 0, volume.AllocationUnit, Mount.Index, Alias, Point,
            volume.Format, Identity ?? "", volume.Label ?? "");
    }

    #endregion
}
