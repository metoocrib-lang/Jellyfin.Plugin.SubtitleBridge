using System.Net.Http;
using System.Text;
using System.Text.Json;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleBridge;

[ApiController]
[Route("MoonfinSubtitleBridge/streaming")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class StreamingController : ControllerBase
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    [HttpGet("status")]
    public Task<IActionResult> Status(CancellationToken ct) => Relay(HttpMethod.Get, "/v1/status", null, ct);

    [HttpPost("login")]
    public Task<IActionResult> Login([FromBody] LoginRequest? data, CancellationToken ct)
    {
        if (data?.Provider is not ("disney" or "netflix" or "prime" or "max" or "apple"))
            return Task.FromResult<IActionResult>(BadRequest(new { error = "Plataforma no válida" }));
        return Relay(HttpMethod.Post, "/v1/login", JsonSerializer.Serialize(new { provider = data.Provider }), ct);
    }

    [HttpPost("scan")]
    public async Task<IActionResult> Scan(CancellationToken ct)
    {
        try
        {
            return Ok(await StreamingScanDispatcher.ScanAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException
            or JsonException or TaskCanceledException)
        {
            // Never echo credentials, signed URLs or HTTP payloads.
            return StatusCode(502, new { error = "Revisión no disponible. Comprueba Bazarr, el agente y su token." });
        }
    }

    [HttpPost("episode")]
    public async Task<IActionResult> SingleEpisode([FromBody] SingleEpisodeRequest? data, CancellationToken ct)
    {
        if (data is null || data.SeriesId < 1 || data.Season < 0 || data.Season > 100
            || data.Episode < 1 || data.Episode > 999)
            return BadRequest(new { error = "Selecciona una serie, temporada y episodio válidos." });
        try
        {
            return Ok(await StreamingScanDispatcher.ScanSingleAsync(
                data.SeriesId, data.Season, data.Episode, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ArgumentException)
        {
            return BadRequest(new { error = "Identificadores de episodio inválidos." });
        }
        catch (FormatException)
        {
            return Conflict(new { error = "Revisa el formato y la ausencia de conflictos en las equivalencias." });
        }
        catch (InvalidOperationException)
        {
            return Conflict(new { error = "No se pudo procesar el episodio. Revisa la serie, las equivalencias, Bazarr y el agente." });
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException)
        {
            return StatusCode(502, new { error = "Error consultando Bazarr o el agente. Revisa conexiones y estado." });
        }
    }

    private async Task<IActionResult> Relay(HttpMethod method, string path, string? body, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BridgeToken) ||
            !StreamingScanDispatcher.SafeHttpUri(cfg.BridgeUrl, out var uri))
            return BadRequest(new { error = "Configura la URL y el token del agente local." });
        try
        {
            using var request = new HttpRequestMessage(method,
                new Uri(uri!.AbsoluteUri.TrimEnd('/') + path));
            request.Headers.TryAddWithoutValidation("X-Bridge-Key", cfg.BridgeToken);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > 250_000)
                return StatusCode(502, new { error = "Respuesta excesiva del agente." });
            var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (data.Length > 250_000) return StatusCode(502, new { error = "Respuesta excesiva del agente." });
            return new ContentResult {
                Content = Encoding.UTF8.GetString(data),
                ContentType = "application/json",
                StatusCode = (int)response.StatusCode
            };
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(502, new { error = "El agente no está disponible desde Jellyfin." });
        }
    }
}

public sealed class LoginRequest
{
    public string Provider { get; set; } = string.Empty;
}

public sealed class SingleEpisodeRequest
{
    public int SeriesId { get; set; }
    public int Season { get; set; }
    public int Episode { get; set; }
}
