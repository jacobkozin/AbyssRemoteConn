// Static checks for the browser client: run with `node tests/web/client-checks.mjs`.
// They catch wiring mistakes a C# test cannot see (a typo in an element id, a broken
// import, an inline script the Content-Security-Policy would block). Behavior is covered by
// the .NET integration tests and by manual testing on real devices.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const web = path.join(root, 'src', 'AbyssRemote.Host', 'wwwroot');
const read = (file) => fs.readFileSync(path.join(web, file), 'utf8');
const html = read('index.html');
const css = read('style.css');
const scripts = fs.readdirSync(web).filter((f) => f.endsWith('.js'));
const source = Object.fromEntries(scripts.map((f) => [f, read(f)]));
const allJs = Object.values(source).join('\n');

let failed = 0;
function check(name, fn) {
  try { fn(); console.log('PASS', name); }
  catch (error) { failed++; console.log('FAIL', name, '\n     ', error.message.split('\n')[0]); }
}

check('every script is valid JavaScript (checked as an ES module)', () => {
  // `node --check` silently skips some module files named .js, so check .mjs copies.
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'abyss-client-'));
  try {
    for (const file of scripts) {
      const copy = path.join(temp, file.replace(/\.js$/, '.mjs'));
      fs.writeFileSync(copy, source[file]);
      try { execFileSync(process.execPath, ['--check', copy], { stdio: 'pipe' }); }
      catch (error) { throw new Error(`${file}: ${error.stderr.toString().split('\n').find((l) => l.includes('Error')) ?? 'syntax error'}`); }
    }
  } finally { fs.rmSync(temp, { recursive: true, force: true }); }
});

check('every relative import points at a file that exists', () => {
  for (const [file, text] of Object.entries(source))
    for (const [, target] of text.matchAll(/^\s*import\s[^'"]*['"](\.\/[^'"]+)['"]/gm))
      assert.ok(fs.existsSync(path.join(web, target)), `${file} imports missing ${target}`);
});

check('every element id used by the scripts exists in index.html', () => {
  const ids = new Set([...allJs.matchAll(/\$\(['"]([\w-]+)['"]\)/g)].map((m) => m[1]));
  const missing = [...ids].filter((id) => !html.includes(`id="${id}"`));
  assert.deepEqual(missing, []);
});

check('no inline scripts, inline styles or inline handlers (the CSP would block them)', () => {
  assert.doesNotMatch(html, /<script(?![^>]*\bsrc=)[^>]*>/i, 'inline <script>');
  assert.doesNotMatch(html, /\sstyle=["']/i, 'style="" attribute');
  assert.doesNotMatch(html, /\son[a-z]+=["']/i, 'onclick-style handler');
});

check('nothing is loaded from another site (no CDN, fonts or analytics)', () => {
  assert.doesNotMatch(html + css + allJs, /(?:src|href)=["']https?:|url\(\s*['"]?https?:|fetch\(\s*['"]https?:|import[^'"]*['"]https?:/i);
});

check('index.html cache-busting matches the project version', () => {
  const project = fs.readFileSync(path.join(root, 'src', 'AbyssRemote.Host', 'AbyssRemote.Host.csproj'), 'utf8');
  const version = project.match(/<Version>([^<]+)<\/Version>/)[1];
  assert.match(html, new RegExp(`app\\.js\\?v=${version.replace(/\./g, '\\.')}"`));
  assert.match(html, new RegExp(`style\\.css\\?v=${version.replace(/\./g, '\\.')}"`));
  assert.match(html, new RegExp(`<footer>[^<]*<span>${version.replace(/\./g, '\\.')}</span>`));
});

check('sockets always carry the CSRF subprotocol', () => {
  const sockets = [...allJs.matchAll(/new WebSocket\((.*)$/gm)].map((m) => m[1]);
  assert.ok(sockets.length > 0, 'no WebSockets found');
  for (const args of sockets) assert.match(args, /PROTOCOL|protocols/, `WebSocket without subprotocols: ${args}`);
});

if (failed) {
  console.log(`\n${failed} client check(s) failed.`);
  process.exit(1);
}
console.log('\nAll browser client checks passed.');
