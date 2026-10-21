// Read-only regression guard: the detection UI must never submit to an agent.
const assert = require('node:assert/strict');
const fs = require('node:fs');

const html = fs.readFileSync('Jellyfin.Plugin.SubtitleBridge/Configuration/configPage.html','utf8');
const controller = fs.readFileSync('Jellyfin.Plugin.SubtitleBridge/StreamingController.cs','utf8');
const service = fs.readFileSync('Jellyfin.Plugin.SubtitleBridge/AutomaticDetectionProbe.cs','utf8');
const begin = html.indexOf("q('#msbDetectionTest').addEventListener('click'");
const end = html.indexOf("q('#msbSingleSubmit').addEventListener('click'",begin);
assert(begin >= 0 && end > begin, 'Expected detection-check handler');
const handler = html.slice(begin,end);
assert.match(handler, /api\('streaming\/detection-test\?/);
assert.doesNotMatch(handler, /apiPost\s*\(/);
assert.doesNotMatch(handler, /confirm\s*\(/);
assert.match(html, /id="msbDetectionResult"/);
assert.match(controller, /\[HttpGet\("detection-test"\)\]/);
const probe = service.slice(service.indexOf('public static async Task<Report>'),service.length);
assert.doesNotMatch(probe, /HttpMethod\.Post/);
assert.doesNotMatch(probe, /HttpMethod\.Patch/);
assert.doesNotMatch(probe, /ScanAsync\s*\(/);
assert.doesNotMatch(probe, /ScanSingleAsync\s*\(/);
assert.doesNotMatch(probe, /\/v1\/scan/);
console.log('Moonfin automatic-detection dry-run guards: passed.');
