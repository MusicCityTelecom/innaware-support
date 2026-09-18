#!/usr/bin/env bash
set -Eeuo pipefail
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
[[ $EUID -eq 0 ]] || { echo "Run as root." >&2; exit 1; }
REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"
git pull --ff-only
go mod download
go test ./...
go vet ./...
go build -trimpath -ldflags='-s -w' -o /opt/innaware-support/bin/innaware-support-server.new ./cmd/server
chmod 0755 /opt/innaware-support/bin/innaware-support-server.new
mv -f /opt/innaware-support/bin/innaware-support-server.new /opt/innaware-support/bin/innaware-support-server

AGENT_URL="${AGENT_URL:-https://github.com/MusicCityTelecom/innaware-support/releases/download/mvp-latest/InnAware-Remote-Support.exe}"
TMP_AGENT="$(mktemp /opt/innaware-support/downloads/.InnAwareSupport.exe.XXXXXX)"
if curl --fail --location --retry 3 --connect-timeout 15 --max-time 300 "$AGENT_URL" -o "$TMP_AGENT"; then
  python3 - "$TMP_AGENT" <<'PY_AGENT'
import pathlib, sys
p = pathlib.Path(sys.argv[1])
data = p.read_bytes()
if len(data) < 100_000 or not data.startswith(b'MZ'):
    raise SystemExit('Downloaded Windows agent is not a plausible PE executable')
PY_AGENT
  chmod 0644 "$TMP_AGENT"
  mv -f "$TMP_AGENT" /opt/innaware-support/downloads/InnAwareSupport.exe
else
  rm -f "$TMP_AGENT"
  echo "WARNING: Could not refresh the Windows agent preview build; keeping the currently published copy." >&2
fi
systemctl restart innaware-support.service
for i in {1..20}; do curl -fsS http://127.0.0.1:8787/api/health >/dev/null && break; sleep 1; done
curl -fsS http://127.0.0.1:8787/api/health >/dev/null
apache2ctl configtest
curl -fsS "${PUBLIC_BASE_URL:-https://remote.innawareucp.com}/api/health"
echo "Update complete."
