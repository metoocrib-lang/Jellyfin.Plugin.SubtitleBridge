using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SubtitleBridge.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string BridgeUrl { get; set; } = "http://moonfin-subtitle-bridge:8787";
    public string BridgeToken { get; set; } = string.Empty;
}
