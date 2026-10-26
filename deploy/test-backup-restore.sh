#!/usr/bin/env bash
# Hosting Admin — backup/restore round-trip test (increment 3, TA18).
#
# Exercises deploy/backup.sh and deploy/restore.sh against a scratch SQLite
# database: backup → corrupt the live DB → restore → assert the round trip.
# Asserts the security-critical restore semantics: the sessions table is
# pruned (no resurrected sessions) while audit rows survive.
#
# Runs in CI (ubuntu, sqlite3 preinstalled) and locally. Uses --no-systemd:
# the real host procedure (systemctl + health check) cannot run in CI.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

DB="$WORK/hostingadmin.db"
BACKUP_DIR="$WORK/backups"

fail() { echo "FAIL: $*" >&2; exit 1; }
pass() { echo "PASS: $*"; }

# --- Arrange: scratch DB with the production schema shape -------------------
sqlite3 "$DB" "
CREATE TABLE audit_events (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp_utc TEXT    NOT NULL,
    actor         TEXT    NOT NULL,
    action        TEXT    NOT NULL,
    outcome       TEXT    NOT NULL,
    source_ip     TEXT,
    detail        TEXT
);
CREATE TABLE sessions (
    id_hash          TEXT PRIMARY KEY,
    admin_id         TEXT NOT NULL,
    created_utc      TEXT NOT NULL,
    expires_utc      TEXT NOT NULL,
    idle_expires_utc TEXT,
    revoked          INTEGER NOT NULL DEFAULT 0
);
INSERT INTO audit_events (timestamp_utc, actor, action, outcome, source_ip, detail)
VALUES ('2026-10-26T00:00:00Z', 'admin', 'auth.login', 'success', '127.0.0.1', NULL);
INSERT INTO sessions (id_hash, admin_id, created_utc, expires_utc, revoked)
VALUES ('stale-hash', 'admin', '2026-10-01T00:00:00Z', '2026-12-01T00:00:00Z', 0);
"
pass "scratch database created with one audit row and one live session"

# --- Act 1: backup -----------------------------------------------------------
"$SCRIPT_DIR/backup.sh" --prune 5 "$DB" "$BACKUP_DIR" >/dev/null

BACKUP_FILE="$(ls -1 "$BACKUP_DIR"/hostingadmin-*.db | head -n 1)"
[ -f "$BACKUP_FILE" ] || fail "backup file was not created"
[ -f "$BACKUP_FILE.sha256" ] || fail "sha256 manifest was not created"

# Backup must be a valid SQLite database containing the audit row.
[ "$(sqlite3 "$BACKUP_FILE" "SELECT COUNT(*) FROM audit_events;")" -eq 1 ] \
    || fail "backup does not contain the audit row"

# Mode 600 on both artifacts (they contain audit history + session hashes).
# Permission bits are only meaningful on Linux (CI); MSYS/Windows emulates
# them, so the assertion is skipped there.
if [ "$(uname -s)" = "Linux" ]; then
    [ "$(stat -c %a "$BACKUP_FILE")" = "600" ] || fail "backup file is not mode 600"
    [ "$(stat -c %a "$BACKUP_FILE.sha256")" = "600" ] || fail "manifest is not mode 600"
    pass "backup + manifest created, valid, mode 600"
else
    pass "backup + manifest created, valid (mode check skipped: not Linux)"
fi

# --- Act 2: damage the live database (simulates corruption/accident) --------
sqlite3 "$DB" "DELETE FROM audit_events; INSERT INTO sessions (id_hash, admin_id, created_utc, expires_utc, revoked) VALUES ('another-hash', 'admin', '2026-10-02T00:00:00Z', '2026-12-02T00:00:00Z', 0);"
[ "$(sqlite3 "$DB" "SELECT COUNT(*) FROM audit_events;")" -eq 0 ] || fail "live DB was not damaged"

# --- Act 3: restore ----------------------------------------------------------
"$SCRIPT_DIR/restore.sh" --yes --no-systemd "$BACKUP_FILE" "$DB" >/dev/null

# --- Assert: round trip (TA18) ----------------------------------------------
[ "$(sqlite3 "$DB" "SELECT COUNT(*) FROM audit_events;")" -eq 1 ] \
    || fail "restored DB lost the audit row"
[ "$(sqlite3 "$DB" "SELECT COUNT(*) FROM sessions;")" -eq 0 ] \
    || fail "restored DB kept sessions — resurrected sessions are a security bug"
pass "restore round trip: audit history intact, sessions pruned (TA18)"

# --- Assert: manifest verification rejects a tampered backup ----------------
TAMPERED="$WORK/tampered.db"
cp "$BACKUP_FILE" "$TAMPERED"
printf 'x' >> "$TAMPERED"
cp "$BACKUP_FILE.sha256" "$TAMPERED.sha256"
if "$SCRIPT_DIR/restore.sh" --yes --no-systemd "$TAMPERED" "$WORK/should-not-exist.db" >/dev/null 2>&1; then
    fail "restore accepted a tampered backup"
fi
[ ! -e "$WORK/should-not-exist.db" ] || fail "tampered restore overwrote the target"
pass "restore rejects a tampered backup (checksum mismatch)"

# --- Assert: --prune keeps only the newest N ---------------------------------
for i in 1 2 3 4 5 6 7; do
    sleep 0.05
    sqlite3 "$DB" "UPDATE audit_events SET detail = 'gen-$i' WHERE id = 1;"
    "$SCRIPT_DIR/backup.sh" --prune 3 "$DB" "$BACKUP_DIR" >/dev/null
done
COUNT="$(ls -1 "$BACKUP_DIR"/hostingadmin-*.db | wc -l)"
[ "$COUNT" -eq 3 ] || fail "prune kept $COUNT backups, expected 3"
ORPHANS="$(ls -1 "$BACKUP_DIR"/*.sha256 2>/dev/null | wc -l)"
[ "$ORPHANS" -eq 3 ] || fail "prune left orphaned manifests ($ORPHANS, expected 3)"
pass "prune keeps the newest 3 backups and their manifests"

echo ""
echo "All backup/restore round-trip checks passed."
