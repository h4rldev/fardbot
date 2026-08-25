using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.H4ip.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        BotUrl = "localhost:8080";
        SharedSecret = string.Empty;
    }

    /// <summary>
    /// Gets or sets the base URL of the h4bot Discord bot's HTTP endpoint.
    /// </summary>
    public string BotUrl { get; set; }

    /// <summary>
    /// Gets or sets the shared secret the bot expects in the X-H4ip-Secret header.
    /// </summary>
    public string SharedSecret { get; set; }
}
