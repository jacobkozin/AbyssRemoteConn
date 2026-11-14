// The Files section of the home screen: send files to the PC's "Abyss Remote" folder in
// Downloads, and download or delete what is in it.
import { $, el, icon } from './dom.js';
import { api, refreshIdentity, uploadFile } from './api.js';
import { confirmAction } from './dialog.js';
import { sizeText, dateText } from './format.js';

/** The longest file name the PC accepts (TransferName.MaxLength on the host). */
export const MAX_NAME = 150;

// What the PC refuses in a name: the characters Windows forbids, control characters (Cc),
// Unicode line breaks (Zl, Zp), characters that switch the reading direction, and half of a
// two-part character (which cannot even be put into an address).
const UNSAFE = /[\\/:*?"<>|\p{Cc}\p{Zl}\p{Zp}\p{Bidi_Control}\p{Surrogate}]/gu;
// What a character turns into when it could not be read. The PC takes it as a damaged name.
const DAMAGED = String.fromCodePoint(0xFFFD);
// Names that are devices in every Windows folder, with or without an extension.
const DEVICE = /^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|(COM|LPT)[0-9¹²³])$/;
const PARTIAL_PREFIX = '.abyss-upload-'; // the PC keeps this prefix for uploads that are still arriving

const trimEnds = (text) => text.replace(/^\s+/, '').replace(/[\s.]+$/, '');

/** Turns any file name into one the PC accepts. The PC refuses unsafe names instead of
 *  repairing them, so a phone photo called "IMG:1?.jpg" has to be tidied here first. */
export function tidyFileName(name) {
  let tidy = trimEnds(String(name ?? '').replace(UNSAFE, '_').replaceAll(DAMAGED, '_'));
  const stem = tidy.split('.')[0].replace(/ +$/, '').toUpperCase();
  if (DEVICE.test(stem) || tidy.toLowerCase().startsWith(PARTIAL_PREFIX)) tidy = `_${tidy}`;
  if (tidy.length > MAX_NAME) tidy = trimEnds(shorten(tidy));
  return tidy || 'file';
}

/** Cuts a name to the allowed length. A normal extension is kept so the file still opens, and
 *  a two-part character (an emoji) is never cut in half. */
function shorten(name) {
  const dot = name.lastIndexOf('.');
  const extension = dot > 0 && name.length - dot <= 16 ? name.slice(dot) : '';
  let stem = name.slice(0, name.length - extension.length).slice(0, MAX_NAME - extension.length);
  if (/[\uD800-\uDBFF]$/.test(stem)) stem = stem.slice(0, -1);
  return stem + extension;
}

let hooks = null;
let maxUploadBytes = Infinity;
let sending = false;
const waiting = []; // uploads that have not started yet; they go one at a time

const say = (message) => { $('filesStatus').textContent = message; };

/** `hooks`: unpaired(error) (see home.js). */
export function initFiles(given) {
  hooks = given;
  $('sendFiles').addEventListener('click', () => $('filePicker').click());
  $('filePicker').addEventListener('change', () => {
    queue($('filePicker').files);
    $('filePicker').value = ''; // so choosing the same file again is noticed
  });

  // Computers: files can be dropped anywhere on the section.
  const panel = $('filesPanel');
  const carriesFiles = (event) => [...(event.dataTransfer?.types || [])].includes('Files');
  panel.addEventListener('dragover', (event) => { if (carriesFiles(event)) panel.classList.add('dropping'); });
  panel.addEventListener('dragleave', (event) => { if (!panel.contains(event.relatedTarget)) panel.classList.remove('dropping'); });
  panel.addEventListener('drop', (event) => {
    panel.classList.remove('dropping');
    if (carriesFiles(event)) queue(event.dataTransfer.files);
  });
  // Without this, a dropped file replaces the whole page with that file, also when it lands
  // beside the section.
  for (const type of ['dragover', 'drop'])
    window.addEventListener(type, (event) => { if (carriesFiles(event)) event.preventDefault(); });
}

/** Lists what is in the folder now, with the folder's path as the PC reports it. */
export async function loadFiles() {
  try {
    const answer = await api('/api/v1/files');
    maxUploadBytes = answer.maxUploadBytes > 0 ? answer.maxUploadBytes : Infinity;
    const limit = Number.isFinite(maxUploadBytes) ? ` Up to ${sizeText(maxUploadBytes)} per file.` : '';
    $('filesFolder').textContent = `On the PC they are in ${answer.folder}.${limit}`;
    $('fileList').replaceChildren(...answer.files.map(fileRow));
    say(answer.files.length ? '' : 'The folder is empty.');
  } catch (error) {
    if (!hooks.unpaired(error)) say(error.message);
  }
}

// ---------------------------------------------------------------- sending

function queue(files) {
  for (const file of files) {
    const upload = { file, name: tidyFileName(file.name), state: 'waiting', cancel: null };
    addUploadRow(upload);
    // Checked here because a browser that is still sending may show "network error" instead
    // of the PC's "too large" answer.
    if (file.size > maxUploadBytes) finish(upload, 'failed', `That file is too large. The limit is ${sizeText(maxUploadBytes)}.`);
    else waiting.push(upload);
  }
  sendWaiting();
}

async function sendWaiting() {
  if (sending || waiting.length === 0) return;
  sending = true;
  try {
    await refreshIdentity(); // the token changes when Abyss Remote restarts on the PC
    while (waiting.length) await sendOne(waiting.shift());
  } catch (error) {
    const reason = error.status ? error.message : 'The PC is not answering.';
    if (!hooks.unpaired(error)) for (const upload of waiting.splice(0)) finish(upload, 'failed', reason);
  } finally {
    sending = false;
  }
  loadFiles();
}

async function sendOne(upload) {
  upload.state = 'sending';
  const size = sizeText(upload.file.size);
  const transfer = uploadFile(upload.file, upload.name, (sent, total) => {
    upload.bar.value = total ? sent / total : 0;
    upload.detail.textContent = `${size} · Sending ${Math.floor(100 * sent / Math.max(1, total))}%`;
  });
  upload.cancel = transfer.cancel;
  try {
    const saved = await transfer.done;
    // The PC never overwrites: a name that was taken comes back as "name (2).jpg".
    finish(upload, 'done', saved.name && saved.name !== upload.name ? `Saved on the PC as ${saved.name}` : 'Saved on the PC');
  } catch (error) {
    if (!hooks.unpaired(error)) finish(upload, 'failed', error.message);
  }
}

function addUploadRow(upload) {
  upload.row = el('li', 'row');
  upload.detail = el('small', '', `${sizeText(upload.file.size)} · Waiting`);
  upload.bar = el('progress');
  upload.bar.max = 1;
  upload.bar.value = 0;
  const text = el('div', 'row-text');
  text.append(el('strong', '', upload.name), upload.detail, upload.bar);
  upload.button = el('button', 'icon-button');
  upload.button.type = 'button';
  upload.button.setAttribute('aria-label', `Cancel sending ${upload.name}`);
  upload.button.append(icon('close'));
  upload.button.addEventListener('click', () => dismiss(upload));
  upload.row.append(text, upload.button);
  $('uploadList').append(upload.row);
}

function finish(upload, state, message) {
  upload.state = state;
  upload.row.classList.toggle('failed', state === 'failed');
  upload.bar.remove();
  upload.detail.textContent = `${sizeText(upload.file.size)} · ${message}`;
  upload.button.setAttribute('aria-label', `Remove ${upload.name} from this list`);
}

/** The X on an upload: stops it while it is waiting or on its way, clears it once it is over. */
function dismiss(upload) {
  if (upload.state === 'sending') { upload.cancel(); return; }
  if (upload.state === 'waiting') waiting.splice(waiting.indexOf(upload), 1);
  upload.row.remove();
}

// ---------------------------------------------------------------- the folder's files

function fileRow(file) {
  const row = el('li', 'row');
  const text = el('div', 'row-text');
  text.append(el('strong', '', file.name), el('small', '', `${sizeText(file.size)} · ${dateText(file.modifiedUtc)}`));
  row.append(text);

  let address;
  try { address = `/api/v1/files/${encodeURIComponent(file.name)}`; }
  catch { return row; } // a damaged name Windows allows but an address cannot carry: listed, no actions

  const download = el('a', 'icon-button');
  download.href = address;
  download.download = file.name;
  download.setAttribute('aria-label', `Download ${file.name}`);
  download.append(icon('download'));
  const remove = el('button', 'icon-button danger');
  remove.type = 'button';
  remove.setAttribute('aria-label', `Delete ${file.name}`);
  remove.append(icon('trash'));
  remove.addEventListener('click', () => deleteFile(file, address));
  row.append(download, remove);
  return row;
}

async function deleteFile(file, address) {
  // The PC deletes for good (not to the Recycle Bin), so this always asks first.
  const yes = await confirmAction({ title: `Delete ${file.name} from the PC?`, message: 'This cannot be undone.', action: 'Delete', danger: true });
  if (!yes) return;
  try {
    await api(address, { method: 'DELETE' });
    await loadFiles();
  } catch (error) {
    if (hooks.unpaired(error)) return;
    await loadFiles();
    say(error.status === 404 ? 'That file is no longer in the folder.' : error.message);
  }
}
