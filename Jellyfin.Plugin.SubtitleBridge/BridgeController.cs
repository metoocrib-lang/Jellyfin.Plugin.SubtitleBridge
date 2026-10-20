using System.Net.Http;
using System.Text;
using System.Text.Json;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleBridge;

[ApiController]
[Route("MoonfinSubtitleBridge")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class BridgeController : ControllerBase
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    [HttpGet("health")]
    public Task<IActionResult> Health(CancellationToken ct) => Relay(HttpMethod.Get, "health", null, ct);

    [HttpGet("series")]
    public Task<IActionResult> Series(CancellationToken ct) => Relay(HttpMethod.Get, "series", null, ct);

    [HttpGet("episodes")]
    public Task<IActionResult> Episodes([FromQuery] string id, CancellationToken ct)
        => Relay(HttpMethod.Get, "episodes?id=" + Uri.EscapeDataString(id), null, ct);

    [HttpPost("watch")]
    public Task<IActionResult> Watch([FromBody] JsonElement data, CancellationToken ct)
        => Relay(HttpMethod.Post, "watch", data, ct);

    [HttpPost("remove")]
    public Task<IActionResult> Remove([FromBody] JsonElement data, CancellationToken ct)
        => Relay(HttpMethod.Post, "remove", data, ct);

    [HttpPost("link")]
    public Task<IActionResult> Link([FromBody] JsonElement data, CancellationToken ct)
        => Relay(HttpMethod.Post, "link", data, ct);

    [HttpPost("scan")]
    public Task<IActionResult> Scan([FromBody] JsonElement data, CancellationToken ct)
        => Relay(HttpMethod.Post, "scan", data, ct);

    private async Task<IActionResult> Relay(HttpMethod method, string route, JsonElement? data, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BridgeToken) ||
            !Uri.TryCreate(cfg.BridgeUrl.TrimEnd('/') + "/integration/" + route, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return BadRequest(new { error = "Configura la URL HTTP(S) y el token local del agente." });
        }

        try
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.TryAddWithoutValidation("X-Bridge-Key", cfg.BridgeToken);
            if (data.HasValue)
            {
                request.Content = new StringContent(data.Value.GetRawText(), Encoding.UTF8, "application/json");
            }
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > 1_000_000)
                return StatusCode(502, new { error = "Respuesta del agente demasiado grande." });
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length > 1_000_000)
                return StatusCode(502, new { error = "Respuesta del agente demasiado grande." });
            return new ContentResult
            {
                Content = Encoding.UTF8.GetString(bytes),
                ContentType = "application/json; charset=utf-8",
                StatusCode = (int)response.StatusCode
            };
        }
        catch (HttpRequestException)
        {
            return StatusCode(502, new { error = "Jellyfin no puede conectar al contenedor agente." });
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return StatusCode(504, new { error = "Tiempo agotado esperando al agente." });
        }
    }
}
