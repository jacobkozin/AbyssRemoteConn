// Turning numbers, dates and headers into the short texts the screens show. No page access
// here, so tests/web/files-checks.mjs can run these in Node.

const UNITS = ['KB', 'MB', 'GB', 'TB'];

/** A file size the way Windows shows it: "1 byte", "512 bytes", "1.2 MB", "2 GB". */
export function sizeText(bytes) {
  const size = Number(bytes);
  if (!Number.isFinite(size) || size < 0) return '';
  if (size < 1024) return size === 1 ? '1 byte' : `${Math.round(size)} bytes`;
  let value = size, unit = -1;
  while (value >= 1024 && unit < UNITS.length - 1) { value /= 1024; unit++; }
  // One decimal for small numbers ("1.2 MB"), none once it stops mattering ("340 MB").
  const shown = value >= 100 ? String(Math.round(value)) : value.toFixed(1).replace(/\.0$/, '');
  return `${shown} ${UNITS[unit]}`;
}

/** When a file was changed: "Today, 14:05", "Yesterday, 09:12" or a date like "12 Nov 2026".
 *  `now` and `locale` exist so the checks do not depend on the clock or the PC's language. */
export function dateText(iso, now = new Date(), locale = undefined) {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  const daysAgo = Math.round((startOfDay(now) - startOfDay(date)) / 86400000);
  const time = date.toLocaleTimeString(locale, { hour: 'numeric', minute: '2-digit' });
  if (daysAgo === 0) return `Today, ${time}`;
  if (daysAgo === 1) return `Yesterday, ${time}`;
  return date.toLocaleDateString(locale, { day: 'numeric', month: 'short', year: 'numeric' });
}

const startOfDay = (date) => new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();

/** The file name a download answer suggests in its Content-Disposition header, or `fallback`
 *  when there is none. Understands both spellings servers send: filename*=UTF-8''... (any
 *  language, percent-encoded) and the plain filename="...". */
export function fileNameFromDisposition(header, fallback) {
  const text = String(header ?? '');
  const encoded = /filename\*\s*=\s*UTF-8''([^;]+)/i.exec(text);
  if (encoded) {
    try { return decodeURIComponent(encoded[1].trim()) || fallback; } catch { /* badly encoded: try the plain form */ }
  }
  const plain = /filename\s*=\s*(?:"([^"]*)"|([^;]+))/i.exec(text);
  return (plain?.[1] ?? plain?.[2] ?? '').trim() || fallback;
}
