// Checks for live typing (live-typing.js): run with `node tests/web/typing-checks.mjs`.
import assert from 'node:assert/strict';
import { loadModules, check, finish } from './check.mjs';
const [{ TYPING_GUARD, keyCodeFor, typedText, typingChange }] = await loadModules('live-typing.js');

const box = (text) => TYPING_GUARD + text;

check('each new character is typed, nothing is deleted', () => {
  assert.deepEqual(typingChange(box(''), box('h')), { backspaces: 0, text: 'h' });
  assert.deepEqual(typingChange(box('h'), box('hi')), { backspaces: 0, text: 'i' });
  assert.deepEqual(typingChange(box('hi'), box('hi there')), { backspaces: 0, text: ' there' }); // paste or dictation
});

check('Backspace on the phone is Backspace on the PC', () => {
  assert.deepEqual(typingChange(box('hello'), box('hell')), { backspaces: 1, text: '' });
  assert.deepEqual(typingChange(box('hello'), box('')), { backspaces: 5, text: '' });
});

check('Backspace in a box that looks empty still reaches the PC', () => {
  const oneGone = TYPING_GUARD.slice(1);
  assert.deepEqual(typingChange(TYPING_GUARD, oneGone), { backspaces: 1, text: '' });
  assert.deepEqual(typingChange(TYPING_GUARD, ''), { backspaces: TYPING_GUARD.length, text: '' });
  // The invisible beginning is never typed on the PC and never deleted there with real text.
  assert.equal(typedText(TYPING_GUARD), '');
  assert.deepEqual(typingChange(box('abc'), 'x'), { backspaces: 3, text: 'x' }); // select all, type over
});

check('autocorrect replaces a word with Backspaces and the new ending', () => {
  assert.deepEqual(typingChange(box('teh'), box('the ')), { backspaces: 2, text: 'he ' });
  assert.deepEqual(typingChange(box('i cant'), box("i can't")), { backspaces: 1, text: "'t" });
});

check('an emoji is one character, and Enter comes through as a line break', () => {
  assert.deepEqual(typingChange(box('ok 😀'), box('ok ')), { backspaces: 1, text: '' });
  assert.deepEqual(typingChange(box('ok'), box('ok 😀')), { backspaces: 0, text: ' 😀' });
  assert.deepEqual(typingChange(box('dir'), box('dir\n')), { backspaces: 0, text: '\n' });
});

check('nothing changed means nothing to do', () => {
  assert.deepEqual(typingChange(box('same'), box('same')), { backspaces: 0, text: '' });
});

check('letters and digits can become shortcut keys, other characters cannot', () => {
  assert.equal(keyCodeFor('c'), 'KeyC');
  assert.equal(keyCodeFor('V'), 'KeyV');
  assert.equal(keyCodeFor('7'), 'Digit7');
  assert.equal(keyCodeFor('é'), null);
  assert.equal(keyCodeFor('ab'), null);
  assert.equal(keyCodeFor(' '), null);
});

finish('live typing');
