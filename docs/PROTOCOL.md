# InnAware Support protocol notes

## HTTP API

Production traffic is expected to use HTTPS through Apache.

### Technician authentication and operations

- `POST /api/login`
- `POST /api/logout`
- `GET /api/me`
- `POST /api/account/password`
- `GET /api/dashboard/metrics`
- `GET /api/technicians`
- `GET /api/sessions`
- `GET /api/sessions/export`
- `POST /api/sessions`
- `GET /api/sessions/{id}`
- `POST /api/sessions/{id}/notes`
- `POST /api/sessions/{id}/end`

Administrators additionally use:

- `GET /api/admins`
- `POST /api/admins`
- `PATCH /api/admins/{id}`
- `POST /api/admins/{id}/password`
- `GET /api/admin-audit`

Technician authentication uses a MySQL-backed account and an HttpOnly, Secure, SameSite=Strict HMAC-signed browser cookie.

### Customer agent

- `POST /api/agent/lookup` — validate an unredeemed enrollment code and return technician/request metadata.
- `POST /api/agent/redeem` — atomically consume the code after consent and return the separate random live agent credential, WSS URL, and live-session deadline.
- `POST /api/agent/end` — bearer-token-authenticated customer termination. This clears the live token and ends the session server-side.

### Downloads / health

- `GET /api/health`
- `GET /api/download-status`
- `GET /download/windows`

## Session lifetime

A support session has two phases that use the same persisted `expires_at` field:

1. **Enrollment phase.** The 8-digit code expires after `SESSION_TTL_MINUTES` (15 minutes by default).
2. **Live phase.** Once the customer redeems that code, `expires_at` is replaced by the live-session deadline using `LIVE_SESSION_TTL_MINUTES` (480 minutes / 8 hours by default).

The enrollment code is single-use. It cannot be used to reconnect after redemption.

The separate random live token may reconnect only until the live deadline. The token is cleared when the customer or technician ends the session or when the live session expires.

After an application-server restart, previously connected sessions with a still-valid live token are normalized to `approved`, because no WebSocket can survive the process restart. The running customer agent can then reconnect using its existing live token.

## WebSockets

### Agent

`GET /ws/agent?session=<uuid>` with:

`Authorization: Bearer <256-bit-token>`

on the WebSocket upgrade request.

The token is generated only after consent and the database stores only its SHA-256 digest. It is carried in the Authorization header rather than the URL so it does not appear in ordinary Apache request logs.

Agent -> server/technician:

- Binary messages: complete JPEG screen frames.
- Text messages: hello/status/capture metadata.

Example hello:

```json
{
  "type": "hello",
  "machine_name": "FRONTDESK-PC",
  "elevated": false,
  "control": true,
  "monitors": [
    {
      "index": 0,
      "name": "\\\\.\\DISPLAY1",
      "width": 1920,
      "height": 1080,
      "primary": true
    }
  ],
  "active_monitor": 0,
  "jpeg_quality": 55,
  "fps": 6,
  "live_expires_at": "2026-09-19T08:00:00Z"
}
```

Capture-setting acknowledgement:

```json
{
  "type": "capture_settings",
  "active_monitor": 0,
  "jpeg_quality": 55,
  "fps": 6
}
```

The server also sends viewer-presence metadata to the agent:

```json
{"type":"viewer_status","connected":true}
```

When no technician viewer is attached, the customer agent keeps the session/WebSocket alive but pauses JPEG capture and upload. Capture resumes when a technician viewer attaches.

### Technician

`GET /ws/tech?session=<uuid>`

Authentication is the technician browser session cookie.

Technician -> agent input example:

```json
{
  "type": "input",
  "input": {
    "kind": "mouse_move",
    "x": 0.42,
    "y": 0.31
  }
}
```

Input kinds are:

- `mouse_move`
- `mouse_button`
- `mouse_wheel`
- `key`

Mouse coordinates are normalized from 0.0 to 1.0 against the currently selected monitor.

The broker drops technician `input` messages when the session was created without keyboard/mouse control permission.

Capture settings can be changed independently of control permission:

```json
{
  "type": "capture_settings",
  "monitor": 1,
  "jpeg_quality": 70,
  "fps": 8
}
```

The Windows agent clamps:

- monitor index to an available display;
- JPEG quality to 25-85;
- FPS to 1-12.

## Reconnection

The Windows agent performs bounded automatic reconnect attempts after transient WebSocket/network failure:

- 1 second
- 2 seconds
- 4 seconds
- 8 seconds
- 10 seconds

The same live token is reused. Reconnection stops when:

- the customer explicitly ends the session;
- the server closes the socket with a normal `session ended` close reason;
- the live deadline has passed;
- all reconnect attempts fail.

## Capture implementation

Phase 2 still uses `Graphics.CopyFromScreen` + JPEG for compatibility and simplicity. It now supports:

- selectable monitors;
- adjustable JPEG quality;
- adjustable FPS;
- capture pause when no viewer is attached;
- browser-side effective FPS and payload-bandwidth telemetry.

DXGI Desktop Duplication and hardware video encoding remain the next major performance architecture change.

## Input implementation

Windows input uses `SendInput`.

Phase 2 expands keyboard support to include:

- letters and number row;
- F1-F24;
- arrows/navigation keys;
- left/right Shift, Control, Alt, Windows keys;
- OEM punctuation keys;
- numeric keypad;
- lock keys and Print Screen.

Secure attention (Ctrl+Alt+Del) and Windows secure-desktop control remain intentionally unsupported.
