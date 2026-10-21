// Regression: the event-triggered single episode and diagnostic must share
// the validated Bazarr /api/episodes lookup with the manual button.
// A missing-subtitle episode may be excluded from Bazarr /episodes/wanted.
const fs = require('node:fs');
const assert = require('node:assert/strict');

const dispatcher = fs.readFileSync('Jellyfin.Plugin.SubtitleBridge/StreamingScanDispatcher.cs','utf8');
const probe = fs.readFileSync('Jellyfin.Plugin.SubtitleBridge/AutomaticDetectionProbe.cs','utf8');
const listener = fs.readFileSync('Jellyfin.Plugin.SubtitleBridge/NewEpisodeListener.cs','utf8');

const scanner = dispatcher.slice(
  dispatcher.indexOf('public static async Task<ScanSummary> ScanAsync('),
  dispatcher.indexOf('public static async Task<SingleEpisodeSummary> ScanSingleAsync(')
);
const i = scanner.indexOf('if (onlySeriesId.HasValue || onlySeason.HasValue || onlyEpisode.HasValue)');
const lookup = scanner.indexOf('ScanSingleAsync(onlySeriesId.Value');
const globalWanted = scanner.indexOf('/api/episodes/wanted?start=');
assert(i >= 0 && lookup > i && globalWanted > lookup,
       'Exact scoped scan must dispatch through ScanSingleAsync BEFORE bulk wanted query');
assert.match(scanner.slice(i,globalWanted), /!matches\[0\]\.Automatic/);
assert.match(scanner.slice(i,globalWanted), /matches\[0\]\.Source, "disney"/);
assert.match(scanner.slice(i,globalWanted), /new ScanSummary\(1, single\.Queued, single\.Message\)/);
assert.match(dispatcher.slice(dispatcher.indexOf('ScanSingleAsync('),dispatcher.length),
  /\/api\/episodes\?seriesid%5B%5D=/);
assert.match(probe, /SingleEpisodePlanner\.Plan\(document\.RootElement/);
assert.match(probe, /\/api\/episodes\?seriesid%5B%5D=/);
assert.doesNotMatch(probe, /\/api\/episodes\/wanted\?/);
assert.match(listener, /StreamingScanDispatcher\.ScanAsync\(\s*ct,\s*tracked\.Id,\s*episode\.ParentIndexNumber\.Value,\s*episode\.IndexNumber\.Value\)/);
console.log('Auto detection: scoped matching, read-only probe and manual lookup align.');
