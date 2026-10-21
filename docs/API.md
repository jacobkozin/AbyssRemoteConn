# API

Version `v1`, application 1.0.0. The host serves HTTPS on port 8444 (`--port` to change). All
routes are under `/api/v1/`. JSON bodies need `Content-Type: application/json`. Clients must
tolerate unknown fields; new behavior that would break existing clients belongs under a new
version.

## Authentication

1. Pairing code: shown in the host console (press `P` there for a new one). 8 digits, valid 10
   minutes, works once. Limits: 5 tries per address per 5 minutes; 20 wrong tries in total
   destroy the code.
2. `POST /api/v1/pair` with `{"code":"01234567","label":"My iPhone"}` and a same-origin
   `Origin` header. On success the host sets the cookie
   `abyss_remote_session` (`Secure; HttpOnly; SameSite=Strict; Path=/`, 30 days).
3. `GET /api/v1/me` returns `{ id, device, csrf, version, features: { gameMode } }`. The CSRF
   token changes every time the host starts; fetch it again after reconnecting.
4. State-changing requests send header `X-Abyss-CSRF: <csrf>` and a matching `Origin`.
5. Every WebSocket requests the subprotocols `abyss-v1, <csrf>` and needs the cookie and a
   matching `Origin`; the host accepts `abyss-v1`. A failed check is a plain HTTP 403 (the
   browser only sees the socket fail; call `/api/v1/me` to tell 401 from other problems).

## REST

| Route | Needs | Result |
| --- | --- | --- |
| `GET /api/v1/info` | nothing | `{ app, version }` |
| `POST /api/v1/pair` | Origin | `{ paired, name }`, sets the cookie. 400 wrong/expired, 429 too many tries |
| `GET /api/v1/me` | cookie | `{ id, device, csrf, version, features }` |
| `GET /api/v1/displays` | cookie | `[{ id, name, x, y, width, height, primary }]` in physical pixels |
| `GET /api/v1/devices` | cookie | `[{ id, name, createdUtc, lastSeenUtc, expiresUtc }]` (no secrets) |
| `DELETE /api/v1/devices/{id}` | cookie + Origin + CSRF | Revokes the device and closes its sockets |
| `POST /api/v1/logout` | cookie + Origin + CSRF | Revokes this device |
| `GET /api/v1/health` | cookie | `{ healthy, time }` |
| `GET /api/v1/clipboard` | cookie | `{ text }` (first 4,096 characters of the PC's text clipboard) |
| `POST /api/v1/clipboard` | cookie + Origin + CSRF | `{ text }` (at most 4,096 characters) replaces the PC's text clipboard |

Every response carries a strict Content-Security-Policy, `X-Frame-Options: DENY`,
`nosniff`, `no-referrer` and `Cache-Control: no-store`. Requests naming an unknown host get
421.

## WebSocket close codes

| Code | Meaning | Browser behavior |
| --- | --- | --- |
| 4001 | Device revoked, signed out or expired | Back to pairing |
| 4008 | Replaced by a newer connection from the same device | Stops; offer Reconnect |
| 4029 | Too many connections to the host | Retry with backoff |
| 4415 | Not supported on this PC (no hardware encoder, no audio device) | Game mode falls back to Desktop mode; sound turns off |
| 1012 | Video pipeline restarted (for example the monitor resolution changed) | Reconnect |
| 1007 / 1009 | Malformed or oversized message | Reconnect |

Limits per device: two video sockets (Desktop or Game mode), one input socket, one audio
socket; at most six of each kind on the host.

## Desktop mode video: `WSS /api/v1/stream`

Query: `display` (index), `fps` 1–30 (default 18), `quality` 30–90 (default 85), `width`
480–3840 (capped at the monitor's width; height keeps the aspect ratio).

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

Browser to host:

- `ack <frame id>` after a frame is decoded. The host starts with two credits and earns one
  back per ack of a frame it really sent, so at most two frames are ever in flight.
- `key`: make the next frame a keyframe (new decoder or a decode error).

Anything else ends the stream.

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
  PC's keyboard layout decides the character.
- `text`: up to 2,048 characters typed as Unicode; line breaks and tabs become real Enter and
  Tab presses.
- `gamepad`: W3C "standard" gamepad layout. `buttons` has one bit per standard button
  (0 A, 1 B, 2 X, 3 Y, 4 LB, 5 RB, 8 Back, 9 Start, 10 LS, 11 RS, 12–15 D-pad up/down/left/right,
  16 Guide); triggers 0–1; sticks −1–1 with Y down positive. `index` 0–3 picks the virtual
  controller. Needs the ViGEmBus driver; otherwise the host replies once with
  `{"type":"notice","message":"…"}`.
- `release`: let go of every key, button and controller this connection holds (sent when the
  page loses focus; the host also does it on disconnect).
- `ping`: the host answers `{"type":"pong","t":…}` with the same `t`.
