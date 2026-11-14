// Checks for the Games section's logic: run with `node tests/web/library-checks.mjs`.
import assert from 'node:assert/strict';
import { check, finish, loadModules } from './check.mjs';

const [{ filterGames, gamesToShow, FIRST_GAMES }] = await loadModules('library.js');

const names = ['Lantern Bay', 'Deep Current', 'Kelp Forest Racing', 'Tags & <b>Brackets</b>', 'Driftwood Kart', 'ÉCLAT'];
const games = names.map((name, index) => ({ id: 1000 + index, name, hasArt: true }));
const library = Array.from({ length: 30 }, (_, index) => ({ id: index, name: `Game ${index + 1}`, hasArt: false }));
const found = (typed) => filterGames(games, typed).map((game) => game.name);

check('nothing typed shows every game in the order the PC sent', () => {
  for (const empty of ['', '   ', null, undefined]) assert.deepEqual(filterGames(games, empty), games);
});

check('the filter ignores upper and lower case and spaces around the text', () => {
  assert.deepEqual(found('kart'), ['Driftwood Kart']);
  assert.deepEqual(found('  KART '), ['Driftwood Kart']);
  assert.deepEqual(found('éclat'), ['ÉCLAT']);
  assert.deepEqual(found('r'), ['Lantern Bay', 'Deep Current', 'Kelp Forest Racing', 'Tags & <b>Brackets</b>', 'Driftwood Kart']);
  assert.deepEqual(found('zzz'), []);
});

check('typed text is matched as plain text, not as a pattern or markup', () => {
  assert.deepEqual(found('<b>'), ['Tags & <b>Brackets</b>']);
  assert.deepEqual(found('.*'), []);
  assert.deepEqual(found('&'), ['Tags & <b>Brackets</b>']);
});

check('a long library starts with the games played last', () => {
  assert.equal(gamesToShow(library, '', false).length, FIRST_GAMES);
  assert.deepEqual(gamesToShow(library, '', false), library.slice(0, FIRST_GAMES));
  assert.equal(gamesToShow(library, '', true).length, 30);
});

check('a short library is shown whole', () => {
  assert.deepEqual(gamesToShow(games, '', false), games);
  assert.deepEqual(gamesToShow([], '', false), []);
});

check('typing searches the whole library, not only the first games', () => {
  assert.deepEqual(gamesToShow(library, 'game 29', false).map((game) => game.name), ['Game 29']);
  // "Game 2" matches 2 and 20 to 29: all eleven are shown although only twelve were on screen.
  assert.equal(gamesToShow(library, 'Game 2', false).length, 11);
  assert.equal(gamesToShow(library, 'game', false).length, 30);
});

finish('games');
