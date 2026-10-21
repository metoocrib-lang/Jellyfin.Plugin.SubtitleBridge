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
        // Automatic ItemAdded calls pass all three coordinates. Resolve the
        // exact Sonarr episode through the same Bazarr /api/episodes endpoint
        // proven by "Procesar un episodio". The global wanted list can omit
        // valid missing-subtitle episodes or truncate them after 500 entries.
        // Never let a partial selector fall through to a bulk scan.
        if (onlySeriesId.HasValue || onlySeason.HasValue || onlyEpisode.HasValue)
        {
            if (!onlySeriesId.HasValue || !onlySeason.HasValue || !onlyEpisode.HasValue
                || onlySeriesId.Value < 1 || onlySeason.Value < 0 || onlyEpisode.Value < 1)
                throw new ArgumentException("La detección automática requiere serie, temporada y episodio.");

            var config = Plugin.Instance?.Configuration
                ?? throw new InvalidOperationException("Plugin no inicializado");
            var matches = (config.TrackedItems ?? []).Where(item =>
                item.Id == onlySeriesId.Value
                && string.Equals(item.Service, "sonarr", StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            if (matches.Length != 1 || !matches[0].Automatic
                || !string.Equals(matches[0].Source, "disney", StringComparison.OrdinalIgnoreCase))
                return new ScanSummary(0, 0, "La serie no tiene Disney+ automático habilitado.");

            var single = await ScanSingleAsync(onlySeriesId.Value,
                onlySeason.Value, onlyEpisode.Value, ct).ConfigureAwait(false);
            return new ScanSummary(1, single.Queued, single.Message);
        }

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

        // Parse once per followed series. Invalid or ambiguous rules must abort
        // rather than silently attach subtitles to a different episode.
        var mappings = new Dictionary<int, IReadOnlyList<EpisodeMapper.Rule>>();
        foreach (var item in tracked)
        {
            try
            {
                mappings.Add(item.Key, EpisodeMapper.Parse(item.Value.EpisodeMapping));
            }
            catch (FormatException)
            {
                throw new InvalidOperationException(
                    "Mapeo de episodios inválido. Revisa las equivalencias de la serie de Sonarr.");
            }
        }

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

                // Sonarr/Bazarr identifiers remain unchanged. Only these two
                // numbers are translated for episode discovery at the provider.
                if (!EpisodeMapper.TryMap(mappings[seriesId], season, episode,
                    out var providerSeason, out var providerEpisode))
                    continue;

                wanted.Add(new Dictionary<string, object> {
                    ["provider"] = follow.Source,
                    ["series_url"] = follow.StreamingUrl,
                    ["series_id"] = seriesId,
                    ["episode_id"] = episodeId,
                    ["season"] = providerSeason,
                    ["episode"] = providerEpisode,
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

    /// <summary>
    /// Admin-triggered single-episode request. Does not depend on Automatic and
    /// does not use the bulk wanted list; Bazarr is queried for only this series.
    /// </summary>
    public static async Task<SingleEpisodeSummary> ScanSingleAsync(
        int seriesId, int season, int episode, CancellationToken ct)
    {
        if (seriesId < 1 || season < 0 || season > 100 || episode < 1 || episode > 999)
            throw new ArgumentOutOfRangeException(nameof(episode));

        var cfg = Plugin.Instance?.Configuration
            ?? throw new SingleEpisodeError("plugin_uninitialized", "Moonfin aún no está inicializado.");
        var candidates = (cfg.TrackedItems ?? [])
            .Where(x => x.Id == seriesId &&
                string.Equals(x.Service, "sonarr", StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        if (candidates.Length > 1)
            throw new SingleEpisodeError("duplicate_tracked_series",
                "La serie está guardada más de una vez en Moonfin. Corrige los duplicados.");
        if (candidates.Length == 0)
            throw new SingleEpisodeError("untracked_series",
                "La serie no aparece entre las series de Sonarr guardadas en Moonfin.");

        var follow = candidates[0];
        if (!string.Equals(follow.Source, "disney", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(follow.StreamingUrl))
            throw new SingleEpisodeError("series_not_disney",
                "Guarda la serie con Disney+ y una URL de la serie antes de procesar episodios.");
        if (string.IsNullOrWhiteSpace(follow.Language))
            throw new SingleEpisodeError("missing_language",
                "Selecciona un idioma para la serie y guarda sus preferencias.");
        if (string.IsNullOrWhiteSpace(cfg.BazarrApiKey) ||
            !SafeHttpUri(cfg.BazarrUrl, out var bazarr))
            throw new SingleEpisodeError("bazarr_not_configured",
                "Configura la URL y la clave API de Bazarr en Moonfin.");
        if (string.IsNullOrWhiteSpace(cfg.BridgeToken) ||
            !SafeHttpUri(cfg.BridgeUrl, out var agent))
            throw new SingleEpisodeError("agent_not_configured",
                "Configura la URL interna y el token del agente Docker y pulsa Probar agente.");

        // Only consult this Sonarr series in Bazarr. Never send jobs for other
        // episodes, even when there are many pending titles in Bazarr.
        using var query = new HttpRequestMessage(HttpMethod.Get, new Uri(
            bazarr!.AbsoluteUri.TrimEnd('/') + "/api/episodes?seriesid%5B%5D="
            + seriesId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        query.Headers.TryAddWithoutValidation("X-API-KEY", cfg.BazarrApiKey);
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(query, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new SingleEpisodeError("bazarr_unreachable",
                "Jellyfin no pudo comunicarse con Bazarr desde su contenedor.", 502);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SingleEpisodeError("bazarr_timeout",
                "Bazarr tardó demasiado en responder.", 504);
        }
        using (response)
        {
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new SingleEpisodeError("bazarr_unauthorized",
                    "Bazarr rechazó la clave API guardada en Jellyfin. Pulsa Guardar y probar Bazarr.", 502);
            if (!response.IsSuccessStatusCode)
                throw new SingleEpisodeError("bazarr_http_error",
                    $"Bazarr rechazó la consulta de episodios (HTTP {(int)response.StatusCode}).", 502);
            if (response.Content.Headers.ContentLength > 4_000_000)
                throw new SingleEpisodeError("bazarr_response_large",
                    "Bazarr devolvió demasiados datos.", 502);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length > 4_000_000)
                throw new SingleEpisodeError("bazarr_response_large",
                    "Bazarr devolvió demasiados datos.", 502);
            using var data = JsonDocument.Parse(bytes);
            var plan = SingleEpisodePlanner.Plan(data.RootElement, seriesId, season, episode,
                follow.Language, follow.Source, follow.StreamingUrl, follow.EpisodeMapping);
            if (plan is null)
                return new SingleEpisodeSummary(0, 0,
                    "Bazarr no tiene registrado ese episodio con archivo y subtítulo pendiente en el idioma solicitado.");

            var body = JsonSerializer.Serialize(new
            {
                jobs = new[] { plan.ToPayload() },
                bazarr_url = cfg.BazarrUrl,
                bazarr_api_key = cfg.BazarrApiKey
            });
            using var post = new HttpRequestMessage(HttpMethod.Post,
                new Uri(agent!.AbsoluteUri.TrimEnd('/') + "/v1/scan"));
            post.Headers.TryAddWithoutValidation("X-Bridge-Key", cfg.BridgeToken);
            post.Content = new StringContent(body, Encoding.UTF8, "application/json");
            HttpResponseMessage result;
            try
            {
                result = await Http.SendAsync(post, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                throw new SingleEpisodeError("agent_unreachable",
                    "Jellyfin no puede conectarse al agente Docker. Comprueba la red y pulsa Probar agente.", 502);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new SingleEpisodeError("agent_timeout",
                    "El agente tardó demasiado en responder a la solicitud.", 504);
            }
            using (result)
            {
                if (result.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                    throw new SingleEpisodeError("agent_unauthorized",
                        "El agente Docker rechazó el token configurado en Moonfin. Revisa el token y pulsa Probar agente.", 502);
                if (!result.IsSuccessStatusCode)
                    throw new SingleEpisodeError("agent_rejected",
                        $"El agente rechazó el trabajo (HTTP {(int)result.StatusCode}). Revisa la URL de la serie Disney+ y los ajustes del agente.", 502);
                if (result.Content.Headers.ContentLength > 20_000)
                    throw new SingleEpisodeError("agent_response_large",
                        "El agente devolvió una respuesta demasiado grande.", 502);
                var statusBytes = await result.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (statusBytes.Length > 20_000)
                    throw new SingleEpisodeError("agent_response_large",
                        "El agente devolvió una respuesta demasiado grande.", 502);
                using var status = JsonDocument.Parse(statusBytes);
                var queued = ReadInt(status.RootElement, "queued", out var n) ? n : 0;
                var cooldown = ReadInt(status.RootElement, "cooldown", out var waiting) ? waiting : 0;
                if (queued > 1 || queued < 0 || cooldown < 0)
                    throw new SingleEpisodeError("invalid_agent_response",
                        "El agente devolvió contadores de cola inesperados.", 502);
                var message = queued == 1
                    ? $"Un episodio enviado: Sonarr T{season}E{episode} → Disney+ T{plan.StreamingSeason}E{plan.StreamingEpisode}. Consulta Estado y trabajos."
                    : cooldown > 0
                        ? "Este episodio está en espera por el límite de reintentos del agente (24 horas)."
                        : "El agente no aceptó el trabajo. Consulta Estado y trabajos.";
                return new SingleEpisodeSummary(queued, cooldown, message);
            }
        }
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
public sealed record SingleEpisodeSummary(int Queued, int Cooldown, string Message);
