# API

Version `v1`, application 1.1.0. The host serves HTTPS on port 8444 (change it under Settings
in the window, or with `--port` for one run). All routes are under `/api/v1/`, except the root
certificate download. JSON bodies need `Content-Type: application/json`. Clients must tolerate
unknown fields; new behavior that would break existing clients belongs under a new version.

## Authentication

1. Pairing code: shown in the Abyss Remote window on the PC (click **New code** for a new one;
   with `--console`, press `P`). 8 digits, valid 10 minutes, works once. Limits: 5 tries per
   address per 5 minutes; 20 wrong tries in total destroy the code. In home-network mode the
   window's QR code holds `https://<address>:<port>/#pair=<8 digits>` while the code is
   usable. The browser client copies the digits into its pairing form and removes the fragment
   from the address at once. A fragment is never part of a request, so the host does not see
   it.
2. `POST /api/v1/pair` with `{"code":"01234567","label":"My iPhone"}` and a same-origin
   `Origin` header. On success the host sets the cookie
   `abyss_remote_session` (`Secure; HttpOnly; SameSite=Strict; Path=/`, 30 days).
3. `GET /api/v1/me` returns `{ id, device, csrf, version, features: { gameMode } }`. The CSRF
   token changes every time the host starts; fetch it again after reconnecting.
4. State-changing requests send header `X-Abyss-CSRF: <csrf>` and a matching `Origin`. The
   browser client repeats a refused change once after fetching `/api/v1/me` again.
5. Every WebSocket requests the subprotocols `abyss-v1, <csrf>` and needs the cookie and a
   matching `Origin`; the host accepts `abyss-v1`. A failed check is a plain HTTP 403 (the
   browser only sees the socket fail; call `/api/v1/me` to tell 401 from other problems).

In the table below, "cookie" means the paired session cookie; without it the answer is 401.
"cookie + Origin + CSRF" is the full check from step 4; when any part is missing or wrong the
answer is 403. Both have an empty body, and so do most 404 answers. Other errors are
`{ "error": "plain sentence" }`, written for the person using the device.

## REST

| Route | Needs | Result |
| --- | --- | --- |
| `GET /api/v1/info` | nothing | `{ app, version }` |
| `GET /AbyssRemote-RootCA.cer` | nothing | The root certificate's public part, as a download |
| `POST /api/v1/pair` | Origin | `{ paired, name }`, sets the cookie. 400 wrong/expired, 429 too many tries |
| `GET /api/v1/me` | cookie | `{ id, device, csrf, version, features }` |
| `GET /api/v1/displays` | cookie | `[{ id, name, x, y, width, height, primary, friendlyName, refreshHz }]` in physical pixels |
| `GET /api/v1/displays/{id}/preview?width=` | cookie | JPEG still of one monitor, 160–960 px wide (default 480). 404, 503 |
| `GET /api/v1/displays/{id}/screenshot` | cookie | Full-size JPEG as a download. 404, 503 |
| `GET /api/v1/devices` | cookie | `[{ id, name, createdUtc, lastSeenUtc, expiresUtc }]` (no secrets) |
| `DELETE /api/v1/devices/{id}` | cookie + Origin + CSRF | Revokes the device and closes its sockets |
| `POST /api/v1/logout` | cookie + Origin + CSRF | Revokes this device |
| `GET /api/v1/health` | cookie | `{ healthy, time }` |
| `GET /api/v1/clipboard` | cookie | `{ text }` (first 4,096 characters of the PC's text clipboard) |
| `POST /api/v1/clipboard` | cookie + Origin + CSRF | `{ text }` (at most 4,096 characters) replaces the PC's text clipboard |
| `GET /api/v1/files` | cookie | `{ folder, maxUploadBytes, files: [{ name, size, modifiedUtc }] }`, newest first |
| `POST /api/v1/files?name=` | cookie + Origin + CSRF | Body = the file's bytes. 201 `{ name, size }`. 400, 413, 507 |
| `GET /api/v1/files/{name}` | cookie | The file as a download, `Range` supported. 404, 409, 416 |
| `DELETE /api/v1/files/{name}` | cookie + Origin + CSRF | `{ deleted }`. 404, 409 |
| `GET /api/v1/actions` | cookie | `{ actions: [{ id, label }] }`: `lock` and `sleep` |
| `POST /api/v1/actions/{id}` | cookie + Origin + CSRF | `{ done }`. 404 unknown id, 500 when Windows refuses to lock |
| `GET /api/v1/steam` | cookie | `{ installed, games: [{ id, name, hasArt, lastPlayedUtc, sizeBytes }] }` |
| `GET /api/v1/steam/games/{id}/art` | cookie | The game's cover (`image/jpeg` or `image/png`). 404 |
| `POST /api/v1/steam/games/{id}/launch` | cookie + Origin + CSRF | `{ launched, name }`. 404 unless `{id}` is a listed game, 503 |
| `POST /api/v1/steam/bigpicture` | cookie + Origin + CSRF | `{ opened }`. 503 |
| `POST /api/v1/steam/bigpicture/close` | cookie + Origin + CSRF | `{ closed }`. 503 |

Every response carries a strict Content-Security-Policy, `X-Frame-Options: DENY`,
`nosniff`, `no-referrer` and `Cache-Control: no-store`. The one exception is a Steam cover,
which is sent with `Cache-Control: private, max-age=86400`. Requests naming an unknown host
get 421.

### Root certificate: `GET /AbyssRemote-RootCA.cer`

Needs no pairing, so a phone can fetch it before it trusts the host. The answer is the root
certificate's public part in DER form, with `Content-Type: application/x-x509-ca-cert` and
`Content-Disposition: attachment; filename=AbyssRemote-RootCA.cer`. The private key is never
part of it. Other methods get 405.

### Monitors

`GET /api/v1/displays` lists the monitors in a fixed order: the primary monitor is `id` 0, the
others follow from left to right (top to bottom when two share a column).

```json
[ { "id": 0, "name": "\\\\.\\DISPLAY2", "x": 0, "y": 0, "width": 3840, "height": 2160,
    "primary": true, "friendlyName": "DELL U2720Q", "refreshHz": 60 } ]
```

- `id` is the number the routes below, the `display` query parameter and the `pointer`
  message take. It can change when a monitor is added, removed or moved. `name`, the Windows
  device name, is what the browser client remembers.
- `friendlyName` is the model name the monitor reports, at most 60 characters. A monitor
  without one is called `Monitor 1`, `Monitor 2` and so on (`id` + 1). Two monitors of the
  same model have the same name.
- `refreshHz` is in whole Hz, rounded (59.94 is 60). It is 0 when Windows does not say.

`GET /api/v1/displays/{id}/preview?width=480` answers with an `image/jpeg` still (quality 75).
`width` is kept within 160–960; a missing or unreadable value means 480. The picture is never
wider than the monitor, and the height keeps the monitor's shape. The mouse pointer is in it.

`GET /api/v1/displays/{id}/screenshot` answers with an `image/jpeg` (quality 95) at the
monitor's full resolution, as a download named like `Abyss Remote 2026-11-14 14.05.22.jpg`
(the PC's local time).

Both answer 404 for a monitor that does not exist, and 503 with an `error` while Windows
blocks capture (lock screen, UAC prompt) or when the monitor was unplugged a moment ago. The
host makes one capture at a time.

### Files

One folder on the PC, `Downloads\Abyss Remote`. A request never carries a path, only a file
name.

`GET /api/v1/files`:

```json
{ "folder": "C:\\Users\\<user>\\Downloads\\Abyss Remote", "maxUploadBytes": 2147483648,
  "files": [ { "name": "photo.jpg", "size": 307200, "modifiedUtc": "2026-11-14T19:05:22.1234567+00:00" } ] }
```

Only plain files directly in the folder are listed, newest first. The list is empty (and the
folder does not exist yet) before the first upload.

`POST /api/v1/files?name=Holiday%20photo.jpg`: the body is the raw bytes of the file, with
any `Content-Type`.

- 201 `{ "name": "Holiday photo (2).jpg", "size": 307200 }` and a `Location` header. `name` is
  the name the file ended up with: a name that is taken gets a number, and nothing is ever
  replaced. An empty body makes an empty file.
- 400 when `name` is missing, repeated or not a safe file name (rules below).
- 413 when the file is larger than `maxUploadBytes` (2 GiB). A request that states its length
  is refused before the host reads it.
- 507 when the PC does not have enough free space. 500 when the PC could not save the file.

`GET /api/v1/files/{name}` answers 200, or 206 for a `Range` request, with
`application/octet-stream`, `Content-Disposition: attachment`, `Accept-Ranges: bytes` and
`Last-Modified`. 416 for a range past the end. 404 when no listed file has exactly that name
(letter case included). 409 while a program on the PC is still writing the file.

`DELETE /api/v1/files/{name}` answers `{ "deleted": true }`, 404, or 409 when Windows cannot
delete the file. The delete is permanent; the file does not go to the Recycle Bin.

A name for an upload must be 1–150 characters and a plain file name: none of
`\ / : * ? " < > |`, no control characters, no characters that switch the reading direction,
no white space at either end, no dot at the end, and not a Windows device name (`CON`, `NUL`,
`COM1` and the like). The host refuses other names with 400 and never repairs them. In the
other two routes, put the name in the address with `encodeURIComponent`.

### Host actions

`GET /api/v1/actions` lists what a device may ask the PC to do:

```json
{ "actions": [ { "id": "lock", "label": "Lock the PC" }, { "id": "sleep", "label": "Put the PC to sleep" } ] }
```

`POST /api/v1/actions/{id}` runs one. The body is ignored. It answers `{ "done": true }`, 404
for any other id (ids are exact and case-sensitive), or 500 with an `error` when Windows does
not lock the screen. `sleep` answers first and suspends the PC about 1.5 seconds later, so it
cannot report a refusal; while one sleep is pending, another request answers 200 and does
nothing.

### Steam

`GET /api/v1/steam`:

```json
{ "installed": true, "games": [
  { "id": 620, "name": "Portal 2", "hasArt": true, "lastPlayedUtc": "2026-09-21T14:13:20+00:00", "sizeBytes": 12345678901 },
  { "id": 1001, "name": "Never played", "hasArt": false, "lastPlayedUtc": null, "sizeBytes": 1000000 } ] }
```

`installed` is false, and `games` empty, when Steam is not installed on the PC. The list
holds the fully installed games, most recently played first, then by name; games never played
come last. `name` is at most 100 characters. The host reads the list from disk on every
request.

`GET /api/v1/steam/games/{id}/art` answers with the cover picture as Steam stored it. The
type comes from the file's first bytes. 404 when `{id}` is not plain digits, the game is not
in the list, or Steam has no usable cover; `hasArt` says which games have one.

`POST /api/v1/steam/games/{id}/launch` (no body) answers
`{ "launched": true, "name": "Portal 2" }` when Steam took the command. The game may not be on
screen yet: Steam can first show a window of its own. Anything that is not the number of a
listed game gets 404 `{ "error": "That game is not installed on this PC." }`. 503 with an
`error` when Steam cannot be started.

`POST /api/v1/steam/bigpicture` and `POST /api/v1/steam/bigpicture/close` (no body) answer
`{ "opened": true }` and `{ "closed": true }`, or 503 with an `error` when Steam is not
installed or cannot be started.

## WebSocket close codes

| Code | Meaning | Browser behavior |
| --- | --- | --- |
| 4001 | Device revoked, signed out or expired | Back to pairing |
| 4008 | Replaced by a newer connection from the same device | Stops; offer Reconnect |
| 4029 | Too many connections to the host | Retry with backoff |
| 4415 | Not supported on this PC (no hardware encoder, no audio device) | Game mode falls back to Desktop mode; sound turns off |
| 1012 | The host is stopping or restarting (a changed port or network mode, Quit), or the video pipeline restarted (for example the monitor resolution changed) | Reconnect |
| 1013 | Game mode cannot start while Windows blocks capture (lock screen, UAC) | Reconnect |
| 1007 / 1009 | Malformed or oversized message | Reconnect |

Limits per device: two video sockets (Desktop or Game mode), one input socket, one audio
socket; at most six of each kind on the host.

## Desktop mode video: `WSS /api/v1/stream`

Query: `display` (a monitor's `id`), `fps` 1–30 (default 18), `quality` 30–90 (default 85),
`width` 480–3840 (capped at the monitor's width; height keeps the aspect ratio).

The browser sends the text message `next` (or `full` to force a frame even if nothing
changed); the host answers each with exactly one message:

- binary: a JPEG of the monitor (mouse pointer included);
- text `{"type":"idle"}`: nothing changed for a second;
- text `{"type":"unavailable","message":"…"}`: Windows is blocking capture (lock screen, UAC).

Anything other than `next`/`full` closes the socket (1007).

## Game mode video: `WSS /api/v1/video`

Query: `display`, `fps` 10–120 (default 60), `height` 360–2160 (default 1080; never larger
than the monitor), `bitrate` in kbit/s 2,000–80,000 (default 20,000).

Host to browser:

- text `{"type":"config","codec":"avc1.64002a","description":"<base64 avcC>","width","height","desktopWidth","desktopHeight","encoder"}`
  before the first frame. Configure a WebCodecs `VideoDecoder` with `codec` and
  `description`.
- binary frame: a 12-byte header followed by H.264 in AVCC form (4-byte big-endian NAL
  lengths):

  | Bytes | Field |
  | --- | --- |
  | 0 | message type, 1 = video frame |
  | 1 | flags, bit 0 = keyframe |
  | 2–3 | reserved (0) |
  | 4–7 | frame id, uint32 little-endian |
  | 8–11 | capture time in ms since the stream started, uint32 little-endian |

- text `{"type":"cursor","x","y","visible"}`: mouse pointer position. `x`,`y` are the top-left
  of the pointer image in monitor pixels.
- text `{"type":"cursorImage","png":"<base64>","hotX","hotY","width","height"}`: a new
  pointer shape.
- text `{"type":"unavailable","message"}` / `{"type":"resumed"}`: capture blocked or back.
- text `{"type":"stats","fps","mbps","encodeMs","deliverMs"}` about once a second.
  `deliverMs` is the time from sending a frame until its `ack` came back.
- text `{"type":"bitrateUnsupported","message"}`: this encoder cannot change its bitrate while
  streaming. The bitrate stays as it was; stop sending `bitrate`.
- text `{"type":"restart","message"}` or `{"type":"error","message"}`: sent just before the
  host closes the socket with 1012.

Browser to host:

- `ack <frame id>` after a frame is decoded. The host starts with two credits and earns one
  back per ack of a frame it really sent, so at most two frames are ever in flight.
- `key`: make the next frame a keyframe (new decoder or a decode error).
- `bitrate <kbps>`: change the encoder's target bitrate without restarting the stream. The
  value must be 2,000–50,000. The host applies at most one change per second and ignores a
  message that comes sooner.

Anything else ends the stream, and so does a `bitrate` outside its range.

The browser client's **Automatic** bitrate uses this message. It starts the stream at 20,000
and reads `deliverMs` from each `stats` message that reports 5 fps or more. Above 80 ms it
asks for three quarters of the current bitrate, rounded down to a multiple of 500 and never
under 2,000. After eight readings in a row under 35 ms it asks for 1,000 more, up to 35,000.
After each change it skips the next two readings.

## Sound: `WSS /api/v1/audio`

The host first sends `{"type":"config","sampleRate":48000,"channels":2,"format":"s16le","chunkMs":10}`,
then binary chunks of 10 ms: 480 frames of interleaved stereo signed 16-bit little-endian PCM
(1,920 bytes). Chunks keep coming during silence. The browser sends nothing.

## Input: `WSS /api/v1/control`

UTF-8 JSON text messages of at most 8,192 bytes. Unknown kinds are ignored, numbers are
clamped, and a missing number counts as 0.

```json
{"kind":"pointer","display":0,"x":0.45,"y":0.62}
{"kind":"move","dx":12,"dy":-5}
{"kind":"mouse","button":0,"action":"down"}
{"kind":"wheel","delta":-120}
{"kind":"hwheel","delta":120}
{"kind":"key","code":"KeyA","action":"down"}
{"kind":"text","text":"Hello from Safari\n"}
{"kind":"gamepad","index":0,"buttons":4097,"lt":0,"rt":1,"lx":0.5,"ly":-1,"rx":0,"ry":0}
{"kind":"release"}
{"kind":"ping","t":1234.5}
```

- `pointer`: `x`,`y` are 0–1 within the chosen monitor. `move`: relative, ±4,000.
- `mouse`: buttons 0 left, 1 middle, 2 right, 3 back, 4 forward.
- `wheel`/`hwheel`: Windows wheel units (120 per notch), ±1,200.
- `key`: `code` is a `KeyboardEvent.code`; the host sends the matching PC/AT scan code, so the
  PC's keyboard layout decides the character. The client's special keys and shortcuts are
  ordinary `key` messages: a shortcut presses its keys in order and lets go in reverse order.
- `text`: up to 2,048 characters typed as Unicode; line breaks and tabs become real Enter and
  Tab presses.
- `gamepad`: W3C "standard" gamepad layout. `buttons` has one bit per standard button
  (0 A, 1 B, 2 X, 3 Y, 4 LB, 5 RB, 8 Back, 9 Start, 10 LS, 11 RS, 12–15 D-pad up/down/left/right,
  16 Guide); triggers 0–1; sticks −1–1 with Y down positive. `index` 0–3 picks the virtual
  controller. The client's on-screen gamepad sends the same message with `index` 0. Needs the
  ViGEmBus driver; otherwise the host replies once with `{"type":"notice","message":"…"}`.
- `release`: let go of every key, button and controller this connection holds (sent when the
  page loses focus; the host also does it on disconnect).
- `ping`: the host answers `{"type":"pong","t":…}` with the same `t`.

Host to browser, besides `pong` and `notice`:

- `{"type":"rumble","index":0,"strong":0.5,"weak":0.25}`: a game changed the vibration of
  virtual controller `index` (0–3). `strong` and `weak` are the large and the small motor,
  0–1; both 0 means stop. The host sends at most one about every 16 ms per controller. The
  browser client plays it on the physical controller with the same index, where the browser
  has a vibration actuator for it.

## Independent cursor feedback

An authenticated control client may send `{"kind":"cursor"}`. The reply is
`{"type":"cursorState","supported":true,"sequence":7,"x":960,"y":540,"visible":true,"image":null}`.
Coordinates are global Windows physical pixels. `image`, when the native cursor changes,
contains base64 `png`, `hotX`, `hotY`, `width`, and `height`. No screen capture is needed.
Polls less than 12 ms apart are ignored; the browser keeps one outstanding poll.
`pointer` optionally carries a nonnegative `sequence`; snapshots echo the greatest
processed pointer sequence on this socket, so local prediction can reject old feedback.
This acknowledges processing, not successful input into a protected Windows desktop.
A supported client may request `/api/v1/stream?...&cursor=separate` to omit the baked
cursor from JPEG frames. Without this parameter existing capture behavior is unchanged.
Watch-only clients may read cursor state but send no input. Auth and origin checks remain
the same as other control traffic. Animated cursor frames and XOR inversion are not
reproduced exactly; XOR cursor pixels use a white shape with a dark outline.
