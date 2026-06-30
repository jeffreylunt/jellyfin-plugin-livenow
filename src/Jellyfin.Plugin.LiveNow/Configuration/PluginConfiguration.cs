using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LiveNow.Configuration;

/// <summary>
/// Plugin configuration. Sensible defaults so a fresh install works zero-config.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the in-process Live Now background loop runs
    /// (name decoration + favorite float). Default true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether warm channels are floated to the top of every
    /// user's native guide by setting IsFavorite. Default true. (Name decoration still runs
    /// independently if this is off.)
    /// </summary>
    public bool EnableFavoriteFloat { get; set; } = true;

    /// <summary>
    /// Gets or sets how often (seconds) the background loop polls active sessions. Default 45.
    /// </summary>
    public int PollSeconds { get; set; } = 45;

    /// <summary>
    /// Gets or sets how often (in cycles) the loop does a FULL favorite re-evaluation of ALL
    /// warm channels (vs the cheap delta) — heals new-user-mid-warm and transient-read misses.
    /// Default 10 (~7.5 min at a 45s poll).
    /// </summary>
    public int FullFavoritePassEvery { get; set; } = 10;

    /// <summary>
    /// Gets or sets how often (seconds) the embedded Live Now web page refreshes. Default 15.
    /// </summary>
    public int RefreshSeconds { get; set; } = 15;
}
