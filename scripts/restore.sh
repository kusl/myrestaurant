#!/usr/bin/env bash
#
# Restore a recovery set (TECHNICAL_SPECIFICATION §15, OPERATIONS §6).
#
#   scripts/restore.sh <path-to-.dump>              interactive; confirms before overwriting
#   scripts/restore.sh --yes <path-to-.dump>        no confirmation prompt (scripted recovery)
#   scripts/restore.sh --no-keys <path-to-.dump>    database only; leave the key ring alone
#   scripts/restore.sh --help                       this text
#
# What it does, in order: verify the archive is a custom-format dump; stop `web`; `pg_restore --clean
# --if-exists` into the postgres container; put the Data Protection key ring back from the sibling
# `-dataprotection.tar` written by `scripts/backup.sh`; start `web` again (DbUp verifies the schema at
# startup and rolls an older dump forward); wait for /healthz/ready.
#
# THE WEB APPLICATION IS ALWAYS STARTED AGAIN. That sentence used to be false and it is the whole
# reason this script was rewritten (F-38). `pg_restore` exits 1 whenever it ignored ANY error — that
# is its documented contract, `exit_code = AH->n_errors ? 1 : 0` in `pg_restore.c` — and `--clean
# --if-exists` ignores errors routinely. Under `set -e` the old script therefore died on the
# `pg_restore` line, which sat BEFORE the `up -d web` line, with `web` already stopped. The single
# most likely outcome of the documented recovery procedure was a database that came back and an
# application that stayed down, silently. Starting `web` again now happens in an EXIT trap, so it
# happens on every path out of this script including the ones nobody planned.
#
# Ignored errors are reported rather than fatal, and turn the exit code into 2. On a healthy set the
# count is zero — `scripts/backup.sh` passes `--no-owner` at dump time precisely so that this number
# stays worth reading — so a non-zero count is news even though it is usually benign.
#
# Exit codes:
#   0   restored, key ring in place, web healthy
#   1   nothing was restored (bad arguments, unusable archive, no container)
#   2   restored, but with reservations: pg_restore ignored errors, or the key ring was not put back
#
# Environment:
#   POSTGRES_USER / POSTGRES_DB     database credentials (default myrestaurant / myrestaurant)
#   POSTGRES_CONTAINER              skip discovery and use this container for pg_restore
#   WEB_CONTAINER                   skip discovery and use this container for the key ring
#   DATA_PROTECTION_KEYS_DIRECTORY  the key-ring path inside the web container
#                                   (default /var/lib/myrestaurant/dataprotection)
#   RESTORE_HEALTH_URL              readiness probe (default http://127.0.0.1:8080/healthz/ready)

set -euo pipefail
cd "$(dirname "$0")/.."

ASSUME_YES=0
WITH_KEYS=1
DUMP=""

while (( $# > 0 )); do
    case "$1" in
        --yes | -y)
            ASSUME_YES=1
            ;;
        --no-keys)
            WITH_KEYS=0
            ;;
        --help | -h)
            awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$0"
            exit 0
            ;;
        -*)
            echo "error: unknown option '$1' (expected --yes, --no-keys, or --help)." >&2
            exit 1
            ;;
        *)
            if [[ -n "$DUMP" ]]; then
                echo "error: more than one dump given ('$DUMP' and '$1')." >&2
                exit 1
            fi
            DUMP="$1"
            ;;
    esac
    shift
done

PGUSER="${POSTGRES_USER:-myrestaurant}"
PGDB="${POSTGRES_DB:-myrestaurant}"
KEYS_DIRECTORY="${DATA_PROTECTION_KEYS_DIRECTORY:-/var/lib/myrestaurant/dataprotection}"
HEALTH_URL="${RESTORE_HEALTH_URL:-http://127.0.0.1:8080/healthz/ready}"

info() { printf '[restore] %s\n' "$*"; }
warn() { printf '[restore] warning: %s\n' "$*" >&2; }
die()  { printf '[restore] error: %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------------------------------
# 1. The archive. Checked before anything is stopped, so a typo costs nothing.
# ---------------------------------------------------------------------------------------------------
[[ -n "$DUMP" ]] || die "usage: scripts/restore.sh [--yes] [--no-keys] <path-to-.dump>"
[[ -f "$DUMP" ]] || die "'$DUMP' is not a file."
[[ -s "$DUMP" ]] || die "'$DUMP' is empty."

if [[ "$(head -c 5 "$DUMP")" != "PGDMP" ]]; then
    die "'$DUMP' does not begin with a custom-format archive header (expected 'PGDMP')."
fi

KEYS_ARCHIVE="${DUMP%.dump}-dataprotection.tar"

# ---------------------------------------------------------------------------------------------------
# 2. Engine and compose command (mirrors run.sh and scripts/quick_tunnel.sh).
# ---------------------------------------------------------------------------------------------------
if command -v podman-compose >/dev/null 2>&1; then
    COMPOSE=(podman-compose); ENGINE="podman"
elif command -v podman >/dev/null 2>&1 && podman compose version >/dev/null 2>&1; then
    COMPOSE=(podman compose); ENGINE="podman"
elif command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
    COMPOSE=(docker compose); ENGINE="docker"
else
    die "need podman-compose, 'podman compose', or 'docker compose' on PATH."
fi

# One container name or a refusal — never whichever one the engine listed first. See the same comment
# in scripts/backup.sh: scripts/restore_drill.sh creates a second postgres container on purpose, and
# restoring into it instead of the real one would look exactly like success.
discover_container() {
    local fragment="$1" label="$2"
    local -a names=()

    mapfile -t names < <("$ENGINE" ps --filter "name=$fragment" --format '{{.Names}}' 2>/dev/null \
        | tr -d '[]' | awk '{ print $1 }' | awk 'NF' | sort -u)

    if (( ${#names[@]} == 0 )); then
        warn "no running container matching '$fragment' (the $label)."
        return 1
    fi

    if (( ${#names[@]} > 1 )); then
        warn "more than one running container matches '$fragment' (the $label):"
        printf '            %s\n' "${names[@]}" >&2
        warn "set ${label^^}_CONTAINER to name the one you mean."
        return 1
    fi

    printf '%s\n' "${names[0]}"
}

if [[ -n "${POSTGRES_CONTAINER:-}" ]]; then
    DATABASE_CONTAINER="$POSTGRES_CONTAINER"
else
    DATABASE_CONTAINER="$(discover_container postgres postgres || true)"
    [[ -n "$DATABASE_CONTAINER" ]] || die "could not identify the postgres container (set POSTGRES_CONTAINER)."
fi

if ! "$ENGINE" exec "$DATABASE_CONTAINER" pg_isready --username "$PGUSER" --dbname "$PGDB" >/dev/null 2>&1; then
    die "'$DATABASE_CONTAINER' did not answer pg_isready for user '$PGUSER' database '$PGDB'."
fi

# The web container is discovered while it is still running, because that is the only time discovery
# by name works — one step from here it will be stopped.
APPLICATION_CONTAINER=""
if (( WITH_KEYS )); then
    if [[ -n "${WEB_CONTAINER:-}" ]]; then
        APPLICATION_CONTAINER="$WEB_CONTAINER"
    else
        APPLICATION_CONTAINER="$(discover_container web web || true)"
    fi
fi

# ---------------------------------------------------------------------------------------------------
# 3. Confirm.
# ---------------------------------------------------------------------------------------------------
echo
info "about to restore:"
info "  dump         $DUMP"
if (( WITH_KEYS )) && [[ -f "$KEYS_ARCHIVE" ]]; then
    info "  key ring     $KEYS_ARCHIVE"
elif (( WITH_KEYS )); then
    info "  key ring     (none found beside the dump — see the warning below)"
else
    info "  key ring     skipped (--no-keys)"
fi
info "  into         database '$PGDB' in container '$DATABASE_CONTAINER'"
echo
warn "this OVERWRITES the current contents of database '$PGDB'."

if (( ! ASSUME_YES )); then
    read -r -p "Type 'restore' to continue: " confirm
    [[ "$confirm" == "restore" ]] || { info "aborted."; exit 1; }
fi

# ---------------------------------------------------------------------------------------------------
# 4. Stop web, and arrange for it to come back no matter what happens next.
# ---------------------------------------------------------------------------------------------------
WEB_STOPPED=0
RESERVATIONS=0

http_ok() {
    if command -v curl >/dev/null 2>&1; then
        curl --fail --silent --show-error --max-time 5 --output /dev/null "$1" >/dev/null 2>&1
    elif command -v wget >/dev/null 2>&1; then
        wget --quiet --timeout=5 --output-document=/dev/null "$1" >/dev/null 2>&1
    else
        return 3
    fi
}

wait_for_health() {
    local deadline=$(( $(date +%s) + 180 ))
    while (( $(date +%s) < deadline )); do
        if http_ok "$HEALTH_URL"; then
            return 0
        fi
        sleep 3
    done
    return 1
}

finish() {
    local status=$?

    if (( WEB_STOPPED )); then
        echo
        info "starting the web application again (DbUp verifies the schema at startup)…"
        if "${COMPOSE[@]}" up -d web; then
            if wait_for_health; then
                info "healthy: $HEALTH_URL answered 200."
            else
                warn "the web application did not report ready at $HEALTH_URL."
                warn "read the log: ${COMPOSE[*]} logs web"
                warn "a dump from a NEWER schema than this code fails fast on purpose — deploy"
                warn "matching code first (OPERATIONS §6, ADR-0012: there are no down-migrations)."
                (( status == 0 )) && status=2
            fi
        else
            warn "could not start 'web'. Start it yourself: ${COMPOSE[*]} up -d web"
            (( status == 0 )) && status=2
        fi
    fi

    if (( status == 0 )) && (( RESERVATIONS )); then
        status=2
    fi

    echo
    case "$status" in
        0) info "restore complete. Sign in and open a sitting's event history to confirm (OPERATIONS §6)." ;;
        2) warn "restore finished WITH RESERVATIONS — read the warnings above before trusting it." ;;
        *) warn "restore did not complete." ;;
    esac

    exit "$status"
}
trap finish EXIT

info "stopping the web application…"
"${COMPOSE[@]}" stop web || warn "'${COMPOSE[*]} stop web' reported a problem; continuing."
WEB_STOPPED=1

# ---------------------------------------------------------------------------------------------------
# 5. The database.
# ---------------------------------------------------------------------------------------------------
echo
info "restoring $DUMP into '$PGDB'…"

restore_status=0
"$ENGINE" exec --interactive "$DATABASE_CONTAINER" \
    pg_restore --clean --if-exists --no-owner --username "$PGUSER" --dbname "$PGDB" \
    < "$DUMP" || restore_status=$?

if (( restore_status != 0 )); then
    warn "pg_restore exited $restore_status, which means it ignored at least one error (see above)."
    warn "with --clean --if-exists that is usually benign — a DROP for an object that was not there."
    warn "It is reported rather than swallowed because the count is small enough to be worth reading."
    RESERVATIONS=1
else
    info "pg_restore reported no errors."
fi

# ---------------------------------------------------------------------------------------------------
# 6. The key ring (§3.4). Put back while `web` is stopped, so the application reads it at startup
#    rather than after it has already created a fresh ring of its own.
# ---------------------------------------------------------------------------------------------------
echo
if (( ! WITH_KEYS )); then
    info "skipping the key ring (--no-keys)."
elif [[ ! -f "$KEYS_ARCHIVE" ]]; then
    warn "no key ring beside the dump ('$KEYS_ARCHIVE' does not exist)."
    warn "The database is back; every stored TOTP secret in it is NOT decryptable without the ring"
    warn "that encrypted it (§3.4). Administrators must clear TOTP per affected account and users"
    warn "re-enroll through the obligations pipeline — OPERATIONS §8. Passwords and passkeys are fine."
    RESERVATIONS=1
elif [[ -z "$APPLICATION_CONTAINER" ]]; then
    warn "found '$KEYS_ARCHIVE' but no web container to put it in."
    warn "'stop' leaves the container in place; 'down' removes it. If you ran 'down', bring the stack"
    warn "up and then: $ENGINE cp - '<web-container>:$KEYS_DIRECTORY' < '$KEYS_ARCHIVE'"
    RESERVATIONS=1
elif ! "$ENGINE" cp - "$APPLICATION_CONTAINER:$KEYS_DIRECTORY" < "$KEYS_ARCHIVE"; then
    warn "could not write the key ring into '$APPLICATION_CONTAINER:$KEYS_DIRECTORY'."
    RESERVATIONS=1
else
    info "key ring restored into '$APPLICATION_CONTAINER:$KEYS_DIRECTORY'."
fi

# `web` is started by the EXIT trap — deliberately, so that every path out of this script starts it,
# including the ones that got here by failing.
