#!/usr/bin/env bash
set -Eeuo pipefail
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
[[ $EUID -eq 0 ]] || { echo "Run as root." >&2; exit 1; }

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
APP_ROOT=/opt/innaware-support
BIN="$APP_ROOT/bin/innaware-support-server"
NEW_BIN="$APP_ROOT/bin/innaware-support-server.new"
DOWNLOAD="$APP_ROOT/downloads/InnAwareSupport.exe"
STAMP="$(date -u +%Y%m%d-%H%M%S)"
BACKUP="/root/innaware-support-update-$STAMP"

mkdir -m 0700 "$BACKUP"
cd "$REPO_ROOT"

git rev-parse HEAD > "$BACKUP/previous-repo-commit.txt"
git pull --ff-only
git rev-parse HEAD > "$BACKUP/target-repo-commit.txt"

echo "Validating target source..."
go mod download
go test ./...
go vet ./...
node --check internal/webui/web/app.js
go build -trimpath -ldflags='-s -w' -o "$NEW_BIN" ./cmd/server
chmod 0755 "$NEW_BIN"

if [[ -x "$BIN" ]]; then
  cp -a "$BIN" "$BACKUP/innaware-support-server.previous"
fi

rollback_server() {
  echo "New server failed its local health check. Rolling back the previous binary..." >&2
  if [[ -x "$BACKUP/innaware-support-server.previous" ]]; then
    install -m 0755 "$BACKUP/innaware-support-server.previous" "$BIN"
    systemctl restart innaware-support.service
    for _ in {1..20}; do
      if curl -fsS http://127.0.0.1:8787/api/health >/dev/null; then
        echo "Rollback succeeded. Previous server is healthy." >&2
        return 0
      fi
      sleep 1
    done
    echo "CRITICAL: rollback binary also failed health check." >&2
  else
    echo "CRITICAL: no previous server binary was available for rollback." >&2
  fi
  return 1
}

mv -f "$NEW_BIN" "$BIN"
if ! systemctl restart innaware-support.service; then
  rollback_server || true
  exit 1
fi

healthy=0
for _ in {1..30}; do
  if curl -fsS http://127.0.0.1:8787/api/health >/dev/null; then
    healthy=1
    break
  fi
  sleep 1
done
if [[ $healthy -ne 1 ]]; then
  journalctl -u innaware-support.service -n 100 --no-pager >&2 || true
  rollback_server || true
  exit 1
fi

echo "New server is healthy. Refreshing the Windows preview agent..."
AGENT_URL="${AGENT_URL:-https://github.com/MusicCityTelecom/innaware-support/releases/download/mvp-latest/InnAware-Remote-Support.exe}"
TMP_AGENT="$(mktemp "$APP_ROOT/downloads/.InnAwareSupport.exe.XXXXXX")"
if curl --fail --location --retry 3 --connect-timeout 15 --max-time 300 "$AGENT_URL" -o "$TMP_AGENT"; then
  python3 - "$TMP_AGENT" <<'PY_AGENT'
import hashlib
import pathlib
import sys

p = pathlib.Path(sys.argv[1])
data = p.read_bytes()
if len(data) < 100_000 or not data.startswith(b'MZ'):
    raise SystemExit('Downloaded Windows agent is not a plausible PE executable')
print("Windows agent SHA-256:", hashlib.sha256(data).hexdigest())
PY_AGENT
  chmod 0644 "$TMP_AGENT"
  mv -f "$TMP_AGENT" "$DOWNLOAD"
else
  rm -f "$TMP_AGENT"
  echo "WARNING: Could not refresh the Windows agent preview build; keeping the currently published copy." >&2
fi

apache2ctl configtest
curl -fsS "${PUBLIC_BASE_URL:-https://remote.innawareucp.com}/api/health"
echo
echo "Update complete."
echo "Backup/rollback evidence: $BACKUP"
echo "Deployed commit: $(git rev-parse HEAD)"
