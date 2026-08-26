#!/usr/bin/env bash
#
# Backup a complete recovery set (TECHNICAL_SPECIFICATION §15, OPERATIONS §6).
#
#   scripts/backup.sh              dump the database AND capture the Data Protection key ring
#   scripts/backup.sh --no-keys    database only (dev, app on the host rather than in a container)
#   scripts/backup.sh --help       this text
#
# A recovery set is TWO files sharing one timestamp, and either alone is useless:
#
#   BACKUP_DIRECTORY/myrestaurant-YYYYMMDD-HHMMSS.dump                pg_dump --format=custom
#   BACKUP_DIRECTORY/myrestaurant-YYYYMMDD-HHMMSS-dataprotection.tar  the key ring (§3.4)
#
# Without the key ring every stored TOTP secret is undecryptable. pg_dump runs inside the postgres
# container so the client matches the server; the key ring is read out with `podman cp`.
#

set -euo pipefail

WITH_KEYS=1

case "${1:-}" in
    "")
        ;;
    --no-keys)
        WITH_KEYS=0
        ;;
    --help | -h)
        awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$0"
        exit 0
        ;;
    *)
        echo "error: unknown argument '$1' (expected --no-keys, --help, or nothing)." >&2
        exit 1
        ;;
esac

BACKUP_DIRECTORY="${BACKUP_DIRECTORY:-/var/lib/myrestaurant/backups}"
BACKUP_RETENTION_COUNT="${BACKUP_RETENTION_COUNT:-14}"
PGUSER="${POSTGRES_USER:-myrestaurant}"
PGDB="${POSTGRES_DB:-myrestaurant}"
KEYS_DIRECTORY="${DATA_PROTECTION_KEYS_DIRECTORY:-/var/lib/myrestaurant/dataprotection}"

info() { printf '[backup] %s\n' "$*"; }
warn() { printf '[backup] warning: %s\n' "$*" >&2; }
die()  { printf '[backup] error: %s\n' "$*" >&2; exit 1; }

if [[ ! "$BACKUP_RETENTION_COUNT" =~ ^[0-9]+$ ]] || (( BACKUP_RETENTION_COUNT < 1 )); then
    die "BACKUP_RETENTION_COUNT must be a positive integer (got '$BACKUP_RETENTION_COUNT')."
fi

ENGINE_CANDIDATES=()

if [[ -n "${CONTAINER_ENGINE:-}" ]]; then
    command -v "$CONTAINER_ENGINE" >/dev/null 2>&1 \
        || die "CONTAINER_ENGINE='$CONTAINER_ENGINE' is not on PATH."
    ENGINE_CANDIDATES=("$CONTAINER_ENGINE")
else
    for candidate in podman docker; do
        if command -v "$candidate" >/dev/null 2>&1; then
            ENGINE_CANDIDATES+=("$candidate")
        fi
    done
    (( ${#ENGINE_CANDIDATES[@]} > 0 )) || die "need podman or docker on PATH (or set CONTAINER_ENGINE)."
fi

running_containers() {
    local engine="$1" fragment="$2"

    "$engine" ps --filter "name=$fragment" --format '{{.Names}}' 2>/dev/null \
        | tr -d '[]' | awk '{ print $1 }' | awk 'NF' | sort -u
}

engine_knows() {
    "$1" container inspect "$2" >/dev/null 2>&1
}

if [[ -n "${POSTGRES_CONTAINER:-}" ]]; then
    DATABASE_CONTAINER="$POSTGRES_CONTAINER"
    ENGINE=""

    for candidate in "${ENGINE_CANDIDATES[@]}"; do
        if engine_knows "$candidate" "$DATABASE_CONTAINER"; then
            ENGINE="$candidate"
            break
        fi
    done

    if [[ -z "$ENGINE" ]]; then
        warn "no available engine has a container '$DATABASE_CONTAINER' (POSTGRES_CONTAINER)."
        warn "asked: ${ENGINE_CANDIDATES[*]}"
        die "either the name is wrong, or the container belongs to an engine not on PATH (set CONTAINER_ENGINE)."
    fi

    info "using POSTGRES_CONTAINER='$DATABASE_CONTAINER' via $ENGINE."
else
    ENGINE=""
    DATABASE_CONTAINER=""

    for candidate in "${ENGINE_CANDIDATES[@]}"; do
        candidate_names=()
        mapfile -t candidate_names < <(running_containers "$candidate" postgres)

        if (( ${#candidate_names[@]} > 1 )); then
            warn "more than one running container matches 'postgres' under $candidate:"
            printf '           %s\n' "${candidate_names[@]}" >&2
            die "set POSTGRES_CONTAINER to name the one you mean."
        fi

        if (( ${#candidate_names[@]} == 1 )); then
            ENGINE="$candidate"
            DATABASE_CONTAINER="${candidate_names[0]}"
            break
        fi
    done

    if [[ -z "$DATABASE_CONTAINER" ]]; then
        warn "no running container matching 'postgres' under any of: ${ENGINE_CANDIDATES[*]}"
        die "could not identify the postgres container (set POSTGRES_CONTAINER, or CONTAINER_ENGINE, or both)."
    fi

    info "using '$DATABASE_CONTAINER', discovered via $ENGINE."
fi

if ! "$ENGINE" container inspect --format '{{.State.Running}}' "$DATABASE_CONTAINER" 2>/dev/null \
        | grep --quiet true; then
    die "$ENGINE knows '$DATABASE_CONTAINER' but it is not running."
fi

if ! "$ENGINE" exec "$DATABASE_CONTAINER" pg_isready --username "$PGUSER" --dbname "$PGDB" >/dev/null 2>&1; then
    die "'$DATABASE_CONTAINER' ($ENGINE) did not answer pg_isready for user '$PGUSER' database '$PGDB'."
fi

mkdir -p "$BACKUP_DIRECTORY"
timestamp="$(date +%Y%m%d-%H%M%S)"
DUMP_FILE="$BACKUP_DIRECTORY/myrestaurant-${timestamp}.dump"
KEYS_FILE="$BACKUP_DIRECTORY/myrestaurant-${timestamp}-dataprotection.tar"
PARTIAL_DUMP="$BACKUP_DIRECTORY/.myrestaurant-${timestamp}.dump.partial"
PARTIAL_KEYS="$BACKUP_DIRECTORY/.myrestaurant-${timestamp}-dataprotection.tar.partial"

trap 'rm -f -- "$PARTIAL_DUMP" "$PARTIAL_KEYS" 2>/dev/null || true' EXIT

info "dumping database '$PGDB' from container '$DATABASE_CONTAINER'…"
"$ENGINE" exec --interactive "$DATABASE_CONTAINER" \
    pg_dump --format=custom --no-owner --username "$PGUSER" "$PGDB" > "$PARTIAL_DUMP"

if [[ ! -s "$PARTIAL_DUMP" ]]; then
    die "pg_dump produced an empty file — nothing was written to $BACKUP_DIRECTORY."
fi

if [[ "$(head -c 5 "$PARTIAL_DUMP")" != "PGDMP" ]]; then
    die "the dump does not begin with a custom-format archive header — refusing to keep it."
fi

mv -- "$PARTIAL_DUMP" "$DUMP_FILE"
info "database dump: $DUMP_FILE ($(du -h -- "$DUMP_FILE" | cut -f1))"

KEYS_CAPTURED=0

if (( WITH_KEYS )); then
    APPLICATION_CONTAINER=""

    if [[ -n "${WEB_CONTAINER:-}" ]]; then
        APPLICATION_CONTAINER="$WEB_CONTAINER"
        info "using WEB_CONTAINER='$APPLICATION_CONTAINER'."
    else
        web_names=()
        mapfile -t web_names < <(running_containers "$ENGINE" web)

        if (( ${#web_names[@]} > 1 )); then
            warn "more than one running container matches 'web' under $ENGINE:"
            printf '           %s\n' "${web_names[@]}" >&2
            warn "set WEB_CONTAINER to name the one you mean."
        elif (( ${#web_names[@]} == 1 )); then
            APPLICATION_CONTAINER="${web_names[0]}"
        fi
    fi

    if [[ -z "$APPLICATION_CONTAINER" ]]; then
        warn "could not identify the web container under $ENGINE, so the Data Protection key ring"
        warn "was NOT captured. In dev the application runs on the host (run.sh), where there is no"
        warn "container to read from — pass --no-keys there. In production this is a real problem:"
        warn "see OPERATIONS §8."
    elif ! "$ENGINE" cp "$APPLICATION_CONTAINER:$KEYS_DIRECTORY/." - > "$PARTIAL_KEYS" 2>/dev/null; then
        warn "could not read '$KEYS_DIRECTORY' out of '$APPLICATION_CONTAINER' — key ring NOT captured."
        warn "check DATA_PROTECTION_KEYS_DIRECTORY matches the container's mount point."
        rm -f -- "$PARTIAL_KEYS"
    else
        key_count="$(tar -tf "$PARTIAL_KEYS" 2>/dev/null | grep -c 'key-.*\.xml$' || true)"
        mv -- "$PARTIAL_KEYS" "$KEYS_FILE"
        KEYS_CAPTURED=1
        info "key ring: $KEYS_FILE (${key_count} key file(s))"
        if (( key_count == 0 )); then
            warn "the key ring is EMPTY. Data Protection creates its first key the first time it"
            warn "protects anything — an instance nobody has signed in to yet has none. Harmless"
            warn "now; not harmless once anyone has enrolled TOTP, so check the next set."
        fi
    fi
else
    info "skipping the key ring (--no-keys)."
fi

mapfile -t dumps < <(ls -1t "$BACKUP_DIRECTORY"/myrestaurant-*.dump 2>/dev/null || true)
if (( ${#dumps[@]} > BACKUP_RETENTION_COUNT )); then
    for stale in "${dumps[@]:BACKUP_RETENTION_COUNT}"; do
        info "pruning $stale (and its key ring, if any)"
        rm -f -- "$stale" "${stale%.dump}-dataprotection.tar"
    done
fi

echo
info "backup complete. Rehearse it with: scripts/restore_drill.sh"
if (( WITH_KEYS )) && (( ! KEYS_CAPTURED )); then
    warn "this set is INCOMPLETE — database only, no key ring (exit 2)."
    exit 2
fi
exit 0
