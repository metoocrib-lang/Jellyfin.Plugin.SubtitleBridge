using System.Globalization;
using Jellyfin.Plugin.SubtitleBridge.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SubtitleBridge;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Moonfin Subtitle Bridge";

    public override Guid Id => Guid.Parse("b8e43169-c5f4-41d3-a9a0-8f4b9f85d792");

    public override string Description => "Administración de subtítulos por serie en Jellyfin y Bazarr.";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html", GetType().Namespace)
        }
    ];
}
