using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Channels;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleBridge;

/// <summary>
/// Only reacts to newly added physical Jellyfin episodes. ItemUpdated is used
/// solely to complete metadata for episodes previously seen in ItemAdded.
/// </summary>
public sealed class NewEpisodeListener : BackgroundService
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };
    private readonly ILibraryManager _library;
    private readonly ILogger<NewEpisodeListener> _log;
    private readonly Channel<Guid> _episodeIds = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(256) {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest
        });
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _newEpisodes = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _processed = new();

    public NewEpisodeListener(ILibraryManager library, ILogger<NewEpisodeListener> log)
    {
        _library = library;
        _log = log;
    }

    public override Task StartAsync(CancellationToken token)
    {
        _library.ItemAdded += OnItemAdded;
        _library.ItemUpdated += OnItemUpdated;
        _log.LogInformation("Moonfin Subtitle Bridge: escuchando nuevos episodios de Jellyfin.");
        return base.StartAsync(token);
    }

    public override async Task StopAsync(CancellationToken token)
    {
        _library.ItemAdded -= OnItemAdded;
        _library.ItemUpdated -= OnItemUpdated;
        _episodeIds.Writer.TryComplete();
        await base.StopAsync(token).ConfigureAwait(false);
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not Episode episode || episode.IsMissingEpisode)
            return;
        _newEpisodes[episode.Id] = DateTimeOffset.UtcNow;
        _episodeIds.Writer.TryWrite(episode.Id);
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not Episode episode || episode.IsMissingEpisode)
            return;
        // Metadata-only updates to existing shows must never trigger a search.
        if (_newEpisodes.TryGetValue(episode.Id, out var firstSeen)
            && DateTimeOffset.UtcNow - firstSeen < TimeSpan.FromHours(1))
            _episodeIds.Writer.TryWrite(episode.Id);
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        try
        {
            await foreach (var episodeId in _episodeIds.Reader.ReadAllAsync(token))
            {
                try
                {
                    // Let Jellyfin finish ingesting the media file and its metadata.
                    await Task.Delay(TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
                    await ProcessEpisodeAsync(episodeId, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    // The message is intentionally generic: exceptions may contain URLs.
                    _log.LogWarning("Moonfin: no pudo procesar un episodio nuevo ({ErrorType}).",
                        ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task ProcessEpisodeAsync(Guid id, CancellationToken ct)
    {
        if (!_newEpisodes.TryGetValue(id, out var added)
            || DateTimeOffset.UtcNow - added > TimeSpan.FromHours(1))
            return;
        var episode = _library.GetItemById<Episode>(id);
        if (episode is null || episode.IsMissingEpisode
            || string.IsNullOrWhiteSpace(episode.Path)
            || !episode.ParentIndexNumber.HasValue || !episode.IndexNumber.HasValue)
            return;
        var series = episode.Series;
        if (series is null
            || !series.TryGetProviderId(MetadataProvider.Tvdb, out var tvdbRaw)
            || !int.TryParse(tvdbRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var tvdb)
            || tvdb <= 0)
        {
            _log.LogInformation("Moonfin: episodio nuevo sin TVDB de serie; no se asignó automáticamente.");
            return;
        }

        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.SonarrApiKey)
            || !StreamingScanDispatcher.SafeHttpUri(cfg.SonarrUrl, out var sonarr))
            return;
        foreach (var tracked in (cfg.TrackedItems ?? []))
        {
            if (!tracked.Automatic
                || !string.Equals(tracked.Service, "sonarr", StringComparison.OrdinalIgnoreCase)
                || tracked.Source is not ("disney" or "netflix" or "prime" or "max" or "apple")
                || tracked.Id < 1 || string.IsNullOrWhiteSpace(tracked.StreamingUrl))
                continue;
            if (!await MatchesSonarrTvdbAsync(sonarr!, cfg.SonarrApiKey, tracked.Id, tvdb, ct)
                    .ConfigureAwait(false))
                continue;

            // Avoid repeated ItemUpdated events launching the same job. The
            // agent additionally imposes a per-episode retry cooldown.
            if (_processed.TryGetValue(id, out var last)
                && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(24))
                return;

            var result = await StreamingScanDispatcher.ScanAsync(
                ct, tracked.Id, episode.ParentIndexNumber.Value, episode.IndexNumber.Value)
                .ConfigureAwait(false);
            if (result.Submitted > 0)
            {
                _processed[id] = DateTimeOffset.UtcNow;
                _log.LogInformation("Moonfin: episodio nuevo reconocido y trabajo enviado al agente.");
            }
            else
            {
                _log.LogInformation("Moonfin: episodio nuevo sin subtítulo pendiente o agente sin trabajo.");
            }
            return;
        }
        _log.LogDebug("Moonfin: episodio nuevo fuera de las series monitoreadas.");
    }

    private static async Task<bool> MatchesSonarrTvdbAsync(
        Uri sonarr, string apiKey, int sonarrId, int jellyfinTvdb, CancellationToken ct)
    {
        foreach (var prefix in new[] { "/api/v3/series/", "/api/v5/series/" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(sonarr.AbsoluteUri.TrimEnd('/') + prefix
                    + sonarrId.ToString(CultureInfo.InvariantCulture)));
            request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                continue;
            if (!response.IsSuccessStatusCode) return false;
            if (response.Content.Headers.ContentLength > 256_000) return false;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length > 256_000) return false;
            using var json = JsonDocument.Parse(bytes);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("tvdbId", out var node)
                && node.ValueKind == JsonValueKind.Number
                && node.TryGetInt32(out var sonarrTvdb)
                && jellyfinTvdb == sonarrTvdb;
        }
        return false;
    }
}
