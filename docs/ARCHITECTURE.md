# InnAware Support architecture

## Objective

InnAware Support is an attended remote-support system. The customer starts a small Windows application, enters a short-lived session code supplied by an authenticated technician, reviews the requested access, and explicitly approves the connection. The customer agent initiates all network connections outbound.

## MVP topology

```text
Customer Windows agent
  | HTTPS: lookup/redeem
  | WSS: JPEG frames + input events
  v
Apache :443 on remote.innawareucp.com
  |
  v
Go support service :8787 (loopback only)
  |-- session API
  |-- technician authentication
  |-- WebSocket broker/relay
  |-- embedded web console
  v
MySQL / MariaDB

Technician browser
  | HTTPS/WSS
  +--------------------> same Apache endpoint
```

The first milestone intentionally relays sessions through the VPS. This provides predictable behavior through NAT, CGNAT, hotel networks, and mobile hotspots. P2P/ICE/STUN/TURN is a later optimization, not a prerequisite for functional support.

## Session security model

1. Technician authenticates to the web console.
2. Technician creates a session. The server generates an 8-digit enrollment code that expires after 15 minutes by default.
3. The database stores an HMAC of the numeric code, not the code itself. The full code is returned only in the create-session response.
4. Customer agent uses the code to look up non-secret session metadata and show the technician identity and requested permissions.
5. Customer explicitly accepts the terms and permission request.
6. The code is atomically redeemed once. The server generates a separate 256-bit random agent credential and stores only its SHA-256 hash.
7. The agent uses that credential to authenticate its WebSocket.
8. Ending the session revokes the credential and closes both live WebSockets.

The numeric code is therefore an enrollment mechanism, not the live transport secret.

## Current capture/control implementation

The Windows agent uses `Graphics.CopyFromScreen` and JPEG encoding for the MVP. This is deliberately simple and functional rather than optimized. The next performance milestone should replace it with DXGI Desktop Duplication and hardware-assisted video encoding.

Keyboard and mouse events are sent from the technician browser as structured JSON and applied with the Windows `SendInput` API. The server enforces whether keyboard/mouse control was requested for the session.

## Elevation

The customer agent normally runs with standard user rights. A session may indicate that elevation could be necessary. The app can restart itself with the Windows `runas` verb, which causes the normal local UAC consent prompt. The customer must approve that prompt.

The MVP does **not** bypass, automate, capture, or control the Windows secure desktop. Full secure-desktop support requires a more privileged architecture (for example a carefully designed signed service/UIAccess helper) and is intentionally out of scope until the basic attended-support path is validated.

## Deliberate non-features in the MVP

- no unattended access password;
- no permanent Windows service installation;
- no screen recording on the VPS;
- no file transfer;
- no clipboard synchronization;
- no Ctrl+Alt+Del / secure-attention injection;
- no multi-monitor selector;
- no P2P/ICE NAT traversal;
- no multi-technician takeover/transfer.

These boundaries reduce the initial attack surface and keep customer consent unambiguous.
