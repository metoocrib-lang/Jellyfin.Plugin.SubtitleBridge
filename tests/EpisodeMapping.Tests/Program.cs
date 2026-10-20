using Jellyfin.Plugin.SubtitleBridge;

static void Expect(bool condition, string description)
{
    if (!condition) throw new Exception("Mapping test failed: " + description);
}

static void Check(IReadOnlyList<EpisodeMapper.Rule> rules, int season, int episode,
    int expectedSeason, int expectedEpisode)
{
    var found = EpisodeMapper.TryMap(rules, season, episode, out var mappedSeason, out var mappedEpisode);
    Expect(found && mappedSeason == expectedSeason && mappedEpisode == expectedEpisode,
        $"{season}x{episode} expected {expectedSeason}x{expectedEpisode}");
}

static void Invalid(string text)
{
    try { EpisodeMapper.Parse(text); }
    catch (FormatException) { return; }
    throw new Exception("Expected invalid mapping to be rejected: " + text);
}

const string bleach = """
17x1-13=1x1
17x14-26=2x1
17x27-40=3x1
17x41-50=4x1
""";
var rules = EpisodeMapper.Parse(bleach);
Expect(rules.Count == 4, "Four mapped cours");
Check(rules, 17, 1, 1, 1);
Check(rules, 17, 13, 1, 13);
Check(rules, 17, 14, 2, 1);
Check(rules, 17, 26, 2, 13);
Check(rules, 17, 27, 3, 1);
Check(rules, 17, 40, 3, 14);
Check(rules, 17, 41, 4, 1);
Check(rules, 17, 49, 4, 9);
Check(rules, 17, 50, 4, 10);
Expect(!EpisodeMapper.TryMap(rules, 16, 1, out _, out _), "Strict mode for unconfigured season");
Expect(!EpisodeMapper.TryMap(rules, 17, 51, out _, out _), "Strict mode for unmapped episode");
Check(EpisodeMapper.Parse(""), 17, 49, 17, 49);
Check(EpisodeMapper.Parse(" 17x49 = 4x9 "), 17, 49, 4, 9);
Invalid("17x1-13=1x1\n17x13-26=2x1");
Invalid("17x1-13=1x1\n17x14-26=1x13");
Invalid("17x20-19=2x1");
Invalid("17x1-13=1x995");
Invalid("17x1-13=0x1");
Invalid("17x1-13=2x0");
Invalid("17x1-13;17x14-26");
Invalid("ThisIsNotMapping");
Console.WriteLine("EpisodeMapper: all checks passed.");
