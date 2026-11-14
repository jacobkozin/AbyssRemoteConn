// Small helpers for building the page without a framework.

export const $ = (id) => document.getElementById(id);

/** A new element with a class and, if wanted, text. Text is always set as text, never as
 *  HTML: names of games, files and devices come from outside this app. */
export function el(tag, className = '', text = '') {
  const element = document.createElement(tag);
  if (className) element.className = className;
  if (text) element.textContent = text;
  return element;
}

/** One of the icons drawn at the top of index.html (the symbol with id "i-<name>"). */
export function icon(name) {
  const namespace = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(namespace, 'svg');
  svg.setAttribute('class', 'icon');
  svg.setAttribute('aria-hidden', 'true');
  const use = document.createElementNS(namespace, 'use');
  use.setAttribute('href', `#i-${name}`);
  svg.append(use);
  return svg;
}

/** Hands a fetched file to the user. Phones and tablets get the share sheet when the browser
 *  offers one (that is where "Save Image" lives on an iPhone); everything else downloads it. */
export async function saveBlob(blob, name) {
  const file = new File([blob], name, { type: blob.type });
  if (window.matchMedia('(pointer: coarse)').matches && navigator.canShare?.({ files: [file] })) {
    try { await navigator.share({ files: [file] }); return; }
    catch (error) { if (error.name === 'AbortError') return; /* the user closed the sheet */ }
  }
  const link = document.createElement('a');
  link.href = URL.createObjectURL(blob);
  link.download = name;
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(link.href), 60000);
}
