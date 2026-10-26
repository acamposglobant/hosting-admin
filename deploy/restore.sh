#!/usr/bin/env bash
# Hosting Admin — database restore (increment 3, D27).
#
# Operator-run script: restores a backup produced by backup.sh.
#
#   restore.sh [--yes] [--no-systemd] <backup-file> [db-path]
#
# Defaults:
#   db-path /opt/hostingadmin/data/hostingadmin.db
#
# Procedure (plan §3.4): stop API → replace DB → prune sessions → start API
# → verify health. Sessions are pruned on restore: a restored database
# resurrects old session rows, which would let a stolen old cookie live
# again; pruning forces re-login for every admin. Confirmation tokens are
# short-TTL and self-expiring, so they need no special handling.
#
# --no-systemd skips systemctl and the health check; it exists for the CI
# round-trip test (TA18) and must not be used on a real host.

set -euo pipefail

ASSUME_YES=""
NO_SYSTEMD=""
BACKUP_FILE=""
DB_PATH="/opt/hostingadmin/data/hostingadmin.db"
API_UNIT="hostingadmin-api"
HEALTH_URL="${HOSTINGADMIN_HEALTH_URL:-http://127.0.0.1:5080/api/health}"

while [ $# -gt 0 ]; do
    case "$1" in
        --yes) ASSUME_YES="1"; shift ;;
        --no-systemd) NO_SYSTEMD="1"; shift ;;
        -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
        *)
            if [ -z "$BACKUP_FILE" ]; then
                BACKUP_FILE="$1"
            else
                DB_PATH="$1"
            fi
            shift
            ;;
    esac
done

if [ -z "$BACKUP_FILE" ]; then
    echo "usage: restore.sh [--yes] [--no-systemd] <backup-file> [db-path]" >&2
    exit 1
fi

if [ ! -f "$BACKUP_FILE" ]; then
    echo "restore.sh: backup file not found: $BACKUP_FILE" >&2
    exit 1
fi

if ! command -v sqlite3 >/dev/null 2>&1; then
    echo "restore.sh: sqlite3 CLI is required (see docs/deployment.md)" >&2
    exit 1
fi

# Verify the backup against its manifest when one sits next to it. Compare
# the recorded hash against the hash of THIS file (not the filename recorded
# inside the manifest — the backup may have been copied or renamed).
if [ -f "$BACKUP_FILE.sha256" ]; then
    expected_hash="$(cut -d' ' -f1 "$BACKUP_FILE.sha256")"
    actual_hash="$(sha256sum "$BACKUP_FILE" | cut -d' ' -f1)"
    if [ "$actual_hash" != "$expected_hash" ]; then
        echo "restore.sh: checksum mismatch — backup file is corrupt or tampered with" >&2
        exit 1
    fi
else
    echo "restore.sh: WARNING: no .sha256 manifest next to the backup; skipping verification" >&2
fi

echo "restore.sh: about to replace $DB_PATH with $BACKUP_FILE"
echo "  - the current database will be overwritten"
echo "  - all sessions will be pruned (every admin must log in again)"
if [ -z "$ASSUME_YES" ]; then
    printf 'Continue? [y/N] '
    read -r answer
    case "$answer" in
        y|Y|yes|YES) ;;
        *) echo "restore.sh: aborted"; exit 1 ;;
    esac
fi

if [ -z "$NO_SYSTEMD" ]; then
    systemctl stop "$API_UNIT"
fi

# Replace the database. Remove WAL/SHM sidecars of the OLD database so the
# new file is not reconciled against stale WAL frames.
rm -f "$DB_PATH-wal" "$DB_PATH-shm"
cp "$BACKUP_FILE" "$DB_PATH"

# Prune sessions BEFORE the API starts: restored sessions must never be
# valid (TA18). The schema initializer is idempotent, so an old backup with
# an older schema still works — the API migrates on startup.
if ! sqlite3 "$DB_PATH" "DELETE FROM sessions;"; then
    echo "restore.sh: failed to prune sessions — restore the file manually and run:" >&2
    echo "  sqlite3 $DB_PATH 'DELETE FROM sessions;'" >&2
    exit 1
fi

if [ -z "$NO_SYSTEMD" ]; then
    systemctl start "$API_UNIT"

    # Health verification: the API must come back before we call it done.
    for _ in $(seq 1 30); do
        if curl -fsS "$HEALTH_URL" >/dev/null 2>&1; then
            echo "restore.sh: health check passed; all sessions pruned (re-login required)"
            exit 0
        fi
        sleep 1
    done
    echo "restore.sh: API did not become healthy within 30 s — inspect journalctl -u $API_UNIT" >&2
    exit 1
fi

echo "restore.sh: database restored; sessions pruned"
