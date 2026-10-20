using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.SubtitleBridge.Configuration;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleBridge;

/// <summary>
/// The Jellyfin server calls Bazarr, Sonarr and Radarr directly. API keys never
/// leave Jellyfin for the user's browser or appear in response bodies.
/// </summary>
[ApiController]
[Route("MoonfinSubtitleBridge/connections")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class ConnectionsController : ControllerBase
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    [HttpGet("test")]
    public Task<IActionResult> Test([FromQuery] string service, CancellationToken cancellationToken)
        => Query(service, false, cancellationToken);

    [HttpGet("library")]
    public Task<IActionResult> Library([FromQuery] string service, CancellationToken cancellationToken)
        => Query(service, true, cancellationToken);

    private async Task<IActionResult> Query(string? service, bool library, CancellationToken ct)
    {
        service = service?.Trim().ToLowerInvariant();
        if (service is not ("sonarr" or "radarr" or "bazarr"))
            return BadRequest(new { error = "Servicio no compatible." });
        if (library && service == "bazarr")
            return BadRequest(new { error = "El catálogo se consulta en Sonarr y Radarr." });

        var config = Plugin.Instance?.Configuration;
        if (config is null)
            return StatusCode(503, new { error = "El plugin no está inicializado." });

        var (baseUrl, apiKey) = service switch
        {
            "sonarr" => (config.SonarrUrl, config.SonarrApiKey),
            "radarr" => (config.RadarrUrl, config.RadarrApiKey),
            _ => (config.BazarrUrl, config.BazarrApiKey)
        };
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
            return BadRequest(new { error = $"Configura la URL y la API key de {service} primero." });

        if (!Uri.TryCreate(baseUrl.TrimEnd('/'), UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment))
            return BadRequest(new { error = "Utiliza una URL HTTP(S) válida sin usuario, contraseña ni parámetros." });

        // Preserve reverse-proxy path prefixes such as http://server:8989/sonarr.
        string[] suffixes = service switch
        {
            "sonarr" when library => ["/api/v3/series", "/api/v5/series"],
            "sonarr" => ["/api/v3/system/status", "/api/v5/system/status"],
            "radarr" when library => ["/api/v3/movie"],
            "radarr" => ["/api/v3/system/status"],
            _ => ["/api/system/status"]
        };

        try
        {
            foreach (var (suffix, index) in suffixes.Select((suffix, index) => (suffix, index)))
            {
                var url = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + suffix);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);

                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound && index < suffixes.Length - 1)
                    continue;
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return StatusCode(502, new { error = $"La API key de {service} fue rechazada (HTTP {(int)response.StatusCode})." });
                if (!response.IsSuccessStatusCode)
                    return StatusCode(502, new { error = $"{service} respondió HTTP {(int)response.StatusCode}. Revisa URL y ruta base." });
                if (response.Content.Headers.ContentLength > 4_000_000)
                    return StatusCode(502, new { error = "La respuesta excede el límite permitido." });
                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (bytes.Length > 4_000_000)
                    return StatusCode(502, new { error = "La respuesta excede el límite permitido." });

                using var document = JsonDocument.Parse(bytes);
                if (library)
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Array)
                        return StatusCode(502, new { error = "El catálogo recibido no tiene el formato esperado." });
                    return new ContentResult
                    {
                        Content = Encoding.UTF8.GetString(bytes),
                        ContentType = "application/json; charset=utf-8",
                        StatusCode = 200
                    };
                }

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return StatusCode(502, new { error = "La respuesta de estado no es un objeto JSON." });
                var root = document.RootElement;
                string? version = root.TryGetProperty("version", out var versionNode)
                    && versionNode.ValueKind == JsonValueKind.String ? versionNode.GetString() : null;
                return Ok(new { ok = true, service, version, message = "Conexión correcta" });
            }
            return StatusCode(502, new { error = $"{service} no expone una ruta API compatible." });
        }
        catch (HttpRequestException)
        {
            return StatusCode(502, new { error = $"Jellyfin no puede acceder a {service}. Comprueba el hostname, puerto y red Docker." });
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return StatusCode(504, new { error = $"Tiempo agotado conectando a {service}." });
        }
        catch (JsonException)
        {
            return StatusCode(502, new { error = $"{service} devolvió una respuesta no JSON (quizás un proxy o pantalla de login)." });
        }
    }
}
