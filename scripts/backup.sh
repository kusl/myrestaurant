#!/usr/bin/env bash
#
# Database backup (TECHNICAL_SPECIFICATION §15): pg_dump -Fc into
# BACKUP_DIRECTORY/myrestaurant-YYYYMMDD-HHMMSS.dump, then prune to BACKUP_RETENTION_COUNT most
# recent dumps. Run pg_dump inside the postgres container (so the tool version matches the server).
# Schedule via a systemd timer or host cron at BACKUP_SCHEDULE_TIME.
#
# IMPORTANT: this backs up the DATABASE only. The Data Protection keys volume must be backed up
# alongside it (§3.4) — without those keys, TOTP secrets and auth cookies are unrecoverable.

set -euo pipefail

BACKUP_DIRECTORY="${BACKUP_DIRECTORY:-/var/lib/myrestaurant/backups}"
BACKUP_RETENTION_COUNT="${BACKUP_RETENTION_COUNT:-14}"
PGUSER="${POSTGRES_USER:-myrestaurant}"
PGDB="${POSTGRES_DB:-myrestaurant}"

if command -v podman >/dev/null 2>&1; then
    ENGINE="podman"
elif command -v docker >/dev/null 2>&1; then
    ENGINE="docker"
else
    echo "error: need podman or docker on PATH." >&2
    exit 1
fi

CONTAINER="${POSTGRES_CONTAINER:-$("$ENGINE" ps --format '{{.Names}}' | grep -m1 postgres || true)}"
if [[ -z "$CONTAINER" ]]; then
    echo "error: no running postgres container found (set POSTGRES_CONTAINER to override)." >&2
    exit 1
fi

mkdir -p "$BACKUP_DIRECTORY"
timestamp="$(date +%Y%m%d-%H%M%S)"
outfile="$BACKUP_DIRECTORY/myrestaurant-${timestamp}.dump"

echo "info: dumping '$PGDB' from container '$CONTAINER' -> $outfile"
"$ENGINE" exec -i "$CONTAINER" pg_dump -Fc -U "$PGUSER" "$PGDB" > "$outfile"

# Prune: keep only the newest BACKUP_RETENTION_COUNT dumps.
mapfile -t dumps < <(ls -1t "$BACKUP_DIRECTORY"/myrestaurant-*.dump 2>/dev/null || true)
if (( ${#dumps[@]} > BACKUP_RETENTION_COUNT )); then
    for stale in "${dumps[@]:BACKUP_RETENTION_COUNT}"; do
        echo "info: pruning old backup $stale"
        rm -f -- "$stale"
    done
fi

echo "info: backup complete. Remember to back up the Data Protection keys volume too (§3.4)."
