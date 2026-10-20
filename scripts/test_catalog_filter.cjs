// Exercise the ACTUAL search helpers embedded in Jellyfin's configuration page.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const html = fs.readFileSync('Jellyfin.Plugin.SubtitleBridge/Configuration/configPage.html','utf8');
const start = html.indexOf('    function normalizeLibraryText(');
const end = html.indexOf('    async function loadLibrary(service)',start);
assert(start >= 0 && end > start, 'Expected library search functions in page');

let onInput = null;
const search = {
  value: '',
  addEventListener(type, callback) {
    assert.equal(type,'input');
    onInput = callback;
  }
};
const count = {textContent: ''};
const entries = ['Bleach', 'Película Árbol', 'One Piece'].map((name, index) => ({
  element: {style: {display: ''}},
  searchable: name.toLowerCase() + ' ' + (index+1),
}));
const context = {
  libraryItems: entries,
  libraryService: 'sonarr',
  q(selector) {
    if(selector === '#msbLibrarySearch') return search;
    if(selector === '#msbLibraryCount') return count;
    throw new Error('Unexpected DOM selector ' + selector);
  },
};
vm.runInNewContext(html.slice(start,end),context);
assert.equal(typeof onInput,'function');
function expect(query, visible, n) {
  search.value=query;
  onInput();
  assert.deepEqual(entries.map(x=>x.element.style.display === ''), visible);
  assert.match(count.textContent,new RegExp('Mostrando '+n+' de 3'));
}
expect('',[true,true,true],3);
expect('BLEACH',[true,false,false],1);
expect('ARbOL',[false,true,false],1);
expect('one piec',[false,false,true],1);
expect('película árbol',[false,true,false],1);
expect('   noexiste  ',[false,false,false],0);
assert.match(count.textContent,/Ninguna coincide/);
console.log('Jellyfin catalog search filter: checks passed');
