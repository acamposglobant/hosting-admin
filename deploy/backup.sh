#!/usr/bin/env bash
# Hosting Admin — database backup (increment 3, D27).
#
# Operator-run script: takes a consistent online snapshot of the SQLite
# database using `sqlite3 .backup` (safe while the API is running, WAL mode),
# writes a sha256 manifest next to it, and optionally prunes old backups.
#
# Usage:
#   backup.sh [--prune N] [db-path] [backup-dir]
#
# Defaults:
#   db-path    /opt/hostingadmin/data/hostingadmin.db
#   backup-dir /var/backups/hostingadmin
#
# Run as the API's service account (least privilege, preferred) or as root.
# Backup files contain the full audit history and session hashes — they are
# sensitive artifacts: mode 600, store outside the repo, restrict access.
#
# Scheduling example (cron, as the API service account):
#   15 3 * * * /opt/hostingadmin/deploy/backup.sh --prune 14

set -euo pipefail

KEEP=""
POSITIONAL=()

while [ $# -gt 0 ]; do
    case "$1" in
        --prune)
            KEEP="${2:?--prune requires a number}"
            shift 2
            ;;
        -*)
            echo "backup.sh: unknown option: $1" >&2
            exit 1
            ;;
        *)
            POSITIONAL+=("$1")
            shift
            ;;
    esac
done

DB_PATH="${POSITIONAL[0]:-/opt/hostingadmin/data/hostingadmin.db}"
BACKUP_DIR="${POSITIONAL[1]:-/var/backups/hostingadmin}"

if [ ! -f "$DB_PATH" ]; then
    echo "backup.sh: database not found: $DB_PATH" >&2
    exit 1
fi

if ! command -v sqlite3 >/dev/null 2>&1; then
    echo "backup.sh: sqlite3 CLI is required (see docs/deployment.md prerequisites)" >&2
    exit 1
fi

mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"

STAMP="$(date -u +%Y%m%dT%H%M%S.%3NZ)"
TARGET="$BACKUP_DIR/hostingadmin-$STAMP.db"
TMP_TARGET="$TARGET.tmp"

# .backup produces a consistent copy even while the source is being written
# (WAL mode). The temp file avoids publishing a partial backup on failure.
# Note: paths containing single quotes are not supported by the .backup
# dot-command; the documented default paths never contain quotes.
sqlite3 "$DB_PATH" ".backup '$TMP_TARGET'"

mv "$TMP_TARGET" "$TARGET"
chmod 600 "$TARGET"

# Manifest covers the backup file only; the manifest itself is written after
# the file is final, so a crash cannot produce a manifest for a partial file.
sha256sum "$TARGET" > "$TARGET.sha256"
chmod 600 "$TARGET.sha256"

echo "backup.sh: wrote $TARGET"

# Prune: keep the newest N backups (by name, which sorts chronologically
# because of the UTC timestamp). The manifest of a pruned backup is removed
# with it.
if [ -n "$KEEP" ]; then
    if ! [ "$KEEP" -ge 1 ] 2>/dev/null; then
        echo "backup.sh: --prune requires a positive integer" >&2
        exit 1
    fi
    # Filenames are machine-generated (UTC timestamp, no whitespace), so
    # glob expansion is safe here; nullglob-style iteration via for-loop.
    for old in "$BACKUP_DIR"/hostingadmin-*.db; do
        [ -e "$old" ] || break
        echo "$old"
    done | head -n -"$KEEP" | while read -r old; do
        rm -f "$old" "$old.sha256"
        echo "backup.sh: pruned $(basename "$old")"
    done
fi
