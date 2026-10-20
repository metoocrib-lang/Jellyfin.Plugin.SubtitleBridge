using System.Text.Json;

namespace Jellyfin.Plugin.SubtitleBridge;

/// <summary>
/// Select exactly one Bazarr episode for a manual Disney+ request. Only
/// already-downloaded, missing-subtitle episodes can be sent to the worker.
/// The Sonarr IDs stay unchanged while the lookup coordinates are mapped.
/// </summary>
public static class SingleEpisodePlanner
{
    public sealed record PlannedJob(int SonarrSeriesId, int SonarrEpisodeId,
        int SonarrSeason, int SonarrEpisode, int StreamingSeason, int StreamingEpisode,
        string Language, string Provider, string StreamingUrl)
    {
        public Dictionary<string, object> ToPayload() => new()
        {
            ["provider"] = Provider,
            ["series_url"] = StreamingUrl,
            ["series_id"] = SonarrSeriesId,
            ["episode_id"] = SonarrEpisodeId,
            ["season"] = StreamingSeason,
            ["episode"] = StreamingEpisode,
            ["language"] = Language
        };
    }

    public static PlannedJob? Plan(JsonElement root, int seriesId, int season, int episode,
        string language, string provider, string streamingUrl, string? mapping)
    {
        if (seriesId < 1 || season < 0 || season > 100 || episode < 1 || episode > 999)
            throw new ArgumentOutOfRangeException(nameof(episode));
        // Deliberately support only the currently implemented streaming adapter.
        if (provider != "disney" || string.IsNullOrWhiteSpace(streamingUrl))
            throw new InvalidOperationException("Esta fuente de streaming aún no se puede procesar.");

        var rules = EpisodeMapper.Parse(mapping);
        if (!EpisodeMapper.TryMap(rules, season, episode, out var streamingSeason, out var streamingEpisode))
            throw new InvalidOperationException("El episodio no tiene una equivalencia configurada.");

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
            throw new FormatException("Bazarr no devolvió una lista de episodios válida.");

        PlannedJob? found = null;
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !ReadInt(item, "sonarrSeriesId", out var currentSeriesId)
                || currentSeriesId != seriesId
                || !ReadInt(item, "sonarrEpisodeId", out var currentEpisodeId)
                || currentEpisodeId < 1
                || !ReadInt(item, "season", out var currentSeason) || currentSeason != season
                || !ReadInt(item, "episode", out var currentEpisode) || currentEpisode != episode)
                continue;

            // Two entries for the same coordinates are ambiguous, even if only
            // one has an available subtitle or media file.
            if (found is not null || HasDuplicateCoordinate(data, seriesId, season, episode))
                throw new InvalidOperationException("Bazarr devolvió más de un episodio con esa numeración.");

            if (!item.TryGetProperty("path", out var path)
                || path.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(path.GetString())
                || !item.TryGetProperty("missing_subtitles", out var missing)
                || missing.ValueKind != JsonValueKind.Array
                || !missing.EnumerateArray().Any(entry =>
                    entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("code2", out var code)
                    && code.ValueKind == JsonValueKind.String
                    && string.Equals(code.GetString(), language, StringComparison.OrdinalIgnoreCase)
                    && !IsTrue(entry, "forced") && !IsTrue(entry, "hi")))
                return null;

            found = new PlannedJob(seriesId, currentEpisodeId, season, episode,
                streamingSeason, streamingEpisode, language, provider, streamingUrl);
        }

        return found;
    }

    private static bool HasDuplicateCoordinate(JsonElement data, int seriesId, int season, int episode)
    {
        var count = 0;
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && ReadInt(item, "sonarrSeriesId", out var sid) && sid == seriesId
                && ReadInt(item, "season", out var s) && s == season
                && ReadInt(item, "episode", out var e) && e == episode
                && ++count > 1)
                return true;
        }
        return false;
    }

    private static bool IsTrue(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool ReadInt(JsonElement item, string name, out int value)
    {
        value = 0;
        return item.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }
}
