# Abyss Remote R3.3 API contract

Version: `v1` / application `0.3.3`.

The Windows host serves HTTPS on port `8443` and an API under `/api/v1/`. The application uses native Windows services, not external remote desktop APIs. JSON bodies must use `Content-Type: application/json`. Static browser assets and requests are same-origin only. The remote control channels require a paired `abyss_remote_session` Secure/HttpOnly/SameSite=Strict cookie.

## Authentication

1. On the PC's console, press `P` to generate an 8-digit code valid for 10 minutes and one success. Codes are rate-limited to 5 attempts per client address in 5 minutes.
2. From the browser, `POST /api/v1/pair` with `{"code":"01234567", "label":"My iPhone"}`. The server creates a random 256-bit secret, stores only its SHA-256 hash and sets a secure session cookie. Only same-Origin POST requests accepted.
3. `GET /api/v1/me` returns `{"id":"…","device":"…","csrf":"64_HEX_DIGITS","version":"0.3.3"}`. CSRF material is session-bound and regenerated when the host restarts.
4. All mutating authenticated REST requests add header `X-Abyss-CSRF: <csrf>` and must have a matching `Origin` header. WebSocket input includes protocols `abyss-v1`, `<csrf>`, where the server accepts `abyss-v1`.
5. `POST /api/v1/logout` immediately revokes the current paired device token and clears the cookie.

## REST

| Route | Auth required | Body / result |
| --- | --- | --- |
| `GET /api/v1/info` | No | App version; listening mode |
| `POST /api/v1/pair` | Physical PIN | `{code,label}`; sets cookie |
| `GET /api/v1/me` | Yes | Device ID and CSRF |
| `GET /api/v1/displays` | Yes | Array of `{id,name,x,y,width,height,primary}` |
| `GET /api/v1/devices` | Yes | Array of paired device descriptions (no secrets) |
| `DELETE /api/v1/devices/{id}` | Yes + CSRF | Revoke paired device |
| `POST /api/v1/logout` | Yes + CSRF | Revoke this device |
| `GET /api/v1/health` | Yes | Health and host UTC time |
| `GET /api/v1/clipboard` | Yes | `{text}` (first 4,096 characters) |
| `POST /api/v1/clipboard` | Yes + CSRF | `{text}` to replace Windows text clipboard (max 4,096) |

## Screen stream (R3+ client-paced protocol)

`WSS /api/v1/stream?display=0&fps=18&quality=58&width=1280`

- Paired cookie and strict Origin check are mandatory. Maximum two concurrent streams, additional requests rejected with HTTP 429.
- **Changed in 0.3.0:** The socket is *client-paced*: after opening it, the browser sends a complete **UTF-8 text WebSocket message** containing `next` (four lowercase bytes). The server responds with one complete binary JPEG frame. After decoding/drawing it, the client sends another `next`. Invalid requests end the stream. This caps in-flight images and prevents old frames building up on slower devices. A R2.1 browser **cannot** use the R3 streaming socket without this message: update both host and UI together.
- Maximum requested FPS `1..24` (actual throughput depends on host capture, encoding, browser decoding and network). Quality `30..85`, width `480..2560`. Width is capped by the monitor width and height tracks its aspect ratio.
- Changing display, FPS or quality opens a new video connection; input remains on a separate authenticated channel. Fit/Fill/Stretch and Zoom are browser-only display transformations and do **not** change cursor coordinates or physical monitor resolution.
- Per-viewer Windows GDI capture buffers are reused between frames, but this is **still CPU-based JPEG streaming**; it does not guarantee 24 FPS on high-resolution displays, HDR or games.

## Input channel

`WSS /api/v1/control`

- Requires paired cookie, strict Origin, and requested WebSocket protocols `abyss-v1,<csrf>`.
- UTF-8 JSON messages (max 8,192 bytes). Unknown event types are ignored.
- At most 2 simultaneous control sockets.

Example events:

```json
{"kind":"pointer","display":0,"x":0.45,"y":0.62}
```

Coordinates are normalized `[0,1]` within the selected display. Sender must convert local canvas touches into proportions of the displayed frame.

```json
{"kind":"move","dx":12,"dy":-5}
{"kind":"mouse","button":0,"action":"down"}
{"kind":"mouse","button":0,"action":"up"}
{"kind":"wheel","delta":-120}
{"kind":"key","code":"KeyA","action":"down"}
{"kind":"key","code":"KeyA","action":"up"}
{"kind":"text","text":"Hello from Safari"}
```

- Mouse buttons: `0=left`, `1=middle`, `2=right`.
- `wheel` delta uses Windows wheel unit convention, clamped to ±1200.
- Supported key codes include `KeyA..KeyZ`, `Digit0..Digit9`, `F1..F12`, arrow keys, Escape, Backspace, Tab, Enter, modifier keys, common punctuation and navigation keys. Not all keyboard layouts have been tested.
- `text` simulates a series of Unicode UTF-16 code-unit key events and is limited to 2,048 characters per event.
- On control WebSocket disconnect, the server attempts to release held mouse buttons.

## Wire stability / evolution

R3.3 remains intentionally small and versioned at `/api/v1`. Future encoders, authentication, relay transport and native clients should be added behind interfaces and/or new versioned endpoints without altering the existing v1 wire types without a version bump. The client must tolerate unknown fields.

## Browser viewport / mobile controls

Fit preserves the entire desktop image with letterboxing; Fill crops the image to cover the stage; Stretch distorts the aspect ratio; Zoom increases the rendered dimensions and allows panning with two touches. All input uses the canvas' actual rendered rectangle (rather than the overall stage). Direct touch taps to click, hold to begin drag, or use trackpad mode. Full screen uses CSS immersive fallback for iOS Safari, which may retain system/browser UI.
