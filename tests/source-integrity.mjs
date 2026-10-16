import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => fs.readFileSync(path.join(root, file), 'utf8');
const html = read('src/AbyssRemote.Host/wwwroot/index.html');
const js = read('src/AbyssRemote.Host/wwwroot/app.js');
const css = read('src/AbyssRemote.Host/wwwroot/style.css');
const host = read('src/AbyssRemote.Host/Program.cs');
const security = read('src/AbyssRemote.Host/Security.cs');
const persistence = read('src/AbyssRemote.Host/StateStore.cs');
const desktop = read('src/AbyssRemote.Host/DesktopEngine.cs');

function verify(name, fn) { fn(); console.log('PASS', name); }
verify('Mobile browser scripts and CSS are bundled locally', () => {
  assert.match(html, /src="\/app\.js(?:\?v=[^"]+)?"/);
  assert.match(html, /href="\/style\.css(?:\?v=[^"]+)?"/);
  assert.doesNotMatch(html + js + css, /https?:\/\/(?:cdn\.|unpkg\.|cdnjs\.|fonts\.googleapis\.|jsdelivr\.)/i);
});
verify('Every DOM ID referenced by browser script appears in HTML', () => {
  const ids = [...js.matchAll(/\$\(['"]([a-zA-Z0-9_-]+)['"]\)/g)].map(match => match[1]);
  for (const id of new Set(ids)) assert.match(html, new RegExp(`id="${id}"`), `${id} is missing`);
  console.log(`  ${new Set(ids).size} IDs verified`);
});
verify('Pairing and input require origin checks and CSRF on control', () => {
  assert.match(host, /RequestProtection\.SameOrigin\(ctx\)/);
  assert.match(host, /ValidCsrf\(ctx\.WebSockets\.WebSocketRequestedProtocols\[1\], device\.Id\)/);
  assert.match(security, /FixedTimeEquals/);
});
verify('Session cookie is secure, HTTP-only and Strict SameSite', () => {
  assert.match(host, /HttpOnly = true, Secure = true, SameSite = SameSiteMode\.Strict/);
  assert.match(persistence, /SHA256\.HashData/);
});
verify('Non-paired browser cannot enumerate displays', () => {
  assert.match(host, /MapGet\("\/api\/v1\/displays"/);
  assert.match(host, /Device\(ctx\) is null \? \(IResult\)Results\.Unauthorized\(\)/);
});
verify('Streaming uses complete JPEG binary frames over WSS', () => {
  assert.match(js, /wss:\/\//);
  assert.match(host, /WebSocketMessageType\.Binary, true/);
  assert.match(desktop, /image\/jpeg/);
});
verify('Host defaults to loopback unless explicitly run in LAN mode', () => {
  assert.match(host, /isLan \? IPAddress\.Any : IPAddress\.Loopback/);
  assert.match(read('scripts/Firewall-Enable-LAN.ps1'), /-RemoteAddress LocalSubnet/);
});
verify('No uncompiled native pre-built binaries included', () => {
  const forbidden = ['dist/win-x64/AbyssRemote.Host.exe', 'src/AbyssRemote.Host/settings.json'];
  for (const file of forbidden) assert.ok(!fs.existsSync(path.join(root, file)));
});
verify('Documentation and GitHub source files included', () => {
  for (const file of ['README.md', 'LICENSE', 'CHANGELOG.md', 'CONTRIBUTING.md', 'docs/API.md', 'docs/ARCHITECTURE.md', 'docs/SECURITY.md', 'docs/DEVELOPMENT_STATUS.md', '.github/workflows/build.yml']) {
    assert.ok(fs.existsSync(path.join(root, file)), file);
  }
});
verify('ASP.NET Core web root configured during builder creation (crash regression)', () => {
  assert.match(host, /WebApplication\.CreateBuilder\(new WebApplicationOptions/);
  assert.match(host, /WebRootPath\s*=\s*Path\.Combine\(AppContext\.BaseDirectory, "wwwroot"\)/);
  assert.match(host, /ContentRootPath\s*=\s*AppContext\.BaseDirectory/);
  assert.doesNotMatch(host, /builder\.WebHost\.UseWebRoot\(/);
});
verify('Certificate PFX loading uses current API (no obsolete constructor)', () => {
  assert.equal((security.match(/X509CertificateLoader\.LoadPkcs12\(/g) || []).length, 2);
  assert.doesNotMatch(security, /new X509Certificate2\(/);
});
verify('Published binary fingerprint is verified and rebuilt if out of date', () => {
  const launch = read('scripts/Launch.ps1');
  const build = read('scripts/Build-Windows.ps1');
  const fingerprint = read('scripts/Source-Fingerprint.ps1');
  assert.match(launch, /Get-AbyssSourceFingerprint/);
  assert.match(launch, /Build-Windows\.ps1/);
  assert.match(build, /\.abyss-source\.sha256/);
  assert.match(fingerprint, /obj\|bin/);
});
verify('The executable refuses to run if published browser assets are missing', () => {
  assert.match(host, /Published web UI is missing/);
  assert.match(read('scripts/Build-Windows.ps1'), /wwwroot\\index.html/);
});
verify('Windows GitHub Actions performs HTTPS runtime smoke test', () => {
  const workflow = read('.github/workflows/build.yml');
  assert.match(workflow, /Smoke-Test\.ps1/);
});
verify('Windows HTTPS test runs inside host using pinned certificate and tests unauthenticated routes', () => {
  const smoke = read('scripts/Smoke-Test.ps1');
  assert.match(smoke, /--smoke-test/);
  assert.doesNotMatch(smoke, /ServerCertificateValidationCallback|Invoke-RestMethod|Invoke-WebRequest/);
  assert.match(host, /isSmokeTest/);
  assert.match(host, /ServerCertificateCustomValidationCallback/);
  assert.match(host, /FixedTimeEquals/);
  assert.match(host, /SELF-TEST PASSED/);
  assert.match(host, /HttpStatusCode\.Unauthorized/);
});
verify('One-click clean build also runs full HTTPS self-test', () => {
  const oneClick = read('BUILD-AND-TEST.bat');
  assert.match(oneClick, /Build-Windows\.ps1/);
  assert.match(oneClick, /Smoke-Test\.ps1/);
  assert.match(oneClick, /if errorlevel 1 goto failed/);
});
verify('Smoke test refuses stale binary and reports genuine host errors', () => {
  const smoke = read('scripts/Smoke-Test.ps1');
  assert.match(smoke, /Get-AbyssSourceFingerprint/);
  assert.match(smoke, /Build-Windows\.ps1/);
  assert.match(smoke, /exitCode/);
  assert.match(smoke, /Port 8443 is in use/);
});
console.log('R3 source integrity checks passed. Real Windows build and end-to-end testing still required.');
