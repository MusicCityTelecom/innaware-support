# Operations console

The InnAware Support web console is the technician and administrative surface for attended remote-support operations.

## Roles

### Technician

Technicians can:

- sign in and change their own password;
- create attended support sessions;
- request view/control and optional customer-approved elevation;
- open the browser remote viewer;
- end sessions;
- search support history;
- review session timelines;
- add technician notes;
- export filtered session history to CSV.

### Administrator

Administrators have all technician capabilities plus:

- add support users;
- assign the `admin` or `technician` role;
- disable and re-enable accounts;
- reset another user's password;
- review the global administrative audit log.

The system refuses to deactivate or demote the last active administrator. An administrator also cannot deactivate or demote their own currently authenticated account through the API.

## Authentication lifecycle

On an existing installation upgraded from the original single-user MVP:

1. The application runs additive schema migrations.
2. If `support_admins` is empty, the current root-only `TECH_USERNAME` / `TECH_PASSWORD` values seed the first MySQL administrator.
3. Once any administrator exists, the environment credential is no longer used for browser login.
4. New accounts and password changes are stored in MySQL.
5. Passwords are PBKDF2-HMAC-SHA256 hashes with per-user random salts.
6. Signed browser cookies include an account authentication version.
7. Password resets, role changes, and account disable/enable changes invalidate previously issued cookies on their next authenticated request.

For continuity, create at least two active administrator accounts before relying on the service for production support.

## Console sections

### Live Sessions

Shows waiting, approved, and connected sessions along with:

- customer/property label;
- machine name when known;
- assigned technician;
- status;
- creation time;
- final four digits of the enrollment code.

Dashboard counters show waiting/approved sessions, currently connected sessions, sessions created today, completed sessions during the past seven days, and average completed support-session duration.

### History

History can be filtered by:

- free-text search over customer/property, machine, technician, and code suffix;
- status;
- technician;
- creation date range.

The browser displays paginated history and can export the current filters to CSV. The export endpoint currently caps a single export at 500 sessions.

### Session detail

A historical or live session can be opened to review:

- current status;
- technician;
- customer machine;
- requested permissions;
- created/connected/ended timestamps;
- calculated duration;
- event timeline;
- technician notes.

Ended and expired sessions open in review mode. Active sessions additionally open the WebSocket remote viewer.

### Team

Administrators can create, edit, disable, re-enable, promote, or demote users and reset passwords. Accounts are retained rather than deleted so historical attribution remains understandable.

### Audit

The administrative audit records recent:

- successful and failed sign-ins;
- sign-outs;
- administrator creation/update;
- password resets and self-service password changes;
- session creation/termination;
- technician notes;
- history exports.

This audit is separate from each support session's event timeline.

## Database additions

The operations-console upgrade adds these objects without deleting existing support data:

- `support_admins`;
- `support_admin_audit`;
- `support_session_notes`;
- nullable `support_sessions.technician_id`.

Existing `technician_name` values remain in place for historical readability.

## Updating vultr01

The VPS updater validates Go tests/vet/build before touching the running binary. It saves the previous server binary under a root-only timestamped update directory and automatically restores it if the replacement fails the loopback health check.

From the checked-out repository:

```bash
cd /opt/innaware-support
sudo bash deploy/update-vps.sh
```

The update script does not modify UFW, Apache virtual-host configuration, the existing RustDesk services, or the root-only application environment file.

After an update:

```bash
systemctl status innaware-support.service --no-pager -l
curl -fsS http://127.0.0.1:8787/api/health
curl -fsS https://remote.innawareucp.com/api/health
journalctl -u innaware-support.service -n 100 --no-pager
```

## Backup considerations

Before major database changes or production rollout, include the `innaware_support` MySQL database in the normal VPS backup process. The database contains support history, account password hashes, session notes, and audit history, but it does not intentionally contain screen-frame recordings.

The root-only environment file remains:

```text
/etc/innaware-support-app/app.env
```

It contains database and application secrets and should be backed up only to protected storage.
