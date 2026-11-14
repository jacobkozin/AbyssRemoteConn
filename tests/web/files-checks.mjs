// Checks for the Files section's logic and the shared text helpers:
// run with `node tests/web/files-checks.mjs`.
import assert from 'node:assert/strict';
import { check, finish, loadModules } from './check.mjs';

const [{ tidyFileName, MAX_NAME }, { sizeText, dateText, fileNameFromDisposition }] = await loadModules('files.js', 'format.js');
const char = String.fromCodePoint;

// The PC's own rule (TransferName.IsSafe in the host), written out again here so the checks
// can prove that whatever tidyFileName returns will be accepted there.
function acceptedByPc(name) {
  if (name.length === 0 || name.length > 150) return false;
  if (/^\s|\s$/.test(name) || name.endsWith('.')) return false;
  for (const character of name) {
    const code = character.codePointAt(0);
    if ('\\/:*?"<>|'.includes(character)) return false;
    if (code <= 0x1F || (code >= 0x7F && code <= 0x9F)) return false;              // control characters
    if (code === 0x2028 || code === 0x2029) return false;                           // Unicode line breaks
    if ((code >= 0x202A && code <= 0x202E) || (code >= 0x2066 && code <= 0x2069)) return false; // direction switches
    if (code === 0xFFFD || (code >= 0xD800 && code <= 0xDFFF)) return false;        // damaged, or half a pair
  }
  const stem = name.split('.')[0].replace(/ +$/, '').toUpperCase();
  if (['CON', 'PRN', 'AUX', 'NUL', 'CONIN$', 'CONOUT$'].includes(stem)) return false;
  if (stem.length === 4 && ['COM', 'LPT'].includes(stem.slice(0, 3)) && /[0-9¹²³]/.test(stem[3])) return false;
  return !name.toLowerCase().startsWith('.abyss-upload-');
}

const awkward = [
  'IMG:1?.jpg', 'a/b\\c.txt', '<tag>|pipe*.txt', '"quoted".txt', '  spaced  .txt  ', 'ends with dot.', 'ends with dots...',
  '...', '', '   ', '.', '..', ' . ',
  'con', 'CON.txt', 'nul.tar.gz', 'com1', 'LPT9.log', `com${char(0xB9)}`, 'aux .txt', 'CONIN$', 'conout$.txt', 'COM0', 'prn.',
  '.abyss-upload-123', '.ABYSS-UPLOAD-x.tmp',
  `line${char(0x0A)}break.txt`, `tab${char(9)}.txt`, `zero${char(0)}char`, `del${char(0x7F)}${char(0x85)}.txt`,
  `sep${char(0x2028)}${char(0x2029)}.txt`, `evil${char(0x202E)}fdp.exe`, `iso${char(0x2066)}late${char(0x2069)}.txt`,
  `broken${char(0xFFFD)}.txt`, `half${char(0xD83D)}.txt`, `${char(0xDC00)}start.txt`,
  'x'.repeat(300), `${'y'.repeat(200)}.jpeg`, `${'z'.repeat(149)}${char(0x1F600)}.png`, char(0x1F600).repeat(100),
  `${'a'.repeat(148)} .txt`, `${'b'.repeat(140)}.${'c'.repeat(40)}`, `${'d'.repeat(149)}.`, `${'e'.repeat(160)}   `,
  `${char(0x3000)}wide space.txt`, `no-break${char(0xA0)}`, `con${' '.repeat(200)}.txt`,
];

check('every tidied name is one the PC accepts', () => {
  for (const name of awkward) {
    const tidy = tidyFileName(name);
    assert.ok(acceptedByPc(tidy), `${JSON.stringify(name)} became ${JSON.stringify(tidy)}`);
  }
});

check('names that are already fine are left alone', () => {
  const fine = ['Holiday photo.jpg', '.gitignore', 'archive.tar.gz', 'Résumé 📄.pdf', '日本語のファイル.txt', 'a'.repeat(MAX_NAME), 'console.log', 'common.txt', 'COM10.txt'];
  for (const name of fine) {
    assert.ok(acceptedByPc(name), name);
    assert.equal(tidyFileName(name), name);
  }
});

check('forbidden characters become underscores and the ends are trimmed', () => {
  assert.equal(tidyFileName('IMG:1?.jpg'), 'IMG_1_.jpg');
  assert.equal(tidyFileName('a/b\\c.txt'), 'a_b_c.txt');
  assert.equal(tidyFileName('  spaced  .txt  '), 'spaced  .txt');
  assert.equal(tidyFileName('ends with dots...'), 'ends with dots');
  assert.equal(tidyFileName(`evil${char(0x202E)}fdp.exe`), 'evil_fdp.exe');
});

check('a name with nothing usable in it becomes "file"', () => {
  for (const name of ['', '   ', '.', '...', ' . ', null, undefined]) assert.equal(tidyFileName(name), 'file');
});

check('Windows device names and the upload prefix get a leading underscore', () => {
  assert.equal(tidyFileName('con'), '_con');
  assert.equal(tidyFileName('NUL.txt'), '_NUL.txt');
  assert.equal(tidyFileName('com1'), '_com1');
  assert.equal(tidyFileName('.abyss-upload-123'), '_.abyss-upload-123');
});

check('a long name is cut to 150 and keeps its extension', () => {
  const photo = tidyFileName(`${'y'.repeat(200)}.jpeg`);
  assert.equal(photo.length, MAX_NAME);
  assert.ok(photo.endsWith('.jpeg'));
  assert.equal(tidyFileName('x'.repeat(300)).length, MAX_NAME);
  // The cut would land in the middle of the emoji, so the whole emoji goes.
  assert.equal(tidyFileName(`${'z'.repeat(145)}${char(0x1F600)}.png`), `${'z'.repeat(145)}.png`);
});

check('sizes read like Windows shows them', () => {
  const cases = [[0, '0 bytes'], [1, '1 byte'], [1023, '1023 bytes'], [1024, '1 KB'], [1536, '1.5 KB'], [307200, '300 KB'],
    [1288490, '1.2 MB'], [5 * 1024 * 1024, '5 MB'], [2147483648, '2 GB'], [12345678901, '11.5 GB'], [3 * 1024 ** 4, '3 TB'], [5000 * 1024 ** 4, '5000 TB']];
  for (const [bytes, text] of cases) assert.equal(sizeText(bytes), text, String(bytes));
  for (const bad of [-1, NaN, Infinity, 'abc', undefined]) assert.equal(sizeText(bad), '', String(bad));
});

check('dates say Today and Yesterday by the calendar, not by 24 hours', () => {
  const now = new Date(2026, 10, 14, 12, 0);
  const iso = (...parts) => new Date(...parts).toISOString();
  assert.match(dateText(iso(2026, 10, 14, 9, 5), now, 'en-US'), /^Today, 9:05\sAM$/);
  assert.match(dateText(iso(2026, 10, 13, 23, 59), now, 'en-US'), /^Yesterday, 11:59\sPM$/);
  assert.equal(dateText(iso(2026, 10, 11, 8, 0), now, 'en-US'), 'Nov 11, 2026');
  assert.equal(dateText(iso(2025, 11, 31, 8, 0), now, 'en-US'), 'Dec 31, 2025');
  assert.equal(dateText('not a date', now, 'en-US'), '');
  assert.equal(dateText(undefined, now, 'en-US'), '');
});

check('a download name is read from either spelling of the header', () => {
  const both = 'attachment; filename="Abyss Remote 2026-11-13 14.05.22.jpg"; filename*=UTF-8\'\'Abyss%20Remote%202026-11-13%2014.05.22.jpg';
  assert.equal(fileNameFromDisposition(both, 'x.jpg'), 'Abyss Remote 2026-11-13 14.05.22.jpg');
  assert.equal(fileNameFromDisposition('attachment; filename="shot one.jpg"', 'x.jpg'), 'shot one.jpg');
  assert.equal(fileNameFromDisposition('attachment; filename=shot.jpg', 'x.jpg'), 'shot.jpg');
  assert.equal(fileNameFromDisposition("attachment; filename*=UTF-8''%E5%86%99%E7%9C%9F.jpg", 'x.jpg'), '写真.jpg');
  assert.equal(fileNameFromDisposition("attachment; filename=\"plain.jpg\"; filename*=UTF-8''%E0%A4%A", 'x.jpg'), 'plain.jpg');
  for (const none of ['attachment', '', null, undefined]) assert.equal(fileNameFromDisposition(none, 'x.jpg'), 'x.jpg');
});

finish('files');
