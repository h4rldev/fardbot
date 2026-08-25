using Jellyfin.Plugin.H4ip.Data;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.H4ip;

/// <summary>
/// Service registrator for the plugin.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHostedService<EventMonitorEntryPoint>();
        serviceCollection.AddHttpClient();
        serviceCollection.AddSingleton(sp => new H4ipRepository(
            sp.GetRequiredService<IApplicationPaths>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<H4ipRepository>()));
    }
}
