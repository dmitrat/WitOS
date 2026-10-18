using Arlecchino.Hosting;
using Microsoft.Extensions.DependencyInjection;
using WitOS.Installer.Views;

namespace WitOS.Installer;

/// <summary>
/// The installer's screen as a host registers it, the same for the program and its tests.
/// </summary>
internal static class InstallerScreen
{
    #region Functions

    /// <summary>
    /// The framework's options: the smallest screen the table fits, smaller than the display's 100 by 37 and the serial
    /// terminal's 80 by 24.
    /// </summary>
    /// <param name="options">The options.</param>
    public static void Configure(ArlecchinoOptions options)
    {
        options.MinimumWidth = 80;
        options.MinimumHeight = 20;
    }

    /// <summary>
    /// Registers the model and the screen, which the host starts at.
    /// </summary>
    /// <param name="builder">The framework's builder.</param>
    /// <param name="options">The layer, the feed and the apphost.</param>
    /// <returns>The builder.</returns>
    public static ArlecchinoBuilder Add(ArlecchinoBuilder builder, InstallerOptions options)
    {
        builder.Services.AddSingleton(options).AddSingleton<InstallerModel>();
        return builder.AddGeneratedViews().StartAt(ViewKind.Installer);
    }

    #endregion
}
