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


// A manual request may only send ONE existing Sonarr episode, never infer an
// episode from its position or mutate Sonarr/Bazarr IDs.
using var bazarr = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new
{
    data = new[] {
        new {
            sonarrSeriesId = 42, sonarrEpisodeId = 2800, season = 17, episode = 2,
            path = "/tv/Bleach/S17E02.mkv",
            missing_subtitles = new[] { new { code2 = "es", forced = false, hi = false } }
        }
    }
}));
var plan = SingleEpisodePlanner.Plan(bazarr.RootElement, 42, 17, 2, "es", "disney",
    "https://www.disneyplus.com/es-419/browse/entity-571475d5-8b1a-453f-a57f-26bb4dd13304", bleach);
Expect(plan is not null, "Plan found exact episode");
Expect(plan!.SonarrSeriesId == 42 && plan.SonarrEpisodeId == 2800, "Original IDs preserved");
Expect(plan.StreamingSeason == 1 && plan.StreamingEpisode == 2, "Season and episode mapped");
Expect(SingleEpisodePlanner.Plan(bazarr.RootElement, 42, 17, 3, "es", "disney",
    plan.StreamingUrl, bleach) is null, "Other episode must not be submitted");
Expect(SingleEpisodePlanner.Plan(bazarr.RootElement, 99, 17, 2, "es", "disney",
    plan.StreamingUrl, bleach) is null, "Wrong Sonarr series must not be submitted");
Expect(SingleEpisodePlanner.Plan(bazarr.RootElement, 42, 17, 2, "en", "disney",
    plan.StreamingUrl, bleach) is null, "Only requested missing language");

using var withoutPath = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new
{
    data = new[] {
        new { sonarrSeriesId = 42, sonarrEpisodeId = 2800, season = 17, episode = 2,
            path = "", missing_subtitles = new[]{new {code2="es", forced=false, hi=false}} }
    }
}));
Expect(SingleEpisodePlanner.Plan(withoutPath.RootElement, 42, 17, 2, "es", "disney",
    plan.StreamingUrl, bleach) is null, "Media path is mandatory");

using var duplicate = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new {
    data = new[] {
        new {sonarrSeriesId=42,sonarrEpisodeId=2800,season=17,episode=2,path="/tv/a.mkv",
             missing_subtitles=new[]{new{code2="es",forced=false,hi=false}}},
        new {sonarrSeriesId=42,sonarrEpisodeId=9999,season=17,episode=2,path="/tv/b.mkv",
             missing_subtitles=new[]{new{code2="es",forced=false,hi=false}}}
    }
}));
try {
    SingleEpisodePlanner.Plan(duplicate.RootElement, 42, 17, 2, "es", "disney",
        plan.StreamingUrl, bleach);
    throw new Exception("Ambiguous duplicates were accepted");
} catch (InvalidOperationException) { }

try {
    SingleEpisodePlanner.Plan(bazarr.RootElement, 42, 16, 1, "es", "disney",
        plan.StreamingUrl, bleach);
    throw new Exception("Unmapped season was accepted");
} catch (InvalidOperationException) { }
Console.WriteLine("SingleEpisodePlanner: all checks passed.");


// Typed diagnostic errors must be predictable and must not contain URLs or secrets.
try
{
    SingleEpisodePlanner.Plan(bazarr.RootElement, 42, 16, 1, "es", "disney",
        "https://www.disneyplus.com/es-419/browse/entity-571475d5-8b1a-453f-a57f-26bb4dd13304", bleach);
    throw new Exception("Unmapped episode should raise a typed diagnostic.");
}
catch (SingleEpisodeError error)
{
    Expect(error.Code == "unmapped_episode" && error.HttpStatus == 409, "Safe mapping diagnostic");
    Expect(!error.Message.Contains("https://", StringComparison.Ordinal), "No URL in diagnostic");
}
try
{
    SingleEpisodePlanner.Plan(duplicate.RootElement, 42, 17, 2, "es", "disney",
        "https://www.disneyplus.com/es-419/browse/entity-571475d5-8b1a-453f-a57f-26bb4dd13304", bleach);
    throw new Exception("Ambiguous episode should raise a typed diagnostic.");
}
catch (SingleEpisodeError error)
{
    Expect(error.Code == "ambiguous_episode", "Ambiguous Bazarr diagnostic");
}
Console.WriteLine("Single episode diagnostics: all checks passed.");


// Dry-run wanted-list matching uses precisely the fields of the automatic
// dispatcher and must never produce a job for another series or episode.
using var wanted = System.Text.Json.JsonDocument.Parse("""
{"data":[{"sonarrSeriesId":42,"sonarrEpisodeId":2801,"episode_number":"17x3",
"missing_subtitles":[{"code2":"es","forced":false,"hi":false}]}]}
""");
var eligible = AutomaticDetectionWanted.Evaluate(wanted.RootElement, 42, 17, 3, "es");
Expect(eligible.Found && eligible.MissingLanguage && eligible.SonarrEpisodeId == 2801,
    "Wanted selection recognizes exact Bleach episode");
Expect(!AutomaticDetectionWanted.Evaluate(wanted.RootElement, 42, 17, 4, "es").Found,
    "Wanted selection rejects unrelated episode");
Expect(!AutomaticDetectionWanted.Evaluate(wanted.RootElement, 43, 17, 3, "es").Found,
    "Wanted selection rejects unrelated Sonarr series");
Expect(!AutomaticDetectionWanted.Evaluate(wanted.RootElement, 42, 17, 3, "en").MissingLanguage,
    "Wanted selection does not infer a missing language");
using var hiWanted = System.Text.Json.JsonDocument.Parse("""
{"data":[{"sonarrSeriesId":42,"sonarrEpisodeId":2801,"episode_number":"17x3",
"missing_subtitles":[{"code2":"es","forced":false,"hi":true}]}]}
""");
Expect(!AutomaticDetectionWanted.Evaluate(hiWanted.RootElement, 42, 17, 3, "es").MissingLanguage,
    "HI-only subtitling must not be treated as normal subtitling");
using var missing = System.Text.Json.JsonDocument.Parse("""{"data":[]}""");
Expect(!AutomaticDetectionWanted.Evaluate(missing.RootElement, 42, 17, 3, "es").Found,
    "Missing wanted row does not dispatch anything");
Console.WriteLine("Automatic detection read-only wanted checks: passed.");
