using System.Text.Json;

namespace Jellyfin.Plugin.SubtitleBridge;

/// <summary>
/// Pure, read-only check of Bazarr's wanted list, matching exactly the
/// episode_number, Sonarr IDs and missing-language criteria used by ScanAsync.
/// It never constructs or dispatches an agent job.
/// </summary>
public static class AutomaticDetectionWanted
{
    public sealed record Match(bool Found, bool MissingLanguage, int SonarrEpisodeId);

    public static Match Evaluate(
        JsonElement root, int seriesId, int season, int episode, string language)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var items)
            || items.ValueKind != JsonValueKind.Array)
            throw new FormatException("Bazarr devolvió un formato inesperado.");

        var found = false;
        var missing = false;
        var matchedEpisodeId = 0;
        var episodeNumber = season.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "x" + episode.ToString(System.Globalization.CultureInfo.InvariantCulture);

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !TryInt(item, "sonarrSeriesId", out var sid) || sid != seriesId
                || !TryInt(item, "sonarrEpisodeId", out var eid) || eid < 1
                || !item.TryGetProperty("episode_number", out var number)
                || number.ValueKind != JsonValueKind.String
                || number.GetString() != episodeNumber)
                continue;

            if (found)
                throw new InvalidOperationException("Bazarr devolvió resultados duplicados para el episodio.");
            found = true;
            matchedEpisodeId = eid;
            missing = item.TryGetProperty("missing_subtitles", out var values)
                && values.ValueKind == JsonValueKind.Array
                && values.EnumerateArray().Any(entry =>
                    entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("code2", out var code)
                    && code.ValueKind == JsonValueKind.String
                    && string.Equals(code.GetString(), language, StringComparison.OrdinalIgnoreCase)
                    && !IsTrue(entry, "forced") && !IsTrue(entry, "hi"));
        }
        return new Match(found, missing, matchedEpisodeId);
    }

    private static bool IsTrue(JsonElement json, string name)
        => json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static bool TryInt(JsonElement json, string name, out int value)
    {
        value = 0;
        return json.TryGetProperty(name, out var node)
            && node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out value);
    }
}
