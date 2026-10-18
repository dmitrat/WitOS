using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WitOS.Apps;

namespace WitOS.Installer;

/// <summary>
/// What the installer shows and does: a row for each application the layer holds or the feed offers, the installation
/// of the feed's latest version of one, and the switch to an installed version.
/// </summary>
internal sealed class InstallerModel
{
    #region Fields

    private readonly InstallerOptions m_options;

    private byte[]? m_apphost;

    #endregion

    #region Constructors

    /// <summary>
    /// The model over the options' layer and feed; read with <see cref="Reload"/>.
    /// </summary>
    /// <param name="options">The layer, the feed and the apphost.</param>
    public InstallerModel(InstallerOptions options)
    {
        m_options = options;
        Layer = new AppsDirectory(options.Layer);
        Rows = [];
        Problems = [];
        Reload();
    }

    #endregion

    #region Functions

    /// <summary>
    /// Reads the layer and the feed again.
    /// </summary>
    public void Reload()
    {
        var offered = new Dictionary<string, FeedEntry>(StringComparer.Ordinal);
        var problems = new List<string>();
        if (m_options.Feed is { } feed)
        {
            try
            {
                var reading = AppFeed.Read(feed);
                problems.AddRange(reading.Problems);
                foreach (var entry in reading.Entries)
                    offered[entry.Manifest.Id] = entry; // ordered by version, so the latest stays
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                problems.Add($"The feed {feed} cannot be read: {error.Message}");
            }
        }
        Rows = [.. Layer.Applications().Union(offered.Keys).Order(StringComparer.Ordinal)
            .Select(id => new AppRow(id, Layer.Versions(id), Layer.Current(id), offered.GetValueOrDefault(id)))];
        Problems = problems;
    }

    /// <summary>
    /// Installs the feed's version of a row's application, beside the versions the layer has, and makes it current.
    /// </summary>
    /// <param name="row">The row.</param>
    /// <param name="report">Told each step.</param>
    /// <param name="cancellationToken">Cancels before the version is renamed into place.</param>
    /// <returns>What happened, for the status line.</returns>
    /// <exception cref="InvalidOperationException">The feed offers no version of the application.</exception>
    /// <exception cref="InvalidDataException">The package is not what its entry names.</exception>
    public async Task<string> InstallAsync(AppRow row, Action<string> report, CancellationToken cancellationToken = default)
    {
        var entry = row.Offered ?? throw new InvalidOperationException($"The feed offers no version of {row.Id}.");
        var written = await Layer.InstallAsync(entry, Apphost(), report, cancellationToken);
        return written
            ? $"Installed {row.Id} {entry.Manifest.Version}"
            : $"{row.Id} {entry.Manifest.Version} was installed; it is in use";
    }

    /// <summary>
    /// Makes an installed version of an application the one in use: its launchers and current.
    /// </summary>
    /// <param name="id">The application.</param>
    /// <param name="version">The installed version.</param>
    /// <returns>What happened, for the status line.</returns>
    /// <exception cref="InvalidOperationException">The layer has no such version.</exception>
    public string Use(string id, string version)
    {
        Layer.Use(id, version, Apphost());
        return $"{id} uses {version}";
    }

    private byte[] Apphost() => m_apphost ??= File.ReadAllBytes(m_options.Apphost);

    #endregion

    #region Properties

    /// <summary>
    /// The layer.
    /// </summary>
    public AppsDirectory Layer { get; }

    /// <summary>
    /// The feed's directory, or null.
    /// </summary>
    public string? Feed => m_options.Feed;

    /// <summary>
    /// The applications, by identity.
    /// </summary>
    public IReadOnlyList<AppRow> Rows { get; private set; }

    /// <summary>
    /// The feed's entries that failed their checks, and why.
    /// </summary>
    public IReadOnlyList<string> Problems { get; private set; }

    #endregion
}

/// <summary>
/// An application as the installer shows it.
/// </summary>
/// <param name="Id">Its identity.</param>
/// <param name="Installed">The versions the layer holds, oldest first.</param>
/// <param name="Current">The version in use, or null.</param>
/// <param name="Offered">The feed's latest version, or null.</param>
internal sealed record AppRow(string Id, IReadOnlyList<string> Installed, string? Current, FeedEntry? Offered)
{
    #region Properties

    /// <summary>
    /// The version the feed offers, or null.
    /// </summary>
    public string? Available => Offered?.Manifest.Version;

    /// <summary>
    /// What the feed means for the application: nothing, an installation, an update or nothing new.
    /// </summary>
    public string State => Available switch
    {
        null => "",
        var version when Installed.Contains(version) => "installed",
        _ when Installed.Count == 0 => "new",
        var version when AppVersion.ORDER.Compare(version, AppVersion.Latest(Installed)) > 0 => "update",
        _ => "older"
    };

    /// <summary>
    /// The row as a line of the list command.
    /// </summary>
    public string Line =>
        $"{Id}: installed {(Installed.Count == 0 ? "none" : string.Join(", ", Installed))}; in use {Current ?? "none"}; feed {Available ?? "none"}" +
        (State.Length > 0 ? $" ({State})" : "");

    #endregion
}
