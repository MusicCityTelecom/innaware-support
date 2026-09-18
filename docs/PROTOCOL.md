# MVP protocol notes

## HTTP API

All production traffic is expected to use HTTPS through Apache.

### Technician

- `POST /api/login`
- `POST /api/logout`
- `GET /api/me`
- `GET /api/sessions`
- `POST /api/sessions`
- `GET /api/sessions/{id}`
- `POST /api/sessions/{id}/end`

Technician authentication uses an HttpOnly, Secure, SameSite=Strict HMAC-signed cookie. The initial release is deliberately single-technician-account; credentials are supplied by root-only environment configuration.

### Customer agent

- `POST /api/agent/lookup` — validate an unredeemed code and return technician/request metadata.
- `POST /api/agent/redeem` — atomically consume the code after consent and return the separate random agent credential and WSS URL.

### Downloads / health

- `GET /api/health`
- `GET /api/download-status`
- `GET /download/windows`

## WebSockets

### Agent

`GET /ws/agent?session=<uuid>` with `Authorization: Bearer <256-bit-token>` on the WebSocket upgrade request.

The token is generated only after consent and the database stores only its SHA-256 digest. It is deliberately carried in an authorization header rather than the URL so it is not exposed in ordinary Apache request logs.

Agent -> server/technician:

- Binary messages: complete JPEG screen frames.
- Text messages: status metadata. Initial message is currently:

```json
{"type":"hello","machine_name":"FRONTDESK-PC","elevated":false,"control":true}
```

### Technician

`GET /ws/tech?session=<uuid>`

Authentication is the technician session cookie.

Technician -> server/agent input example:

```json
{
  "type":"input",
  "input":{"kind":"mouse_move","x":0.42,"y":0.31}
}
```

Other MVP input kinds are `mouse_button`, `mouse_wheel`, and `key`. Mouse coordinates are normalized from 0.0 to 1.0.

The broker drops technician `input` messages when the session was created without keyboard/mouse control permission.
