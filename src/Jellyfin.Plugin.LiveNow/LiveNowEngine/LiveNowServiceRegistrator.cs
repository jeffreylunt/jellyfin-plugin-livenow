using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.LiveNow.LiveNowEngine;

/// <summary>
/// Registers the Live Now in-process background loop with Jellyfin's DI container so it starts
/// with the server. This is what makes the plugin fully self-contained — no external daemon.
/// </summary>
public sealed class LiveNowServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc/>
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHostedService<LiveNowHostedService>();
    }
}
