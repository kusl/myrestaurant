#!/usr/bin/env bash
#
# Database restore (TECHNICAL_SPECIFICATION §15): stop the web app, pg_restore --clean --if-exists
# the given dump, then start the web app again (startup migrations verify the schema is current).
#
#   scripts/restore.sh <path-to-.dump>
#
# Reminder: a full recovery also requires the Data Protection keys volume that was backed up next to
# this dump (§3.4). Restoring the database alone will orphan existing TOTP secrets and cookies.

set -euo pipefail
cd "$(dirname "$0")/.."

DUMP="${1:-}"
if [[ -z "$DUMP" || ! -f "$DUMP" ]]; then
    echo "usage: scripts/restore.sh <path-to-.dump>" >&2
    exit 1
fi

PGUSER="${POSTGRES_USER:-myrestaurant}"
PGDB="${POSTGRES_DB:-myrestaurant}"

if command -v podman-compose >/dev/null 2>&1; then
    COMPOSE=(podman-compose); ENGINE="podman"
elif command -v podman >/dev/null 2>&1 && podman compose version >/dev/null 2>&1; then
    COMPOSE=(podman compose); ENGINE="podman"
elif command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
    COMPOSE=(docker compose); ENGINE="docker"
else
    echo "error: need podman-compose, 'podman compose', or 'docker compose' on PATH." >&2
    exit 1
fi

CONTAINER="${POSTGRES_CONTAINER:-$("$ENGINE" ps --format '{{.Names}}' | grep -m1 postgres || true)}"
if [[ -z "$CONTAINER" ]]; then
    echo "error: no running postgres container found (set POSTGRES_CONTAINER to override)." >&2
    exit 1
fi

echo "warning: this will overwrite the current contents of database '$PGDB'."
read -r -p "Type 'restore' to continue: " confirm
[[ "$confirm" == "restore" ]] || { echo "aborted."; exit 1; }

echo "info: stopping the web app..."
"${COMPOSE[@]}" stop web || true

echo "info: restoring $DUMP into '$PGDB'..."
"$ENGINE" exec -i "$CONTAINER" pg_restore --clean --if-exists --no-owner -U "$PGUSER" -d "$PGDB" < "$DUMP"

echo "info: starting the web app (migrations will verify the schema)..."
"${COMPOSE[@]}" up -d web

echo "info: restore complete. Confirm health at /healthz/ready."
