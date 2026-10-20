using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubtitleBridge;

/// <summary>
/// Explicit, per-series cross-catalog mapping. Sonarr IDs and episode numbering
/// must never be rewritten: these rules affect only the streaming lookup.
/// </summary>
public static partial class EpisodeMapper
{
    private const int MaxRules = 100;
    private const int MaxTextLength = 4096;
    private const int MaxEpisode = 999;

    [GeneratedRegex(@"^(?<ss>\d{1,3})x(?<from>\d{1,3})(?:-(?<to>\d{1,3}))?\s*=\s*(?<ps>\d{1,3})x(?<first>\d{1,3})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MappingPattern();

    public sealed record Rule(int SonarrSeason, int SonarrStart, int SonarrEnd,
        int ProviderSeason, int ProviderStart)
    {
        public int ProviderEnd => ProviderStart + SonarrEnd - SonarrStart;
    }

    public static IReadOnlyList<Rule> Parse(string? specification)
    {
        if (string.IsNullOrWhiteSpace(specification))
            return Array.Empty<Rule>();
        if (specification.Length > MaxTextLength)
            throw new FormatException("El mapeo de episodios es demasiado largo.");

        var lines = specification.Split('\n',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > MaxRules)
            throw new FormatException("Hay demasiadas reglas de equivalencia.");

        var rules = new List<Rule>(lines.Length);
        foreach (var line in lines)
        {
            var match = MappingPattern().Match(line);
            if (!match.Success)
                throw new FormatException("Formato de equivalencias inválido. Ejemplo: 17x14-26=2x1");

            var ss = int.Parse(match.Groups["ss"].Value, CultureInfo.InvariantCulture);
            var start = int.Parse(match.Groups["from"].Value, CultureInfo.InvariantCulture);
            var end = match.Groups["to"].Success
                ? int.Parse(match.Groups["to"].Value, CultureInfo.InvariantCulture)
                : start;
            var ps = int.Parse(match.Groups["ps"].Value, CultureInfo.InvariantCulture);
            var first = int.Parse(match.Groups["first"].Value, CultureInfo.InvariantCulture);
            var rule = new Rule(ss, start, end, ps, first);

            if (ss > 100 || ps < 1 || ps > 100 || start < 1 || end < start
                || end > MaxEpisode || first < 1 || rule.ProviderEnd > MaxEpisode)
                throw new FormatException("La equivalencia contiene una temporada o episodio fuera de rango.");

            foreach (var previous in rules)
            {
                if (previous.SonarrSeason == ss
                    && Intersects(previous.SonarrStart, previous.SonarrEnd, start, end))
                    throw new FormatException("Hay episodios de Sonarr cubiertos por varias reglas.");

                if (previous.ProviderSeason == ps
                    && Intersects(previous.ProviderStart, previous.ProviderEnd, first, rule.ProviderEnd))
                    throw new FormatException("Dos episodios de Sonarr apuntan al mismo episodio de streaming.");
            }

            rules.Add(rule);
        }
        return rules;
    }

    private static bool Intersects(int fromA, int toA, int fromB, int toB)
        => fromA <= toB && fromB <= toA;

    /// <summary>
    /// No rules: unchanged numbering. Rules configured: only mapped episodes
    /// are eligible; never silently fall back to the wrong streaming season.
    /// </summary>
    public static bool TryMap(IReadOnlyList<Rule> rules, int sonarrSeason, int sonarrEpisode,
        out int providerSeason, out int providerEpisode)
    {
        providerSeason = 0;
        providerEpisode = 0;
        if (rules.Count == 0)
        {
            providerSeason = sonarrSeason;
            providerEpisode = sonarrEpisode;
            return true;
        }

        foreach (var rule in rules)
        {
            if (rule.SonarrSeason != sonarrSeason
                || sonarrEpisode < rule.SonarrStart || sonarrEpisode > rule.SonarrEnd)
                continue;
            providerSeason = rule.ProviderSeason;
            providerEpisode = rule.ProviderStart + sonarrEpisode - rule.SonarrStart;
            return true;
        }
        return false;
    }
}
