using System.Globalization;
using Jellyfin.Plugin.LiveNow.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.LiveNow;

/// <summary>
/// "Live Now" plugin: surfaces which Live TV channels are currently warm
/// (being watched by someone on the server) so others can tune into an
/// already-open upstream stream instead of consuming a new scarce slot.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Live Now";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("b919b337-587b-4533-922e-c8f8c5c8c9b0");

    /// <inheritdoc />
    public override string Description =>
        "Shows which Live TV channels are currently being watched on this server " +
        "so you can tune into a warm channel instead of opening a new upstream slot.";

    /// <summary>
    /// Returns the embedded web pages this plugin exposes (the Live Now page,
    /// reachable from the dashboard plugin list and as a Custom Tab URL).
    /// </summary>
    /// <returns>Web page info.</returns>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "livenow",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Web.livenow.html",
                    GetType().Namespace),
            },
        };
    }
}
