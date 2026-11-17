// Live typing: what the phone keyboard puts in the typing box goes straight to the PC.
// The box is compared with what it held before, and only the difference is sent: Backspace
// for what went away after the unchanged beginning, then the new characters. That one rule
// covers plain typing, autocorrect, swipe typing, dictation and paste. Nothing here touches
// the page, so tests/web/typing-checks.mjs runs it in Node.

// The box starts with a few invisible characters (zero-width spaces). A phone keyboard does
// nothing when Backspace is pressed in an empty box; with these it removes one, and that is
// how a Backspace meant for the PC is noticed.
const INVISIBLE = '​';
export const TYPING_GUARD = INVISIBLE.repeat(4);

const visible = (text) => Array.from(text).filter((character) => character !== INVISIBLE);
const guards = (text) => Array.from(text).length - visible(text).length;

/** The text of the box without its invisible beginning. */
export function typedText(box) { return visible(box).join(''); }

/** What to do on the PC after the box changed from `before` to `after`: how many times to
 *  press Backspace, then which text to type. Characters are compared whole, so an emoji
 *  (two UTF-16 units) counts as one press. */
export function typingChange(before, after) {
  const was = visible(before), now = visible(after);
  let same = 0;
  while (same < was.length && same < now.length && was[same] === now[same]) same++;
  let backspaces = was.length - same;
  const text = now.slice(same).join('');
  // Nothing visible changed, but an invisible character is gone: Backspace in an empty box.
  if (!backspaces && !text) backspaces = Math.max(0, guards(before) - guards(after));
  return { backspaces, text };
}

/** A typed letter or digit as the key it sits on, so a held Ctrl, Alt, Shift or Windows key
 *  makes a shortcut of it (Ctrl+C). Null for everything else, which is sent as text. */
export function keyCodeFor(character) {
  if (/^[a-z]$/i.test(character)) return `Key${character.toUpperCase()}`;
  if (/^[0-9]$/.test(character)) return `Digit${character}`;
  return null;
}
