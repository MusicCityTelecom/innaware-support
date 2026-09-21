# InnAware Support

InnAware Support is a self-hosted **attended remote-support** system for TechFinity / InnAware. A customer downloads a temporary Windows support application, enters a short-lived code supplied by a technician, reviews the requested permissions, accepts the session, and then establishes an outbound encrypted connection to the InnAware support server.

The project is intentionally session-oriented rather than device/password-oriented. The initial MVP does **not** install unattended access or depend on an existing RustDesk/TeamViewer/AnyDesk installation.

> **Status: functional attended-support beta preview / security review and code signing still required before broad production use.** Real end-to-end browser sessions have passed remote screen/control, DXGI/GDI capture switching, clipboard, bidirectional file transfer, reconnect/revocation, fullscreen, and customer-to-technician transfer acceptance. Current main also includes negotiated H.264 with JPEG fallback, composite multi-monitor viewing, chat/image chat, local recording, network diagnostics, customer-approved elevation, focused pop-out viewing, and local technician screenshots. GitHub Actions builds the Linux broker, customer Windows agent, native technician portable package, and technician installer.

## Customer workflow

```text
Technician creates session
          |
          v
    8-digit code
          |
          v
Customer runs InnAware-Remote-Support.exe
          |
          v
 Enters code + reviews technician/access
          |
          v
      Accepts terms
          |
          v
Server consumes code once and issues random 256-bit live token
          |
          v
Customer agent opens outbound WSS connection
          |
          v
Remote desktop appears in technician browser
```

The short code is **not** the live remote-control key. It is single-use enrollment. After consent, the server generates a separate random credential and stores only its SHA-256 hash.

## MVP components

- **Go server/broker** — HTTPS API behind Apache, technician sessions, short-code enrollment, MySQL persistence, WebSocket relay.
- **Technician web console** — live sessions, searchable history, metrics, notes/timeline, CSV export, remote viewer/control, detachable focused viewer, local PNG screenshots, recording, chat/image chat, file/clipboard tools, network diagnostics, on-demand elevation request, team management, and administrative audit.
- **Native Windows technician app** — .NET 8/WebView2 client using the same web authentication/session mechanism, packaged as both a portable ZIP and Windows installer. Its native overlay exposes monitor selection, capture mode, video transport, resolution, quality, FPS, detach, Fit/1:1, screenshot, tools/sidebar control, recording, chat, files, network refresh, elevation, fullscreen, and always-on-top controls.
- **Windows customer agent** — .NET 8 WinForms single-file executable with explicit terms/consent, DXGI/GDI screen capture, cursor capture, keyboard/mouse input, chat/image chat, file transfer, clipboard, network diagnostics, reconnect/revocation, and customer-approved on-demand UAC elevation restart.
- **Apache deployment** — existing TLS termination on `remote.innawareucp.com`; Go service stays on `127.0.0.1:8787`.
- **MySQL/MariaDB** — sessions and audit events. Live screen frames are not intentionally persisted.

The first milestone relays all frames through the VPS for predictable NAT/CGNAT behavior. P2P/ICE/STUN/TURN is a later optimization.

## Current limitations

- individual monitor selection plus a composite **All monitors** virtual-desktop view are supported; independent simultaneous per-monitor streams/windows are not implemented yet;
- JPEG remains the default production-safe frame transport; negotiated H.264/Annex-B transport is implemented as an optional beta path with automatic JPEG fallback and still requires broader hardware/driver acceptance testing;
- Windows secure-desktop/UAC prompt control and Ctrl+Alt+Del injection are intentionally unsupported;
- no permanent service/unattended customer access;
- built-in `admin` and `technician` roles exist, but MFA/SSO and a finer-grained permission matrix are still future work;
- customer and technician Windows releases are not Authenticode-signed yet; Azure Artifact Signing setup is planned;
- technician-side recording is currently local WebM canvas recording rather than centrally retained server recording.

These are deliberate boundaries. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), [docs/OPERATIONS.md](docs/OPERATIONS.md), and [docs/SECURITY.md](docs/SECURITY.md). Before widening field use, run the [beta acceptance checklist](docs/ACCEPTANCE-TEST.md).

## VPS deployment

The supplied installer is designed for the current `vultr01` style deployment: Ubuntu 24.04/26.04, Apache, MySQL/MariaDB, an existing Let's Encrypt certificate for `remote.innawareucp.com`, and root execution.

It **does not modify UFW and does not stop the existing RustDesk services**. The custom support system uses only HTTPS/WSS on TCP 443, so both systems can coexist during acceptance testing.

```bash
cd /opt
git clone https://github.com/MusicCityTelecom/innaware-support.git
cd innaware-support
sudo bash deploy/install-vps.sh
```

The installer:

1. installs Git/Go from Ubuntu repositories if they are missing, then runs Go tests/vet and builds the server;
2. creates the `innaware_support` MySQL database/user if this is the first install;
3. creates root-only application secrets and initial technician credentials;
4. installs `innaware-support.service` listening only on `127.0.0.1:8787`;
5. backs up and replaces the InnAware-managed Apache vhost for `remote.innawareucp.com`;
6. reuses the existing Let's Encrypt certificate;
7. proxies `/ws/` as WebSocket and all other paths as HTTPS to the Go service;
8. downloads the latest successful `main` Windows-agent preview release when available;
9. leaves RustDesk and UFW unchanged.

After installation:

```bash
systemctl status innaware-support.service --no-pager -l
curl -sS https://remote.innawareucp.com/api/health
journalctl -u innaware-support.service -f
```

The installer-generated username/password are a **bootstrap credential**. On the first start of the multi-user console, they seed the first MySQL administrator only when the administrator table is empty. They are stored root-only in:

```text
/etc/innaware-support-app/app.env
```

That file is mode `0600`. After the first database administrator exists, browser authentication uses the MySQL account records; support users and password changes are managed in the console. Changing `TECH_PASSWORD` later does not overwrite an existing database account.

### Update the VPS

From the checked-out repository:

```bash
sudo bash deploy/update-vps.sh
```

## Build the Windows agent

GitHub Actions builds the Windows x64 customer agent and native technician client on every push to `main`. A successful `main` build refreshes the prerelease tag `mvp-latest`. The current beta identity is `0.9.0-preview`.

Published Windows artifacts:

```text
InnAware-Remote-Support.exe
InnAware-Support-Technician-Portable.zip
InnAware-Support-Technician-Setup.exe
```

For a local Windows build with .NET 8 SDK:

```powershell
dotnet publish agent/InnAwareSupport.Agent.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -o dist/agent
```

Before production distribution, digitally sign the executable. Do not train users to ignore Windows publisher warnings.

The rolling preview executable is published at `mvp-latest`. If you need to publish a locally signed or otherwise approved build instead, copy it to the VPS and run:

```bash
sudo bash deploy/publish-agent.sh /path/to/InnAwareSupport.exe
```

Customers can then download it from:

```text
https://remote.innawareucp.com/download/windows
```

## Development server configuration

Required environment variables:

```text
MYSQL_DSN=innaware_support:password@tcp(127.0.0.1:3306)/innaware_support?parseTime=true&loc=UTC&charset=utf8mb4
TECH_USERNAME=admin
TECH_PASSWORD=a-long-random-password
COOKIE_SECRET=<at least 32 random bytes, hex>
CODE_SECRET=<at least 32 random bytes, hex>
```

Optional:

```text
LISTEN_ADDR=127.0.0.1:8787
PUBLIC_BASE_URL=https://remote.innawareucp.com
SESSION_TTL_MINUTES=15
LIVE_SESSION_TTL_MINUTES=480
AGENT_DOWNLOAD_PATH=/opt/innaware-support/downloads/InnAwareSupport.exe
TRUST_PROXY=true
```

Generate secrets with:

```bash
openssl rand -hex 32
```

Then:

```bash
go mod download
go test ./...
go run ./cmd/server
```

## Repository layout

```text
cmd/server/                Go server entry point
internal/app/              API, auth, MySQL store, WebSocket broker
internal/webui/web/        Embedded customer/technician website
agent/                     Windows WinForms customer agent
technician/                Native Windows technician client + installer definition
deploy/                    VPS install/update/agent publishing scripts
docs/                      Architecture, protocol, security notes
.github/workflows/          Linux server + customer/technician Windows CI/release builds
```

## Security

Remote-control software is security-sensitive. The MVP deliberately keeps the trust model narrow: explicit customer consent, single-use enrollment, random bounded-lifetime live credentials, customer/technician server-side revocation, no unattended password, HTTPS/WSS, loopback-only application server, and no secure-desktop bypass.

Before broad customer deployment, complete code signing, MFA/SSO, finer-grained RBAC, dependency/release signing, independent review, and real-network acceptance testing. See [docs/SECURITY.md](docs/SECURITY.md).

## License

MIT. See [LICENSE](LICENSE).
