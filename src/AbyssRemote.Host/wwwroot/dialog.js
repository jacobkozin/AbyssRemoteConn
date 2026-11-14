// "Are you sure?" in the app's own look, for things that reach the PC: starting a game,
// locking it, deleting a file.
import { $ } from './dom.js';

/** Asks one question. Resolves true only when the user picks the action; Cancel, Esc and a
 *  tap outside the box all mean no. `danger` paints the action red (deleting, signing out). */
export function confirmAction({ title, message, action, danger = false }) {
  const dialog = $('confirmDialog');
  // Browsers without <dialog> (Safari before 15.4) still get a working question.
  if (typeof dialog.showModal !== 'function') return Promise.resolve(window.confirm(`${title}\n\n${message}`));

  $('confirmTitle').textContent = title;
  $('confirmMessage').textContent = message;
  $('confirmYes').textContent = action;
  $('confirmYes').classList.toggle('danger', danger);

  return new Promise((resolve) => {
    // The answer resolves at once, inside the tap itself. A caller that goes on to start
    // sound or full screen needs the browser to still count that as the user's own action.
    const answer = (value) => {
      $('confirmYes').removeEventListener('click', yes);
      $('confirmNo').removeEventListener('click', no);
      dialog.removeEventListener('click', outside);
      dialog.removeEventListener('close', no);
      if (dialog.open) dialog.close();
      resolve(value);
    };
    const yes = () => answer(true);
    const no = () => answer(false);
    const outside = (event) => { if (event.target === dialog) no(); };
    $('confirmYes').addEventListener('click', yes);
    $('confirmNo').addEventListener('click', no);
    dialog.addEventListener('click', outside);
    dialog.addEventListener('close', no); // Esc closes the dialog without a click
    dialog.showModal();
    $('confirmNo').focus(); // the safe answer is the one Enter picks
  });
}
