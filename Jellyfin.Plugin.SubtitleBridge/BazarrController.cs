using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleBridge;

/// <summary>
/// Read missing subtitles and request a single episode search in Bazarr.
/// This controller only operates on series the administrator already tracked
/// with Bazarr as the selected provider.
/// </summary>
[ApiController]
[Route("MoonfinSubtitleBridge/bazarr")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class BazarrController : ControllerBase
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    [HttpGet("episodes")]
    public async Task<IActionResult> Episodes([FromQuery] int seriesId, CancellationToken cancellationToken)
    {
        if (!TryGetConfiguration(seriesId, out var url, out var key, out var language, out var error))
            return error!;

        try
        {
            var matches = await GetMissingEpisodes(url!, key!, seriesId, language!, cancellationToken).ConfigureAwait(false);
            return Ok(new
            {
                seriesId,
                language,
                episodes = matches.Select(ep => new
                {
                    id = ep.EpisodeId,
                    season = ep.Season,
                    episode = ep.EpisodeNumber,
                    title = ep.Title
                }).ToArray()
            });
        }
        catch (BazarrCallException e)
        {
            return StatusCode(502, new { error = e.Message });
        }
        catch (HttpRequestException)
        {
            return StatusCode(502, new { error = "Bazarr no responde. Comprueba la conexión de red." });
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(504, new { error = "Bazarr tardó demasiado en responder." });
        }
        catch (JsonException)
        {
            return StatusCode(502, new { error = "Bazarr devolvió un JSON de episodios inesperado." });
        }
    }

    [HttpPost("search")]
    public async Task<IActionResult> Search([FromBody] EpisodeSearchRequest? data, CancellationToken cancellationToken)
    {
        if (data is null || data.SeriesId < 1 || data.EpisodeId < 1)
            return BadRequest(new { error = "Indica una serie y un episodio válidos." });
        if (!TryGetConfiguration(data.SeriesId, out var url, out var key, out var language, out var error))
            return error!;

        try
        {
            // Re-read Bazarr before mutating anything. We only accept the ID of
            // an episode Bazarr reports as missing this tracked language.
            var missing = await GetMissingEpisodes(url!, key!, data.SeriesId, language!, cancellationToken).ConfigureAwait(false);
            if (!missing.Any(ep => ep.EpisodeId == data.EpisodeId))
                return Conflict(new { error = "El episodio ya no figura con ese idioma pendiente en Bazarr." });

            using var request = new HttpRequestMessage(HttpMethod.Patch, BuildUri(url!, "api/episodes/subtitles"));
            request.Headers.TryAddWithoutValidation("X-API-KEY", key);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["seriesid"] = data.SeriesId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["episodeid"] = data.EpisodeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["language"] = language!,
                ["forced"] = "false",
                ["hi"] = "false"
            });
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return StatusCode(502, new { error = $"Bazarr rechazó la búsqueda (HTTP {(int)response.StatusCode})." });
            return Ok(new
            {
                requested = true,
                episodeId = data.EpisodeId,
                message = "Búsqueda solicitada a Bazarr; la descarga puede tardar o no encontrar resultados."
            });
        }
        catch (BazarrCallException e)
        {
            return StatusCode(502, new { error = e.Message });
        }
        catch (HttpRequestException)
        {
            return StatusCode(502, new { error = "No se pudo enviar la búsqueda a Bazarr." });
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(504, new { error = "Tiempo agotado contactando a Bazarr." });
        }
        catch (JsonException)
        {
            return StatusCode(502, new { error = "Bazarr devolvió metadatos no válidos." });
        }
    }

    private static bool TryGetConfiguration(
        int seriesId,
        out string? url,
        out string? key,
        out string? language,
        out IActionResult? error)
    {
        url = null;
        key = null;
        language = null;
        error = null;
        if (seriesId < 1)
        {
            error = new BadRequestObjectResult(new { error = "ID de serie no válido." });
            return false;
        }

        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null)
        {
            error = new ObjectResult(new { error = "Plugin no inicializado." }) { StatusCode = 503 };
            return false;
        }

        var tracked = cfg.TrackedItems?.FirstOrDefault(item =>
            item.Id == seriesId
            && string.Equals(item.Service, "sonarr", StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Source, "bazarr", StringComparison.OrdinalIgnoreCase));
        if (tracked is null)
        {
            error = new ConflictObjectResult(new { error = "Primero guarda la serie con la fuente Proveedores Bazarr." });
            return false;
        }

        if (string.IsNullOrWhiteSpace(cfg.BazarrUrl) || string.IsNullOrWhiteSpace(cfg.BazarrApiKey))
        {
            error = new BadRequestObjectResult(new { error = "Configura la URL y la API key de Bazarr primero." });
            return false;
        }

        if (!Uri.TryCreate(cfg.BazarrUrl.TrimEnd('/'), UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            error = new BadRequestObjectResult(new { error = "URL de Bazarr no válida." });
            return false;
        }
        if (string.IsNullOrWhiteSpace(tracked.Language) || tracked.Language.Length > 12
            || tracked.Language.Any(ch => !(char.IsLetter(ch) || ch == '-')))
        {
            error = new BadRequestObjectResult(new { error = "Idioma de seguimiento no válido." });
            return false;
        }

        url = baseUri.AbsoluteUri.TrimEnd('/');
        key = cfg.BazarrApiKey;
        language = tracked.Language;
        return true;
    }

    private static Uri BuildUri(string configuredUrl, string relativePath)
    {
        // Keep any reverse-proxy path prefix while preventing path injection.
        return new Uri(configuredUrl.TrimEnd('/') + "/" + relativePath);
    }

    private static async Task<List<MissingEpisode>> GetMissingEpisodes(
        string url,
        string key,
        int seriesId,
        string language,
        CancellationToken ct)
    {
        var uri = BuildUri(url, "api/episodes?seriesid%5B%5D="
            + seriesId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("X-API-KEY", key);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new BazarrCallException($"No se pudieron consultar los episodios en Bazarr (HTTP {(int)response.StatusCode}).");
        if (response.Content.Headers.ContentLength > 4_000_000)
            throw new BazarrCallException("Bazarr devolvió demasiados datos.");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length > 4_000_000)
            throw new BazarrCallException("Bazarr devolvió demasiados datos.");

        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("data", out var list)
            || list.ValueKind != JsonValueKind.Array)
            throw new BazarrCallException("La respuesta de Bazarr no contiene una lista de episodios.");

        var found = new List<MissingEpisode>();
        foreach (var ep in list.EnumerateArray())
        {
            if (ep.ValueKind != JsonValueKind.Object)
                continue;
            if (!TryInteger(ep, "sonarrSeriesId", out var returnedSeriesId)
                || returnedSeriesId != seriesId
                || !TryInteger(ep, "sonarrEpisodeId", out var epId) || epId < 1)
                continue;
            if (!ep.TryGetProperty("missing_subtitles", out var missing)
                || missing.ValueKind != JsonValueKind.Array)
                continue;

            bool needsLanguage = missing.EnumerateArray().Any(x =>
                x.ValueKind == JsonValueKind.Object
                && x.TryGetProperty("code2", out var code)
                && code.ValueKind == JsonValueKind.String
                && string.Equals(code.GetString(), language, StringComparison.OrdinalIgnoreCase)
                && !IsFlagSet(x, "forced")
                && !IsFlagSet(x, "hi"));
            if (!needsLanguage)
                continue;

            _ = TryInteger(ep, "season", out var season);
            _ = TryInteger(ep, "episode", out var episode);
            var title = ep.TryGetProperty("title", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString() ?? string.Empty : string.Empty;
            found.Add(new MissingEpisode(epId, season, episode, title));
        }
        return found.OrderBy(ep => ep.Season).ThenBy(ep => ep.EpisodeNumber).Take(200).ToList();
    }

    private static bool TryInteger(JsonElement json, string field, out int number)
    {
        number = 0;
        return json.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out number);
    }

    private static bool IsFlagSet(JsonElement json, string field)
    {
        return json.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.True;
    }

    private sealed record MissingEpisode(int EpisodeId, int Season, int EpisodeNumber, string Title);

    private sealed class BazarrCallException(string message) : Exception(message);
}

public sealed class EpisodeSearchRequest
{
    public int SeriesId { get; set; }

    public int EpisodeId { get; set; }
}
