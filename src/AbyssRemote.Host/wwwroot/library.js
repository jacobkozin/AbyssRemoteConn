// The Games section of the home screen: the PC's installed Steam games as a grid of covers.
// Tapping one starts it on the PC and then opens the stream on the main monitor.
import { $, el } from './dom.js';
import { api } from './api.js';
import { confirmAction } from './dialog.js';

/** A library with more games than this gets a filter box and starts with only this many. */
export const FIRST_GAMES = 12;

/** The games whose name contains what was typed, ignoring upper and lower case and spaces
 *  around it. Nothing typed means every game, in the PC's order (last played first). */
export function filterGames(games, typed) {
  const wanted = String(typed ?? '').trim().toLowerCase();
  return wanted ? games.filter((game) => String(game.name).toLowerCase().includes(wanted)) : games;
}

/** The games to draw. A long library would push everything below it off a phone's screen,
 *  so it starts with the games played last until "Show all" is tapped. Typing in the filter
 *  always searches the whole library. */
export function gamesToShow(games, typed, showAll) {
  const matching = filterGames(games, typed);
  return showAll || matching !== games ? matching : matching.slice(0, FIRST_GAMES);
}

let hooks = null;
let games = [];
let showAll = false;

const say = (message) => { $('gamesStatus').textContent = message; };

/** `hooks`: openStream(what), prepareSound(), unpaired(error) (see home.js). */
export function initLibrary(given) {
  hooks = given;
  $('gameFilter').addEventListener('input', draw);
  $('showAllGames').addEventListener('click', () => { showAll = true; draw(); });
  $('bigPicture').addEventListener('click', openBigPicture);
  $('bigPictureClose').addEventListener('click', closeBigPicture);
}

/** Asks the PC for its games. Steam's list is read fresh each time, so this runs whenever the
 *  home screen is shown. The whole section stays hidden on a PC without Steam. */
export async function loadLibrary() {
  try {
    const steam = await api('/api/v1/steam');
    games = steam.games || [];
    showAll = false;
    $('gamesPanel').classList.toggle('hidden', !steam.installed);
    $('gameFilterLabel').classList.toggle('hidden', games.length <= FIRST_GAMES);
    if (games.length <= FIRST_GAMES) $('gameFilter').value = '';
    draw();
  } catch (error) {
    if (!hooks.unpaired(error)) say(error.message);
  }
}

function draw() {
  const typed = $('gameFilter').value;
  const shown = gamesToShow(games, typed, showAll);
  $('gameGrid').replaceChildren(...shown.map(tile));
  $('showAllGames').textContent = `Show all ${games.length} games`;
  $('showAllGames').classList.toggle('hidden', shown.length === filterGames(games, typed).length);
  say(games.length === 0 ? 'Steam has no installed games on this PC.' : shown.length === 0 ? 'No game has that in its name.' : '');
}

function tile(game) {
  const item = el('li');
  const button = el('button', 'game-tile');
  button.type = 'button';
  const cover = el('span', 'game-cover');
  if (game.hasArt) {
    const picture = el('img');
    picture.alt = '';
    picture.loading = 'lazy';
    picture.decoding = 'async';
    // A cover Steam removed in the meantime, or one the browser cannot show: use the name instead.
    picture.addEventListener('error', () => cover.replaceChildren(nameTile(game)), { once: true });
    picture.src = `/api/v1/steam/games/${game.id}/art`;
    cover.append(picture);
  } else {
    cover.append(nameTile(game));
  }
  button.append(cover, el('span', 'game-name', game.name));
  button.addEventListener('click', () => start(game));
  item.append(button);
  return item;
}

/** Stands in for a missing cover. The name is already read out below it, so this copy is
 *  hidden from screen readers. */
function nameTile(game) {
  const text = el('span', 'game-letters', game.name);
  text.setAttribute('aria-hidden', 'true');
  return text;
}

async function start(game) {
  const yes = await confirmAction({
    title: `Start ${game.name} on the PC?`,
    message: 'Steam may show a window of its own first. You will see it here and can answer it.',
    action: 'Start',
  });
  if (!yes) return;
  await openAfter(`Starting ${game.name}…`, () => api(`/api/v1/steam/games/${game.id}/launch`, { method: 'POST' }));
}

async function openBigPicture() {
  const yes = await confirmAction({
    title: 'Open Steam Big Picture on the PC?',
    message: 'Steam switches to its full-screen view on the PC, and this device shows the main monitor.',
    action: 'Open',
  });
  if (!yes) return;
  await openAfter('Opening Big Picture…', () => api('/api/v1/steam/bigpicture', { method: 'POST' }));
}

/** Runs a Steam request and, when the PC accepted it, opens the main monitor in Game mode
 *  with sound (the stream falls back to Desktop mode by itself when Game mode is missing). */
async function openAfter(message, request) {
  hooks.prepareSound(); // still inside the tap, which is the only moment a browser lets sound start
  say(message);
  try {
    await request();
    say('');
    hooks.openStream({ display: 'main', mode: 'game', sound: true });
  } catch (error) {
    if (!hooks.unpaired(error)) say(error.message);
  }
}

async function closeBigPicture() {
  try {
    await api('/api/v1/steam/bigpicture/close', { method: 'POST' });
    say('Big Picture is closed on the PC.');
  } catch (error) {
    if (!hooks.unpaired(error)) say(error.message);
  }
}
