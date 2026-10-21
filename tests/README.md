# Tests

## .NET tests: `dotnet test tests\AbyssRemote.Tests`

MSTest, 102 tests, no network or hardware needed.

- **Unit tests:** pairing limits and expiry (with a manual clock), session cookie checks and
  storage, CSRF, origin matching, host allowlist, the private-address filter, certificate
  coverage, command-line options, keyboard scan codes, controller mapping and IOCTL codes,
  H.264 Annex B / AVCC / avcC conversion, stream sizing.
- **Integration tests** (`ServerIntegrationTests`, `GameModeProtocolTests`): each test starts
  the real web server (Kestrel, TLS with a throwaway certificate, all middleware and endpoints)
  on a free loopback port, with fake desktop, input, clipboard, video and audio sources that
  record what reaches them. The tests then act as a browser, or as an attacker without a
  session, a wrong origin, a wrong CSRF token, a revoked device or oversized messages, and
  check that nothing reaches the fakes unless every check passed. They also cover pairing,
  cookies, revocation, reconnect replacement, input release on disconnect, and the
  JPEG/Game-mode/audio protocols including flow control.

## Browser client checks: `node tests\web\client-checks.mjs`

Every browser module parses as an ES module, imports resolve, every element id the scripts
use exists, there are no inline scripts or styles (the CSP would block them), nothing loads
from other sites, every WebSocket carries the CSRF subprotocol, and the cache-busting version
matches the project version.

## Published host self-test: `AbyssRemote.Host.exe --smoke-test`

Starts the real published host with its real certificate on a free loopback port, checks it
from outside with the certificate pinned (no system trust changes), confirms the UI and
security headers, and confirms that every API route and WebSocket refuses anonymous access.
`TEST-HOST.bat` runs it.

## Game mode on real hardware: `AbyssRemote.Host.exe --probe-video`

Builds the GPU capture + hardware encoder pipeline on every monitor and reports the encoder,
frame rate, encode time and bitrate.

## Manual testing still needed

On an iPhone or iPad: pairing and root-certificate trust, touch gestures, Game mode decoding,
sound, a controller with the ViGEmBus driver installed on the PC, and the Home Screen app. See
[docs/ROADMAP.md](../docs/ROADMAP.md).
