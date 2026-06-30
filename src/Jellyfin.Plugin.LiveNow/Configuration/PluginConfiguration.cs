using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LiveNow.Configuration;

/// <summary>
/// Plugin configuration. Kept intentionally small for v1.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets how often (seconds) the Live Now page polls the API.
    /// </summary>
    public int RefreshSeconds { get; set; } = 15;
}
