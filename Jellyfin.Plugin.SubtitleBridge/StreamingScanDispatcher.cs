using System.Net.Http;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.SubtitleBridge.Configuration;

namespace Jellyfin.Plugin.SubtitleBridge;

/// <summary>
/// Reusable scan invoked by the library event and the manual Jellyfin button.
/// No daily schedule is registered; episode events drive automatic attempts.
/// </summary>
public static class StreamingScanDispatcher
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    public static async Task<ScanSummary> ScanAsync(
        CancellationToken ct, int? onlySeriesId = null, int? onlySeason = null, int? onlyEpisode = null)
    {
        var cfg = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Plugin no inicializado");
        if (string.IsNullOrWhiteSpace(cfg.BazarrApiKey) ||
            !SafeHttpUri(cfg.BazarrUrl, out var bazarr) ||
            string.IsNullOrWhiteSpace(cfg.BridgeToken) ||
            !SafeHttpUri(cfg.BridgeUrl, out var agent))
            throw new InvalidOperationException("Configura Bazarr y el agente local antes de la revisión.");

        var tracked = (cfg.TrackedItems ?? []).Where(x =>
            string.Equals(x.Service, "sonarr", StringComparison.OrdinalIgnoreCase)
            && x.Automatic && (x.Source is "disney" or "netflix" or "prime" or "max" or "apple")
            && x.Id > 0 && !string.IsNullOrWhiteSpace(x.StreamingUrl))
            .ToDictionary(x => x.Id);
        if (tracked.Count == 0)
            return new ScanSummary(0, 0, "No hay series de streaming habilitadas con URL.");

        var wanted = new List<Dictionary<string, object>>();
        var count = 0;
        // Bazarr wanted list is paginated, limit work per run to prevent overload.
        for (var start = 0; start < 500; start += 100)
        {
            using var get = new HttpRequestMessage(HttpMethod.Get,
                new Uri(bazarr!.AbsoluteUri.TrimEnd('/') + "/api/episodes/wanted?start=" + start + "&length=100"));
            get.Headers.TryAddWithoutValidation("X-API-KEY", cfg.BazarrApiKey);
            using var response = await Http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Bazarr devolvió HTTP {(int)response.StatusCode}");
            if (response.Content.Headers.ContentLength > 2_000_000)
                throw new InvalidOperationException("Respuesta de Bazarr demasiado grande");
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length > 2_000_000) throw new InvalidOperationException("Respuesta de Bazarr demasiado grande");
            using var json = JsonDocument.Parse(bytes);
            if (!json.RootElement.TryGetProperty("data", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("La lista de Bazarr tiene un formato inesperado");
            var length = 0;
            foreach (var ep in items.EnumerateArray())
            {
                length++;
                if (!ReadInt(ep, "sonarrSeriesId", out var seriesId) ||
                    !ReadInt(ep, "sonarrEpisodeId", out var episodeId) ||
                    !tracked.TryGetValue(seriesId, out var follow) ||
                    !ep.TryGetProperty("episode_number", out var episodeTag) ||
                    episodeTag.ValueKind != JsonValueKind.String)
                    continue;
                var parts = (episodeTag.GetString() ?? "").Split('x', 2);
                if (parts.Length != 2 || !int.TryParse(parts[0], out var season) ||
                    !int.TryParse(parts[1], out var episode) || season < 0 || episode < 1)
                    continue;
                if ((onlySeriesId.HasValue && seriesId != onlySeriesId.Value)
                    || (onlySeason.HasValue && season != onlySeason.Value)
                    || (onlyEpisode.HasValue && episode != onlyEpisode.Value))
                    continue;
                if (!ep.TryGetProperty("missing_subtitles", out var languages) ||
                    languages.ValueKind != JsonValueKind.Array ||
                    !languages.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.Object
                        && x.TryGetProperty("code2", out var code)
                        && code.ValueKind == JsonValueKind.String
                        && string.Equals(code.GetString(), follow.Language, StringComparison.OrdinalIgnoreCase)
                        && (!x.TryGetProperty("forced", out var forced) || forced.ValueKind != JsonValueKind.True)
                        && (!x.TryGetProperty("hi", out var hi) || hi.ValueKind != JsonValueKind.True)))
                    continue;

                // Only send known IDs, season/episode, source and URL to the agent.
                wanted.Add(new Dictionary<string, object> {
                    ["provider"] = follow.Source,
                    ["series_url"] = follow.StreamingUrl,
                    ["series_id"] = seriesId,
                    ["episode_id"] = episodeId,
                    ["season"] = season,
                    ["episode"] = episode,
                    ["language"] = follow.Language
                });
            }
            count += length;
            if (length < 100) break;
        }
        if (wanted.Count == 0)
            return new ScanSummary(count, 0, "No hay episodios faltantes para las series monitoreadas.");
        // A maximum of 30 requests is queued per scan. The agent itself limits
        // work concurrency to one and prevents frequent reattempts per episode.
        var payload = JsonSerializer.Serialize(new {
            jobs = wanted.Take(30).ToArray(),
            bazarr_url = cfg.BazarrUrl,
            bazarr_api_key = cfg.BazarrApiKey
        });
        using var post = new HttpRequestMessage(HttpMethod.Post,
            new Uri(agent!.AbsoluteUri.TrimEnd('/') + "/v1/scan"));
        post.Headers.TryAddWithoutValidation("X-Bridge-Key", cfg.BridgeToken);
        post.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var result = await Http.SendAsync(post, ct).ConfigureAwait(false);
        if (!result.IsSuccessStatusCode)
            throw new InvalidOperationException($"Agente rechazó la revisión (HTTP {(int)result.StatusCode})");
        return new ScanSummary(count, Math.Min(wanted.Count, 30),
            "Solicitudes enviadas al agente. Consulta su estado para confirmar la extracción.");
    }

    public static bool SafeHttpUri(string? value, out Uri? uri)
    {
        uri = null;
        return !string.IsNullOrWhiteSpace(value)
            && Uri.TryCreate(value.TrimEnd('/'), UriKind.Absolute, out uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
    }

    private static bool ReadInt(JsonElement ep, string name, out int value)
    {
        value = 0;
        return ep.ValueKind == JsonValueKind.Object && ep.TryGetProperty(name, out var node)
            && node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out value);
    }
}
public sealed record ScanSummary(int CheckedEpisodes, int Submitted, string Message);
