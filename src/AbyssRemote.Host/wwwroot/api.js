// Talking to the PC over HTTPS. Every screen uses these helpers, so the rule "anything that
// changes something carries the CSRF header" lives in one place (see docs/SECURITY.md).

/** Who this browser is to the PC. Filled by refreshIdentity(). */
export const session = { csrf: null, id: null, device: '', features: {} };

function send(path, options) {
  const headers = { ...(options.headers || {}) };
  if (options.body !== undefined) headers['Content-Type'] = 'application/json';
  if (session.csrf && changesSomething(options)) headers['X-Abyss-CSRF'] = session.csrf;
  return fetch(path, { credentials: 'same-origin', cache: 'no-store', ...options, headers });
}

const changesSomething = (options) => Boolean(options.method) && options.method !== 'GET';

function failure(status, message) {
  const error = new Error(message || `The PC answered ${status}.`);
  error.status = status;
  return error;
}

/** One JSON request. An error answer becomes an Error with the PC's own sentence and `status`. */
export async function api(path, options = {}) {
  let response = await send(path, options);
  // The CSRF token changes when Abyss Remote restarts on the PC. A refused change did nothing,
  // so it is safe to fetch the new token and ask once more.
  if (response.status === 403 && session.csrf && changesSomething(options)) {
    await refreshIdentity();
    response = await send(path, options);
  }
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw failure(response.status, data.error);
  return data;
}

/** Loads this device's id, name, CSRF token and what the PC can do. Throws with status 401
 *  when this browser is not paired (any more). */
export async function refreshIdentity() {
  const me = await api('/api/v1/me');
  session.csrf = me.csrf;
  session.id = me.id;
  session.device = me.device || '';
  session.features = me.features || {};
}

/** A plain GET that is not JSON (a picture, a screenshot). Returns the Response when it is
 *  fine and throws like api() when it is not. */
export async function fetchFile(path) {
  const response = await fetch(path, { credentials: 'same-origin', cache: 'no-store' });
  if (response.ok) return response;
  const data = await response.json().catch(() => ({}));
  throw failure(response.status, data.error);
}

/** Sends one file to the PC's transfer folder. fetch() cannot report upload progress, so this
 *  uses XMLHttpRequest. Returns { done, cancel }: `done` resolves with the PC's answer
 *  ({ name, size }, where name is the final name on the PC). */
export function uploadFile(file, name, onProgress) {
  const request = new XMLHttpRequest();
  const done = new Promise((resolve, reject) => {
    request.open('POST', `/api/v1/files?name=${encodeURIComponent(name)}`);
    request.setRequestHeader('X-Abyss-CSRF', session.csrf || '');
    request.upload.onprogress = (event) => { if (event.lengthComputable) onProgress(event.loaded, event.total); };
    request.onload = () => {
      let data = {};
      try { data = JSON.parse(request.responseText); } catch { /* an empty or non-JSON answer */ }
      if (request.status >= 200 && request.status < 300) resolve(data);
      else reject(failure(request.status, data.error));
    };
    request.onerror = () => reject(failure(0, 'The upload stopped before the whole file arrived.'));
    request.onabort = () => reject(failure(0, 'Cancelled.'));
    request.send(file);
  });
  return { done, cancel: () => request.abort() };
}
