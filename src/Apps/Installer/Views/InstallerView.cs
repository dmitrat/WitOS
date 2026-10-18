using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Arlecchino;
using Arlecchino.Commands;
using Arlecchino.Diagnostics;
using Arlecchino.Hosting;
using Arlecchino.Input;
using Arlecchino.Navigation;
using Arlecchino.Rendering;
using Arlecchino.Rendering.Colors;
using Arlecchino.State;
using Arlecchino.Widgets.Lists;
using Arlecchino.Widgets.Readouts;
using Microsoft.Extensions.Hosting;

namespace WitOS.Installer.Views;

/// <summary>
/// The installer's screen (plan step C4.3): the applications of the layer and the feed in a table — the versions
/// installed, the one in use, the feed's — with I to install or update the selected one from the feed, U to use another
/// installed version (the rollback), R to read the layer and the feed again and F10 to quit. An installation runs off the
/// frame thread under a notification that shows its steps, and settles into what happened.
/// </summary>
internal sealed class InstallerView : IArlecchinoView
{
    #region Constants

    private const int OUTPUT_ROWS = 2;

    #endregion

    #region Fields

    private readonly Surface m_surface;
    private readonly ArlecchinoState m_state;
    private readonly IHostApplicationLifetime m_lifetime;
    private readonly ViewLifetime m_view;
    private readonly TimeProvider m_time;
    private readonly InstallerModel m_model;
    private readonly Table<AppRow> m_table;
    private readonly StatusBar m_status;

    private volatile string m_step = "";
    private bool m_busy;

    #endregion

    #region Constructors

    /// <summary>
    /// The screen over the installer's model.
    /// </summary>
    public InstallerView(Surface surface, ArlecchinoState state, ArlecchinoOptions options, IHostApplicationLifetime lifetime, ViewLifetime view,
        TimeProvider time, InstallerModel model)
    {
        m_surface = surface;
        m_state = state;
        m_lifetime = lifetime;
        m_view = view;
        m_time = time;
        m_model = model;
        m_table = new Table<AppRow>(options.Keymap)
        {
            Columns =
            [
                new TableColumn<AppRow> { Header = static () => "Application", Cell = static row => row.Id },
                new TableColumn<AppRow> { Header = static () => "Installed", Cell = static row => string.Join(", ", row.Installed), Width = 24 },
                new TableColumn<AppRow> { Header = static () => "In use", Cell = static row => row.Current ?? "", Width = 12 },
                new TableColumn<AppRow> { Header = static () => "Feed", Cell = static row => row.Available is { } version ? $"{version} {row.State}" : "", Width = 20 }
            ],
            ItemStyle = static row => row.State is "new" or "update" ? Theme.Info : Theme.Default,
            IsFocused = true
        };
        m_status = new StatusBar { Left = [Selected], Right = [() => m_busy ? "working" : ""] };
    }

    #endregion

    #region IArlecchinoView

    /// <inheritdoc />
    public void Draw()
    {
        var content = m_surface.Content.Inset(new Margin(0, 0, 0, OUTPUT_ROWS));
        var (header, rest) = content.SplitTop(3);
        var (rows, status) = rest.SplitTop(rest.Height - 1);
        header.WriteLine(0, "WitOS base applications", Theme.Header);
        header.WriteLine(1, $"layer {m_model.Layer.Root} · feed {m_model.Feed ?? "none (--feed <directory>)"}", Theme.Secondary);
        if (m_model.Problems.Count > 0)
            header.WriteLine(2, $"{m_model.Problems.Count} feed entries left out: {m_model.Problems[0]}", Theme.Warning);
        m_table.Rows = m_model.Rows;
        m_table.Draw(rows);
        m_status.Draw(status);
    }

    /// <inheritdoc />
    public ViewRoute Handle(KeyPress key) => m_table.Handle(key).Route;

    /// <inheritdoc />
    public IReadOnlyList<ViewCommand> Commands() =>
    [
        ViewCommand.For(ConsoleKey.I, static () => "install or update", Install),
        ViewCommand.For(ConsoleKey.U, static () => "use a version", PickVersion),
        ViewCommand.For(ConsoleKey.R, static () => "read again", Reload),
        ViewCommand.For(ConsoleKey.F10, static () => "quit", m_lifetime.StopApplication)
    ];

    #endregion

    #region Functions

    private void Install()
    {
        if (m_busy || m_table.SelectedRow is not { } row)
            return;
        if (row.Offered is null)
        {
            m_state.Output = $"The feed offers no version of {row.Id}.";
            return;
        }
        m_busy = true;
        m_step = $"Installing {row.Id} {row.Available}";
        var entry = m_state.Notifications.Raise(new Notification(m_time.GetUtcNow(), NotificationLevel.Information, m_step) { ProgressText = () => m_step });
        var closing = m_view.Closing;
        _ = Task.Run(async () =>
        {
            string text;
            var level = NotificationLevel.Information;
            try
            {
                text = await m_model.InstallAsync(row, step =>
                {
                    m_step = step;
                    m_state.Invalidate();
                }, closing);
            }
            catch (Exception error)
            {
                text = $"{row.Id} {row.Available} was not installed: {error.Message}";
                level = NotificationLevel.Failure;
            }
            FrameThread.Post(() =>
            {
                m_busy = false;
                m_model.Reload();
                m_state.Notifications.Settle(entry, text, level);
                m_state.Output = text;
            });
        }, closing);
    }

    private void PickVersion()
    {
        if (m_busy || m_table.SelectedRow is not { } row)
            return;
        if (row.Installed.Count == 0)
        {
            m_state.Output = $"No version of {row.Id} is installed.";
            return;
        }
        m_state.RequestChoice($"Use which version of {row.Id}?", row.Installed, version =>
        {
            try
            {
                m_state.Output = m_model.Use(row.Id, version);
            }
            catch (Exception error)
            {
                m_state.Output = $"{row.Id} {version} was not put in use: {error.Message}";
            }
            m_model.Reload();
        }, row.Current ?? "");
    }

    private void Reload()
    {
        if (m_busy)
            return;
        m_model.Reload();
        m_state.Output = $"Read {m_model.Rows.Count} applications";
    }

    private string Selected() => m_table.SelectedRow is { } row ? row.Line : "no application";

    #endregion
}
