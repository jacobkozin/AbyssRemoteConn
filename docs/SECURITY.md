# Security

Abyss Remote lets another device see your screen, type, move the mouse, use a virtual game
controller, read and write the clipboard, and hear the PC's sound. Since 1.1 a paired device
can also send files to one folder on the PC and fetch or delete files there, lock the PC or
put it to sleep, and list and start the installed Steam games. Anyone who gets a session has
all of that. This page describes what protects it, what was fixed for 1.0, what 1.1 added,
and what is still a known risk. **It has not been independently audited or
penetration-tested.**

## Reporting a vulnerability

Use GitHub's private vulnerability reporting for this repository if it is enabled. Otherwise
open a minimal issue asking for a private contact, without exploit details, pairing codes,
cookies, certificates, logs or network addresses.

## Threat model

Defended against:

- **Other devices on the same network** that try to pair, guess codes, or use the API without
  a session.
- **Websites you visit** on a paired phone or on the PC, which could try cross-site requests,
  cross-site WebSockets, DNS rebinding or clickjacking.
- **Accidental exposure,** such as a router port forward set up by mistake.
- **A lost or retired device:** its access can be revoked instantly.

Out of scope: malware running on the PC or on a paired device; anyone who can see the Abyss
Remote window on the PC (it shows the pairing code, as digits and inside the QR code);
someone who controls your Wi-Fi equipment; denial of service by a device you paired yourself.

## Protections

| Protection | How | Code | Tested by |
| --- | --- | --- | --- |
| Not reachable by default | Listens on 127.0.0.1 until home-network mode is turned on (the window's setting, or `--lan`) | `AbyssServer.Build`, `Preferences` | smoke test, `LaunchOptionsTests`, `WithoutAFileTheDefaultsAreSafe` |
| LAN mode refuses internet addresses | A Kestrel connection filter drops peers outside loopback, RFC 1918, link-local and IPv6 unique-local before the TLS handshake | `NetworkGuard` | `RequestProtectionTests` |
| Firewall | Optional rule: one TCP port, Private profile, local subnet only. The window only reads the firewall; **Add firewall rule…** starts the same script behind the Windows administrator prompt | `scripts/Firewall-Enable-LAN.ps1`, `PhoneSetup` | manual; the reading logic by `PhoneSetupTests` |
| TLS | TLS 1.2/1.3 only, server certificate from a private root CA, keys non-exportable in the Windows user store; reissued from the same root when near expiry or when the LAN address changed | `HostCertificates` | smoke test (pinned), `CertificateCoverage…` |
| Root certificate download | `/AbyssRemote-RootCA.cer` needs no pairing, so a phone can fetch it before it trusts the host. It is the public certificate only, never the private key; GET only; still behind the `Host` check | `AbyssServer` | `AnyoneCanDownloadThePublicRootCertificate`, `TheDownloadIsReadOnlyAndStillBehindTheHostCheck`, smoke test |
| DNS rebinding | Unknown `Host` headers get 421 | `RequestProtection.KnownHost` | integration + smoke test |
| Pairing code | 8 digits from a CSPRNG, shown only in the Abyss Remote window (and its QR code), 10 minutes, single use, constant-time comparison. A used, expired or locked code is no longer shown. A hidden start (`--minimized`) makes no code until someone asks for one | `PairingManager`, `HostRuntime`, `WindowApp` | `PairingManagerTests`, `PairingRaisesEventsAndUsesUpTheCode`, `TheAppStartsHiddenServesAndQuitsCleanly` |
| QR code | The code travels in the address's fragment (`#pair=`), which a browser never sends in a request. The page moves it into the pairing form and removes it from the address bar and history at once | `PairingCode.PairingUrl`, `pairing.js` | `ThePairingCodeIsShownInTwoGroupsAndGoesIntoTheQrAddress`, `tests/web/pairing-checks.mjs` |
| Brute force | 5 tries per address per 5 minutes, and the code is destroyed after 20 wrong tries in total (any mix of addresses), so at most 20 guesses out of 100,000,000 | `PairingManager` | `TwentyWrongGuesses…` |
| Session | 256-bit random secret; cookie `Secure`, `HttpOnly`, `SameSite=Strict`, `Path=/`; only the SHA-256 hash is stored; constant-time check; 30-day lifetime; expired devices pruned | `StateStore` | `StateStoreTests`, `PairingSetsAHardenedSessionCookie` |
| Cross-site requests | Every request that changes or does something on the PC needs the cookie, the exact `Origin` and a per-device CSRF token (HMAC with a key that changes on every host start). That includes uploads, deletes, lock, sleep and everything that starts Steam | `ServerContext.Authorized` | `StateChangingRequestsNeedCsrf`, `ForgedRequests` (each new request in seven forged forms) |
| WebSockets | One gate for all four sockets: cookie + exact `Origin` + CSRF token as subprotocol; failures get HTTP 403 and never reach the desktop | `AbyssServer.RunSocketAsync` | `ControlSocketNeedsCookieOriginAndCsrf`, `VideoSocketHasTheSameSecurityGate`, `AnonymousClientsCannotSeeOrControlThePc` |
| Revocation | Revoking (in the browser, with **Revoke** or **Revoke all** in the window, or `R` in console mode) closes the device's live sockets at once; every message also re-checks the device | `SessionRegistry`, `StateStore.IsActive` | `RevokingADeviceDisconnectsItImmediately`, `LiveConnectionsAreReportedAndRevokingClosesThem` |
| Input validation | Control messages are small JSON (8 KB max); every number is clamped; unknown kinds are ignored; held keys, buttons and controllers are released on disconnect. A `bitrate` message is bounded, applied at most once a second, and earns no frame credits | `InputCommand`, `NativeInputSink`, `VideoStream` | `MalformedControlMessages…`, `GamepadTests`, `BitrateChangesAreBoundedAndRateLimitedWithoutGrantingFrames` |
| File transfer is one folder | A device never sends a path. Upload names must be plain file names and are refused, never repaired. Downloads and deletes are matched against a real listing of the folder instead of building a path, so traversal, absolute paths, alternate data streams, 8.3 aliases and names Windows would tidy up reach nothing. Sub-folders, links, hidden and system files and unfinished uploads are invisible | `TransferName`, `TransferFolder` | `FileTransferAttackTests`, `TransferFolderTests`, `TransferNameTests` |
| Uploads | Never replace a file ("name (2).ext"). They arrive under a hidden partial name and are removed when they fail or are cancelled. On NTFS each one gets the Mark of the Web (`Zone.Identifier`, `ZoneId=3`), so Windows and SmartScreen treat it like any download. The limit is 2 GiB, refused from the declared length and counted again while streaming | `TransferFolder`, `FileTransferEndpoints` | `ATakenNameGetsANumberAndNothingIsReplaced`, `ACancelledUploadLeavesNothingBehind`, `UploadsCarryTheDownloadMarkAndAreOrdinaryFiles`, `OversizedUploadsAreRefused` |
| Downloads are never rendered | Always `attachment` + `application/octet-stream` + `nosniff`, so an uploaded HTML file cannot run as a page of the host's origin | `FileTransferEndpoints` | `AFileGoesToThePcAndComesBackUnchanged` |
| Host actions are a fixed list | Lock and sleep, nothing else. `IHostActions` has one method per action and none takes a parameter, so no request data can become a command. Each use is shown on the PC with the device's name | `IHostActions`, `HostActionEndpoints` | `NoActionTakesAnythingFromTheBrowser`, `AnythingOffTheListIsNotFound`, `StrangersCannotSeeOrRunActions` |
| Starting Steam games | The only input is a game's number: digits only, 32-bit, no sign, space, hex or overflow. It must match a fully installed game before anything is started, and that check sits in the launcher itself. `steam.exe` is started directly, without a shell, with one argument built from fixed text and the number. No `steam://` link is opened through Windows | `SteamLibrary.TryParseId`, `WindowsSteam.Launch`, `WindowsSteam.Run` | `OnlyAnInstalledGameCanBeStarted`, `AGameNumberIsDigitsOnly`, `StartingAGameRunsSteamItselfWithOneFixedArgument` |
| Steam's files | Only read, never written. Damaged, oversized or locked files are skipped. A cover is sent only for a listed game and only as real JPEG or PNG data up to 8 MB. The list and covers need the cookie | `SteamLibrary`, `KeyValues`, `SteamEndpoints` | `StrangersCannotSeeGamesOrCovers`, `ACoverRequestCanOnlyNameAGameByNumber`, `OnlyListedGamesHaveACover`, `DamagedAndOddManifestsAreSkipped` |
| Previews and screenshots | Need the paired cookie, like watching the stream. Refused while Windows blocks capture. One capture at a time | `DisplayPreviewEndpoints` | `StrangersGetNoPictures`, `ALockedPcAnswers503WithTheReason`, `CapturesNeverOverlap` |
| Changes to the PC | The only registry write is "Start with Windows": one value under the current user's `Run` key, written when the box is ticked and deleted when it is unticked. The app never changes the firewall or the network type itself | `Autostart`, `WindowApp` | `NothingIsWrittenUntilTheUserTurnsItOn`, `TurningItOffRemovesTheEntry`, `OtherProgramsEntriesAreNeverTouched` |
| Browser hardening | CSP `default-src 'none'` with same-origin scripts/styles only, `frame-ancestors 'none'`, `X-Frame-Options: DENY`, `nosniff`, `no-referrer`, `no-store`; no inline scripts; untrusted text (names of devices, games, files and monitors) rendered with `textContent` | `AbyssServer`, `wwwroot` | `UiIsServedWithSecurityHeaders`, client checks |
| No detailed errors | Always runs as Production, so no stack traces reach clients | `AbyssServer.Build` | — |
| Output on the PC | Names of devices and games lose control characters before they are stored, listed or shown; an upload name with a control character is refused | `StateStore.CleanName`, `SteamLibrary.CleanName`, `TransferName` | `DeviceNamesLoseControlCharacters…`, `GameNamesLoseControlCharactersAndAreCut`, `EveryControlCharacterIsRefusedAnywhereInTheName` |
| Resource limits | Request bodies 64 KB (only an upload may be larger, and only after the cookie, Origin, CSRF and name checks), 64 connections, per-kind socket caps (6 total, 1–2 per device), at most 2 video frames in flight, bounded audio queue, one screen capture at a time | `AbyssServer`, `SessionRegistry`, `VideoStream`, `FileTransferEndpoints` | `AReconnectReplaces…`, `VideoIsFlowControlledByAcks`, `OtherRequestsKeepTheSmallBodyLimit` |
| No secrets in the repo | `settings.json`, certificates, keys, logs and builds are git-ignored; state lives in `%LOCALAPPDATA%\AbyssRemote` | `.gitignore` | review |

## What the 1.0 review found and fixed

These were present in R3.3 (0.3.3) and are fixed in 1.0:

1. **Pairing guesses were only limited per address.** Many addresses together could keep
   guessing. Now 20 wrong guesses in total destroy the code.
2. **LAN mode accepted any source address.** With a mistaken port forward, the login page and
   API faced the internet. Now non-local peers are dropped before TLS.
3. **No `Host` header check.** TLS already stopped DNS rebinding in practice; there is now a
   second layer.
4. **Revocation waited for the next message,** and a revoked phone could keep watching an idle
   screen. Now revoking closes the sockets immediately.
5. **Keys held at a disconnect stayed down on the PC** (Ctrl, Alt, Shift, Windows), and all
   mouse buttons were released blindly, which can even confuse another connection. Now each
   connection releases exactly what it pressed, and also does so when the browser loses focus.
6. **Missing numeric fields defaulted to the minimum of their range:** a `move` message without
   `dx` moved the mouse 400 px left, and a controller message without `ly` would have meant
   "stick fully up". Missing values now mean 0.
7. **Dead connections held the only two video/input slots** for minutes, so a phone that
   switched networks was refused (HTTP 429) when it reconnected. Now there are keepalive
   timeouts, and a reconnect replaces the old socket.
8. **After the host restarted, input never came back** until the page was reloaded (the
   browser kept a stale CSRF token). It now fetches a fresh one on every reconnect.
9. **Device names were printed to the console unfiltered,** so control characters could spoof
   console output. They are now stripped.
10. **`ASPNETCORE_ENVIRONMENT=Development` would have shown stack traces** to clients. It is
    now forced to Production.
11. **Each certificate renewal left the old server certificate in the user store.** The
    previous one is now removed, matched by exact thumbprint, subject and issuer.
12. **The default port 8443 collided with another local HTTPS server** on the development PC,
    and the console told users to open `https://localhost:8443`, which browsers may resolve to
    `::1`, that other server. The default is now 8444, the console prints `127.0.0.1`, and a
    clear error appears when the port is taken.
13. **The CI "syntax check" (`node --check app.js`) did not actually check ES module syntax**
    (Node silently accepted a broken module). The checks now run on `.mjs` copies.

## What 1.1 added

1.1 gives a paired device more to do, so there is more to protect. Each point has a row in
the table above. A paired device can already use the keyboard and mouse, so none of this is
a wall against a device you paired yourself. The rules make sure each new route does no more
than it says, also when a request is forged or malformed.

- **File transfer.** A paired device can now write into, read from and delete in one folder,
  `Downloads\Abyss Remote`. It cannot name a path, and nothing outside that folder can be
  reached.
- **Lock and sleep.** A paired device can lock or suspend the PC. Those are the only two
  actions, and neither takes any data from the request.
- **Steam.** A paired device can see the installed Steam games and start one, or Big Picture.
  It picks a game by number; it cannot supply a path, a program name or a command line.
- **Stills of every monitor.** Previews and screenshots show what the stream would show, to
  the same paired devices.
- **A public download.** Anyone who can reach the host can fetch the root certificate's
  public part. It is not a secret. See the note on trust below.
- **The pairing code on screen.** The code moved from the console to the window, and in
  home-network mode it is also inside the QR code.
- **A window with settings.** Turning on home-network mode is now a setting that is saved,
  not only a command-line option. It is off until you turn it on, and the window explains it
  once and asks you to confirm.
- **Confirmations in the browser.** Lock, sleep, delete, sign-out, revoke, starting a game and
  Big Picture ask first. That guards against a slip of the finger. The server-side checks are
  the real gate.

These parts were verified with automated tests and, for file transfer, the Steam list and
previews, in the browser against the real host on loopback. Lock, sleep, starting a game and
Big Picture have only been run against stand-ins.

## Known risks and limitations

- **No independent audit or penetration test.**
- **Root certificate trust is powerful.** A device that trusts the Abyss Remote root will trust
  any certificate signed by it. The root's private key cannot be exported, but malware running
  as your Windows user could still use it. The root has no name constraints. Remove it from
  your devices if you stop using Abyss Remote.
- **The certificate download is not protected until the device trusts it.** Someone in the
  middle of your network could hand the phone a different certificate. Compare the SHA-256
  fingerprint on the device with the one in the window before you turn on trust, or carry the
  saved file over yourself.
- **Cookies are per host, not per port.** Another HTTPS service on the same address (for
  example a different local server on `https://192.168.1.20:8443`) receives the session
  cookie. Only run servers you trust on the same PC address.
- **A stolen session cookie is full access** for up to 30 days, until the device is revoked.
  Sessions are not bound to a device fingerprint.
- **Every paired device has full control.** There are no read-only devices, roles, MFA or audit
  log. **Watch only** is a switch in the browser, not a permission: the device can still send
  input. The window lists new pairings, revocations, file transfers, lock, sleep, game starts
  and refused connections, but only the newest 500 lines, in memory.
- **The pairing code is on the PC's screen.** Anyone who sees the window, or a photo or
  screenshot of it, can read the code or scan the QR code while the code is valid.
- **Anyone on the local network can reach the pairing page** in home-network mode. Guessing is
  capped as described above, but they can make you click **New code**.
- **Uploaded files are untrusted and not filtered by type.** A paired device can put an
  `.exe`, `.lnk` or `.url` file into the folder. The download mark only exists on NTFS.
- **The transfer folder is shared with every paired device.** Each one can read and delete
  everything in it, including files you put there yourself, and can fill the drive (2 GiB per
  upload, no total limit). Deletes are permanent.
- **A paired device can lock or suspend the PC,** which also cuts off everyone else.
- **Steam.** A paired device can see which games are installed, their sizes and when they
  were last played, and can start any of them. Which program is started as "Steam" comes
  from the current user's registry (`HKCU\Software\Valve\Steam`); a program already running
  as that user could change it. If Steam was not running, the copy that Abyss Remote starts
  is its child: it gets Abyss Remote's environment, and it is elevated if Abyss Remote is. A
  browser may keep cover pictures for up to a day, also after the device was revoked.
- **Sound streaming sends everything the PC plays** to the paired device, including voice
  chat. It is off until you turn it on in the browser.
- **Clipboard:** a paired device can read the PC's text clipboard whenever it asks.
- **Game mode uses hand-written native interop** (DirectX, Media Foundation, WASAPI, ViGEmBus).
  A bug there could crash the host. Only paired devices can start Game mode, sound or
  controllers.
- **ViGEmBus is a third-party kernel driver** that is no longer actively developed. Installing
  it is your decision; Abyss Remote works without it.
- **Code that has not run for real yet.** The Windows calls for lock and sleep, the real start
  of Steam, and the controller and rumble path through ViGEmBus are covered by tests that stop
  at the last step before Windows or the driver. Home-network mode was not started on the
  development PC, and nothing has been tested on a real phone.
- **The "Phone setup" checks are readings, not guarantees.** The window reads the firewall
  rules and the network type; another security program can still block or allow a connection.
- **Lock screen, UAC prompts and elevated windows** cannot be seen or controlled. That is
  Windows' own protection, and it is a limitation and a safety property at the same time.
- **Binaries are unsigned,** and builds are not reproducible or attested.
- **Pairing limits live in memory.** They reset when the program is started again (which also
  needs a new code).

## Removing access

- Revoke one device from **Paired devices** on the browser's home screen, or with **Revoke**
  in the window. **Revoke all** in the window (or `R` in console mode) removes all of them.
  Their connections close immediately.
- Stop Abyss Remote with **Quit** in the tray icon's menu. Closing the window only hides it,
  unless "Keep running in the tray when I close the window" is off. In console mode, press `Q`.
- Untick **Start with Windows** if you turned it on; that removes its registry value.
- Remove the firewall rule as Administrator:
  `Remove-NetFirewallRule -DisplayName 'Abyss Remote (Local Subnet, TCP 8444)'`
  (and `'Abyss Remote (Local Subnet)'` if a pre-1.0 rule exists).
- To retire Abyss Remote completely, also remove the "Abyss Remote Personal Root CA" from
  every device that trusts it and from the Windows user store (check thumbprints before
  deleting anything), and delete `%LOCALAPPDATA%\AbyssRemote`. Files that were sent to
  `Downloads\Abyss Remote` stay until you delete them.
