using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SubtitleBridge;

/// <summary>
/// Diagnostic simulation of the preconditions for NewEpisodeListener.
/// IMPORTANT: Only HTTP GET requests to Sonarr and Bazarr, never /v1/scan,
/// uploads, library modifications or generated Jellyfin ItemAdded events.
/// Already-indexed episodes will not automatically trigger the listener.
/// </summary>
public static class AutomaticDetectionProbe
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public sealed record Check(string Code, bool Ok, string Message);
    public sealed record Report(bool Ready, int? ProviderSeason, int? ProviderEpisode,
        string Notice, IReadOnlyList<Check> Checks);

    public static async Task<Report> InspectAsync(
        ILibraryManager library, int seriesId, int season, int episode, CancellationToken ct)
    {
        if (seriesId < 1 || season < 0 || season > 100 || episode < 1 || episode > 999)
            throw new ArgumentOutOfRangeException(nameof(episode));

        var cfg = Plugin.Instance?.Configuration
            ?? throw new SingleEpisodeError("plugin_uninitialized", "Moonfin no está inicializado.");

        var tracked = (cfg.TrackedItems ?? []).Where(x => x.Id == seriesId &&
            string.Equals(x.Service, "sonarr", StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        if (tracked.Length != 1)
            throw new SingleEpisodeError("tracked_series_invalid",
                "Selecciona una única serie de Sonarr guardada en Moonfin.");

        var follow = tracked[0];
        var checks = new List<Check>();
        checks.Add(new Check("automatic", follow.Automatic,
            follow.Automatic ? "Automático está activado." :
                "Automático está desactivado: un episodio nuevo no dispararía el proceso."));

        var providerOk = follow.Source is "disney"
            && !string.IsNullOrWhiteSpace(follow.StreamingUrl);
        checks.Add(new Check("provider", providerOk,
            providerOk ? "Disney+ y URL de la serie están guardados." :
                "Selecciona Disney+ y guarda la URL de la serie."));

        var agentOk = !string.IsNullOrWhiteSpace(cfg.BridgeToken)
            && StreamingScanDispatcher.SafeHttpUri(cfg.BridgeUrl, out _);
        checks.Add(new Check("agent_config", agentOk,
            agentOk ? "Agente configurado (conectividad no comprobada en esta prueba)." :
                "Configura la URL y el token del agente Docker."));

        var sonarrTvdb = await ReadSonarrTvdbAsync(cfg.SonarrUrl, cfg.SonarrApiKey,
            seriesId, ct).ConfigureAwait(false);
        checks.Add(new Check("sonarr_tvdb", sonarrTvdb.HasValue,
            sonarrTvdb.HasValue ? "Sonarr devuelve el identificador TVDB de la serie." :
                "Sonarr no proporciona un TVDB válido para esta serie."));

        // Search only the requested season/episode within Jellyfin's index.
        // Compare the parent series TVDB exactly as NewEpisodeListener does.
        var items = library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            IndexNumber = episode,
            ParentIndexNumber = season,
            IsMissing = false,
            Limit = 251
        });
        var matchCount = 0;
        foreach (var candidate in items.OfType<Episode>())
        {
            if (candidate.IsMissingEpisode || string.IsNullOrWhiteSpace(candidate.Path)
                || candidate.ParentIndexNumber != season || candidate.IndexNumber != episode)
                continue;
            var series = candidate.Series;
            if (series is null
                || !series.TryGetProviderId(MetadataProvider.Tvdb, out var raw)
                || !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var tvdb)
                || tvdb < 1 || sonarrTvdb != tvdb)
                continue;
            matchCount++;
        }
        var jellyfinOk = matchCount == 1 && items.Count <= 250;
        checks.Add(new Check("jellyfin_tvdb", jellyfinOk,
            matchCount == 0 ? "Jellyfin no encontró un archivo T/E de esta serie con TVDB coincidente." :
            matchCount > 1 ? "Jellyfin encontró varias coincidencias; revisa las duplicaciones." :
            items.Count > 250 ? "Jellyfin devolvió demasiados candidatos para verificar el episodio." :
            "Jellyfin tiene el archivo del episodio y su TVDB coincide con Sonarr."));

        int? providerSeason = null, providerEpisode = null;
        bool mappingOk;
        try
        {
            var rules = EpisodeMapper.Parse(follow.EpisodeMapping);
            mappingOk = EpisodeMapper.TryMap(rules, season, episode,
                out var mappedSeason, out var mappedEpisode);
            if (mappingOk)
            {
                providerSeason = mappedSeason;
                providerEpisode = mappedEpisode;
            }
        }
        catch (FormatException)
        {
            mappingOk = false;
        }
        checks.Add(new Check("mapping", mappingOk,
            mappingOk ? $"Equivalencia encontrada: Sonarr T{season}E{episode} → streaming T{providerSeason}E{providerEpisode}." :
                "Las reglas no incluyen este episodio o contienen errores."));

        var bazarrPending = await ReadBazarrWantedAsync(cfg.BazarrUrl, cfg.BazarrApiKey,
            seriesId, season, episode, follow.Language, ct).ConfigureAwait(false);
        checks.Add(new Check("bazarr_wanted", bazarrPending,
            bazarrPending ? "Bazarr indica que falta el subtítulo normal del idioma seleccionado." :
            "Bazarr no informa ese subtítulo como pendiente en la lista examinada (hasta 500 episodios)."));

        return new Report(checks.All(x => x.Ok), providerSeason, providerEpisode,
            "Simulación de requisitos, solo lectura: no reproduce ItemAdded ni envía trabajos. " +
            "Un episodio que Jellyfin ya conoce no se vuelve nuevo al ejecutar esta prueba.",
            checks);
    }

    private static async Task<int?> ReadSonarrTvdbAsync(
        string? url, string? apiKey, int seriesId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey) ||
            !StreamingScanDispatcher.SafeHttpUri(url, out var sonarr))
            throw new SingleEpisodeError("sonarr_not_configured",
                "Configura y prueba primero la URL y clave API de Sonarr.");
        foreach (var version in new[] { "v3", "v5" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(sonarr!.AbsoluteUri.TrimEnd('/') + "/api/" + version + "/series/"
                    + seriesId.ToString(CultureInfo.InvariantCulture)));
            request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
            using var response = await SendSafelyAsync(request, "Sonarr", ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) continue;
            if (!response.IsSuccessStatusCode)
                throw new SingleEpisodeError("sonarr_http_error",
                    $"Sonarr rechazó la consulta (HTTP {(int)response.StatusCode}).", 502);
            var bytes = await ReadSafelyAsync(response, 256_000, "Sonarr", ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("tvdbId", out var node)
                && node.ValueKind == JsonValueKind.Number
                && node.TryGetInt32(out var value) && value > 0)
                return value;
            return null;
        }
        return null;
    }

    private static async Task<bool> ReadBazarrWantedAsync(string? url, string? apiKey,
        int seriesId, int season, int episode, string language, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey) ||
            !StreamingScanDispatcher.SafeHttpUri(url, out var bazarr))
            throw new SingleEpisodeError("bazarr_not_configured",
                "Configura y prueba primero la URL y clave API de Bazarr.");

        var found = false;
        var pending = false;
        for (var start = 0; start < 500; start += 100)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(bazarr!.AbsoluteUri.TrimEnd('/') + "/api/episodes/wanted?start="
                    + start + "&length=100"));
            request.Headers.TryAddWithoutValidation("X-API-KEY", apiKey);
            using var response = await SendSafelyAsync(request, "Bazarr", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new SingleEpisodeError("bazarr_http_error",
                    $"Bazarr rechazó la consulta (HTTP {(int)response.StatusCode}).", 502);
            var bytes = await ReadSafelyAsync(response, 2_000_000, "Bazarr", ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            var page = AutomaticDetectionWanted.Evaluate(document.RootElement,
                seriesId, season, episode, language);
            if (page.Found)
            {
                if (found)
                    throw new SingleEpisodeError("ambiguous_episode",
                        "Bazarr devuelve varias filas para ese episodio.");
                found = true;
                pending = page.MissingLanguage;
            }
            var items = document.RootElement.GetProperty("data");
            if (items.GetArrayLength() < 100) break;
        }
        return found && pending;
    }

    private static async Task<HttpResponseMessage> SendSafelyAsync(
        HttpRequestMessage request, string service, CancellationToken ct)
    {
        try
        {
            return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new SingleEpisodeError("probe_network_error",
                "Jellyfin no pudo consultar " + service + " desde su contenedor.", 502);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SingleEpisodeError("probe_timeout",
                service + " tardó demasiado en responder.", 504);
        }
    }

    private static async Task<byte[]> ReadSafelyAsync(HttpResponseMessage response,
        int limit, string service, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit)
            throw new SingleEpisodeError("probe_response_large",
                service + " devolvió demasiados datos.", 502);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length > limit)
            throw new SingleEpisodeError("probe_response_large",
                service + " devolvió demasiados datos.", 502);
        return bytes;
    }
}
