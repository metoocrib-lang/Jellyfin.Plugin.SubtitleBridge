using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SubtitleBridge.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string SonarrUrl { get; set; } = string.Empty;
    public string SonarrApiKey { get; set; } = string.Empty;
    public string RadarrUrl { get; set; } = string.Empty;
    public string RadarrApiKey { get; set; } = string.Empty;
    public string BazarrUrl { get; set; } = string.Empty;
    public string BazarrApiKey { get; set; } = string.Empty;

    // Optional, for the separate browser automation agent only.
    public string BridgeUrl { get; set; } = "http://moonfin-subtitle-bridge:8787";
    public string BridgeToken { get; set; } = string.Empty;

    public List<TrackedMedia> TrackedItems { get; set; } = [];
}

public sealed class TrackedMedia
{
    public string Service { get; set; } = "sonarr";
    public int Id { get; set; }
    public string Language { get; set; } = "es";
    public string Source { get; set; } = "bazarr";
    public bool Automatic { get; set; } = true;
}
