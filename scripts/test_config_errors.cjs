// Exercise the real Jellyfin configuration page's failed-request parser.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const html = fs.readFileSync('Jellyfin.Plugin.SubtitleBridge/Configuration/configPage.html','utf8');
const start = html.indexOf('    function errorText(');
const end = html.indexOf('    async function saveConfig(', start);
assert(start >= 0 && end > start);
const scope = {};
vm.runInNewContext(html.slice(start, end) + '\nthis.parseError = errorText;', scope);
const get = scope.parseError;
assert.equal(get({responseJSON: {error:'No existe mapeo',code:'unmapped_episode'}}, 'fallo'),
    'No existe mapeo (unmapped_episode)');
assert.equal(get({status:409,responseText:'{"error":"La serie no está configurada","code":"untracked_series"}'}, 'fallo'),
    'La serie no está configurada (untracked_series)');
assert.equal(get({status:502,response:{data:{error:'El agente no responde',code:'agent_unreachable'}}}, 'fallo'),
    'El agente no responde (agent_unreachable)');
assert.equal(get({status:409, responseText:'<html>secret abc</html>'},'Solicitud fallida'),
    'Solicitud fallida (HTTP 409).');
assert.equal(get({status:502, responseJSON: {error:'x'.repeat(301)}}, 'Solicitud fallida'),
    'Solicitud fallida (HTTP 502).');
assert.equal(get({},'Solicitud fallida'), 'Solicitud fallida');
console.log('Moonfin API diagnostic handling: checks passed.');
