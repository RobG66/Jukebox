using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Jukebox.Plugin.Abstractions;

/// <summary>
/// A media browser plugin. Each implementation is discovered at startup
/// by <c>PluginLoader</c> and rendered in the host application's media
/// browser surface. Plugins own their Models, Services, ViewModel, View, and
/// Styles — the host app never needs to know about a specific plugin.
///
/// <para><b>Lifecycle:</b></para>
/// <list type="number">
///   <item>Host calls the parameterless constructor.</item>
///   <item>Host calls <see cref="InitializeAsync"/> with a context that
///         gives the plugin access to host services (playback, paths,
///         logging).</item>
///   <item>Host calls <see cref="CreateView"/> to get the browser content
///         UserControl. The returned control is parented to the main app's
///         browser surface.</item>
///   <item>Host calls <see cref="Dispose"/> exactly once during shutdown.</item>
/// </list>
///
/// <para><b>Isolation:</b> if the constructor or <see cref="InitializeAsync"/>
/// throws, the plugin is silently skipped with a log message. A broken
/// plugin never takes down the app.</para>
/// </summary>
public interface IJukeboxMediaBrowser
{
    /// <summary>
    /// Stable unique identifier ("khinsider", "radiobrowser"). Used as
    /// the directory name under <c>Cache/Plugins/&lt;id&gt;/</c> for the
    /// plugin's private data. Must be lowercase, no spaces.
    /// </summary>
    string Id { get; }

    /// <summary>Display name shown in the host navigation UI.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Optional Avalonia asset URI for the host navigation icon
    /// (e.g. <c>avares://My.Plugin/Assets/icon.png</c>).
    /// Null = no icon.
    /// </summary>
    string? IconUri { get; }

    /// <summary>
    /// Optional vector path data for the host navigation icon. This is useful
    /// for plugins that prefer a themeable monochrome symbol over a bitmap.
    /// <see cref="IconUri"/> takes precedence when both are supplied.
    /// </summary>
    string? IconPathData => null;

    /// <summary>
    /// Sort order — lower numbers appear first after the host destinations.
    /// Use 100+ for third-party plugins to leave room for built-in ones.
    /// </summary>
    int SortOrder { get; }

    /// <summary>
    /// Called once at startup after construction. The plugin should load
    /// any persisted state (favorites, cache) here. Must not throw —
    /// catch and log internally, or fail gracefully.
    /// </summary>
    Task InitializeAsync(IJukeboxMediaBrowserContext context);

    /// <summary>
    /// Returns the UserControl that will be hosted in the browser surface. Called
    /// once after <see cref="InitializeAsync"/>. The plugin owns the
    /// control and its ViewModel — the host only parents it to the active
    /// browser content presenter.
    /// </summary>
    UserControl CreateView();

    /// <summary>
    /// Releases the browser view-model, event subscriptions, and active work.
    /// The default keeps older third-party plugins source-compatible.
    /// </summary>
    void Dispose() { }

    /// <summary>
    /// Returns <see langword="true"/> when this plugin can turn the stable
    /// source URL or identifier into a currently playable URL.
    /// </summary>
    /// <remarks>
    /// The default implementation preserves source compatibility for plugins
    /// that do not need URL resolution, such as Radio Browser and Internet
    /// Archive.
    /// </remarks>
    bool CanResolve(string sourceUrl) => false;

    /// <summary>
    /// Resolves a stable source URL or identifier into the URL that should be
    /// handed to the playback engine for this attempt.
    /// </summary>
    /// <remarks>
    /// Plugins that return temporary or signed media URLs should override this
    /// method and retain any useful cache internally. The default implementation
    /// returns <paramref name="sourceUrl"/> unchanged.
    /// </remarks>
    Task<string> ResolveUrlAsync(
        string sourceUrl,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(sourceUrl);
    }
}
