// Shared by the *-checks.mjs files next to it: a small PASS/FAIL runner, a way to load the
// browser's own modules into Node, and a pretend page for the few functions that touch one.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const web = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', 'src', 'AbyssRemote.Host', 'wwwroot');

/** Imports browser modules by file name, for example `loadModules('files.js', 'format.js')`.
 *  They are plain .js files with no package.json beside them, so Node gets temporary .mjs
 *  copies (with their imports renamed to match). */
export async function loadModules(...names) {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'abyss-client-'));
  const asModule = (file) => file.replace(/\.js$/, '.mjs');
  try {
    for (const file of fs.readdirSync(web).filter((f) => f.endsWith('.js'))) {
      const text = fs.readFileSync(path.join(web, file), 'utf8').replace(/(from\s+['"]\.\/[^'"]+)\.js(['"])/g, '$1.mjs$2');
      fs.writeFileSync(path.join(temp, asModule(file)), text);
    }
    return await Promise.all(names.map((name) => import(pathToFileURL(path.join(temp, asModule(name))).href)));
  } finally { fs.rmSync(temp, { recursive: true, force: true }); }
}

/** Just enough of a browser page for code that looks elements up by id and reads the address.
 *  Returns what the code under test did to it. */
export function pretendPage({ hash = '', platform = '', touchPoints = 0 } = {}) {
  const page = { elements: {}, replaced: [] };
  const element = () => ({
    value: '', textContent: '', disabled: false, listeners: {},
    classList: { add() {}, remove() {}, toggle() {} },
    addEventListener(type, handler) { this.listeners[type] = handler; },
  });
  const define = (name, value) => Object.defineProperty(globalThis, name, { value, configurable: true, writable: true });
  define('document', { getElementById: (id) => (page.elements[id] ??= element()) });
  define('location', { hash, pathname: '/' });
  define('history', { replaceState: (...args) => page.replaced.push(args) });
  define('navigator', { platform, maxTouchPoints: touchPoints });
  return page;
}

let failed = 0;

/** Runs one check. A check that is asynchronous returns its promise: `await check(…)`. */
export function check(name, fn) {
  const pass = () => console.log('PASS', name);
  const fail = (error) => { failed++; console.log('FAIL', name, '\n     ', String(error.message).split('\n')[0]); };
  try {
    const result = fn();
    if (result instanceof Promise) return result.then(pass, fail);
    pass();
  } catch (error) { fail(error); }
}

/** Prints the result line and makes the process fail when a check did. */
export function finish(what) {
  if (failed) {
    console.log(`\n${failed} ${what} check(s) failed.`);
    process.exit(1);
  }
  console.log(`\nAll ${what} checks passed.`);
}
