#!/usr/bin/env bash
#
# Backup a complete recovery set (TECHNICAL_SPECIFICATION §15, OPERATIONS §6).
#
#   scripts/backup.sh              dump the database AND capture the Data Protection key ring
#   scripts/backup.sh --no-keys    database only (dev, where the app runs on the host, not in a container)
#   scripts/backup.sh --help       this text
#
# A recovery set is TWO files sharing one timestamp, and it is a set because either one alone is
# useless for the thing you will actually be doing at the time — bringing a restaurant back:
#
#   BACKUP_DIRECTORY/myrestaurant-YYYYMMDD-HHMMSS.dump                    pg_dump --format=custom
#   BACKUP_DIRECTORY/myrestaurant-YYYYMMDD-HHMMSS-dataprotection.tar      the key ring (§3.4)
#
# Without the key ring, every stored TOTP secret is undecryptable — the database comes back and every
# enrolled authenticator does not (§3.4, OPERATIONS §8). §15 has always said the key ring must be
# backed up alongside the database; until F-38 nothing in this tree did it, and this script only
# printed a reminder. It now captures both.
#
# pg_dump runs INSIDE the postgres container via `podman exec`, so the dump client always matches the
# server version (F-16). The key ring is read out of the web container with `podman cp`, which uses
# the engine's own archive API and therefore needs no tools installed in that image.
#
# Retention prunes to the newest BACKUP_RETENTION_COUNT *sets* and prunes only after a new set has
# landed, so a failing backup never eats an old one. Two smaller guarantees make that true rather
# than merely intended: the dump is written to a hidden `.partial` file and only renamed into place
# once it is complete and its header checks out, so a half-written dump can never become "the newest
# backup" and evict a good one on the next run; and a dump is only attempted after `pg_isready`
# confirms the discovered container really is the database and the credentials work.
#
# Schedule it at BACKUP_SCHEDULE_TIME with a systemd USER timer or cron. With a user timer,
# `loginctl enable-linger <user>` is what keeps it running with nobody logged in.
#
# Exit codes are three-valued on purpose, because "the database was dumped but the key ring was not"
# is neither success nor failure and a scheduled job needs to be able to tell:
#
#   0   a complete recovery set landed
#   1   no usable backup was produced (nothing was written, or what was written was removed)
#   2   the database dump landed but the key ring did not — recoverable, but not a complete set
#
# ENGINE SELECTION IS A REAL DECISION, not a formality, and getting it wrong reports as a database
# fault (F-43). This script used to take the first of podman/docker on PATH. A host with both — every
# GitHub Actions ubuntu runner is one, because its image installs a static podman bundle alongside
# Docker — would therefore run `podman exec <a docker container>`, which fails with "no such
# container", which arrived here as "did not answer pg_isready" and sent the reader to PostgreSQL.
#
# So the container chooses the engine rather than PATH order choosing it. The only reason this script
# needs an engine at all is to reach one named container; whether a given engine can see that
# container is a fact it can check, not a preference. CONTAINER_ENGINE overrides the whole question.
#
# Environment:
#   BACKUP_DIRECTORY                where sets are written (default /var/lib/myrestaurant/backups)
#   BACKUP_RETENTION_COUNT          how many sets to keep (default 14; must be a positive integer)
#   POSTGRES_USER / POSTGRES_DB     database credentials (default myrestaurant / myrestaurant)
#   POSTGRES_CONTAINER              skip discovery and use this container for pg_dump
#   WEB_CONTAINER                   skip discovery and use this container for the key ring
#   CONTAINER_ENGINE                force the engine (podman or docker) instead of deciding. Honoured
#                                   by scripts/restore_drill.sh too, and by nothing else here — the
#                                   compose-driven scripts (run.sh, restore.sh, quick_tunnel.sh)
#                                   choose a compose command, which is a different question.
#   DATA_PROTECTION_KEYS_DIRECTORY  the key-ring path inside the web container
#                                   (default /var/lib/myrestaurant/dataprotection)

set -euo pipefail

WITH_KEYS=1

case "${1:-}" in
    "")
        ;;
    --no-keys)
        WITH_KEYS=0
        ;;
    --help | -h)
        # Print the header comment block and stop at the first line of real code, so the help text
        # cannot drift out of step with a hard-coded line range.
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

# A retention count that is not a positive integer would reach `(( ${#dumps[@]} > $count ))` and, under
# `set -e`, take the whole script down AFTER a good dump had landed — reported as a backup failure when
# the backup had in fact succeeded. Check it before anything is written.
if [[ ! "$BACKUP_RETENTION_COUNT" =~ ^[0-9]+$ ]] || (( BACKUP_RETENTION_COUNT < 1 )); then
    die "BACKUP_RETENTION_COUNT must be a positive integer (got '$BACKUP_RETENTION_COUNT')."
fi

# ---------------------------------------------------------------------------------------------------
# Container engine, then containers.
#
# CANDIDATES first, one engine second. podman leads the list because ADR-0004 makes rootless Podman
# canonical, but leading the list is all that preference buys: which candidate is used is settled
# below by which one can actually see the database container.
# ---------------------------------------------------------------------------------------------------
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

# Prints every running container name an engine reports for a name fragment, one per line, and
# nothing else — no warnings, no exit codes to interpret. It is called once per candidate engine
# during selection, and a function that complained on its own would complain about the engine that
# was about to lose. The caller counts the lines and decides.
running_containers() {
    local engine="$1" fragment="$2"

    "$engine" ps --filter "name=$fragment" --format '{{.Names}}' 2>/dev/null \
        | tr -d '[]' | awk '{ print $1 }' | awk 'NF' | sort -u
}

# True when this engine has a container of that name OR id, running or not. `container inspect` rather
# than `ps` because POSTGRES_CONTAINER is frequently an id (CI passes job.services.postgres.id), and
# `ps --filter name=` does not match ids.
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
    # Discovery must name exactly one container. `grep -m1 postgres` used to stand here, and it
    # silently picked whichever container the engine happened to list first — which is fine until a
    # second one exists, and `scripts/restore_drill.sh` creates a second postgres container on
    # purpose. A backup that dumps the drill's scratch database instead of the real one would
    # succeed, be the right size, and be worthless. Ambiguity is an error you can act on.
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

# Prove it is the database, and prove the credentials work, BEFORE creating a file that would
# otherwise look like a backup. A dump that fails after the redirect has opened leaves bytes behind.
#
# Two failures, said separately. "Not running" and "running but not answering for these credentials"
# are fixed in different places, and one message covering both is how an engine-selection fault came
# to read as a PostgreSQL fault in the first place.
if ! "$ENGINE" container inspect --format '{{.State.Running}}' "$DATABASE_CONTAINER" 2>/dev/null \
        | grep --quiet true; then
    die "$ENGINE knows '$DATABASE_CONTAINER' but it is not running."
fi

if ! "$ENGINE" exec "$DATABASE_CONTAINER" pg_isready --username "$PGUSER" --dbname "$PGDB" >/dev/null 2>&1; then
    die "'$DATABASE_CONTAINER' ($ENGINE) did not answer pg_isready for user '$PGUSER' database '$PGDB'."
fi

# ---------------------------------------------------------------------------------------------------
# The database dump: hidden partial, verified, then renamed into place.
# ---------------------------------------------------------------------------------------------------
mkdir -p "$BACKUP_DIRECTORY"
timestamp="$(date +%Y%m%d-%H%M%S)"
DUMP_FILE="$BACKUP_DIRECTORY/myrestaurant-${timestamp}.dump"
KEYS_FILE="$BACKUP_DIRECTORY/myrestaurant-${timestamp}-dataprotection.tar"
PARTIAL_DUMP="$BACKUP_DIRECTORY/.myrestaurant-${timestamp}.dump.partial"
PARTIAL_KEYS="$BACKUP_DIRECTORY/.myrestaurant-${timestamp}-dataprotection.tar.partial"

# Single-quoted so the paths expand when the trap fires, not when it is installed. An inline trap
# rather than a named function on purpose: a partial file must never survive, and there is nothing
# here worth the indirection.
trap 'rm -f -- "$PARTIAL_DUMP" "$PARTIAL_KEYS" 2>/dev/null || true' EXIT

info "dumping database '$PGDB' from container '$DATABASE_CONTAINER'…"
"$ENGINE" exec --interactive "$DATABASE_CONTAINER" \
    pg_dump --format=custom --no-owner --username "$PGUSER" "$PGDB" > "$PARTIAL_DUMP"

# `--no-owner` at dump time (rather than only at restore time) is deliberate: every ownership
# statement pg_dump would emit is one more thing pg_restore can report as an ignored error, and
# `scripts/restore_drill.sh` counts those. Fewer ignored errors means the count is worth reading.

if [[ ! -s "$PARTIAL_DUMP" ]]; then
    die "pg_dump produced an empty file — nothing was written to $BACKUP_DIRECTORY."
fi

# A custom-format archive starts with the five bytes 'PGDMP'. This is cheap insurance rather than the
# real integrity check: pg_dump exiting non-zero already takes the script down under `set -e`, and
# the partial file is removed by the trap. `scripts/restore_drill.sh` is where an archive is proved
# restorable, because proving that means restoring it, and 03:30 is the wrong time to find out.
if [[ "$(head -c 5 "$PARTIAL_DUMP")" != "PGDMP" ]]; then
    die "the dump does not begin with a custom-format archive header — refusing to keep it."
fi

mv -- "$PARTIAL_DUMP" "$DUMP_FILE"
info "database dump: $DUMP_FILE ($(du -h -- "$DUMP_FILE" | cut -f1))"

# ---------------------------------------------------------------------------------------------------
# The key ring (§3.4). Read out of the web container with `podman cp`, which streams a tar archive
# through the engine and therefore does not care what is installed inside that image.
# ---------------------------------------------------------------------------------------------------
KEYS_CAPTURED=0

if (( WITH_KEYS )); then
    APPLICATION_CONTAINER=""

    if [[ -n "${WEB_CONTAINER:-}" ]]; then
        APPLICATION_CONTAINER="$WEB_CONTAINER"
        info "using WEB_CONTAINER='$APPLICATION_CONTAINER'."
    else
        # The same engine the database was reached through, deliberately: the two containers are one
        # stack, and a tree where they live under different engines is not a topology this project
        # has. Ambiguity is fatal for the dump and only a warning for the key ring, because §15's
        # three-valued exit already has a word for "database dumped, ring not captured".
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

# ---------------------------------------------------------------------------------------------------
# Retention. Prune whole sets, newest first, and only now that a new set is on disk.
# ---------------------------------------------------------------------------------------------------
mapfile -t dumps < <(ls -1t "$BACKUP_DIRECTORY"/myrestaurant-*.dump 2>/dev/null || true)
if (( ${#dumps[@]} > BACKUP_RETENTION_COUNT )); then
    for stale in "${dumps[@]:BACKUP_RETENTION_COUNT}"; do
        info "pruning $stale (and its key ring, if any)"
        rm -f -- "$stale" "${stale%.dump}-dataprotection.tar"
    done
fi

# ---------------------------------------------------------------------------------------------------
# Report.
# ---------------------------------------------------------------------------------------------------
echo
info "backup complete. Rehearse it with: scripts/restore_drill.sh"
if (( WITH_KEYS )) && (( ! KEYS_CAPTURED )); then
    warn "this set is INCOMPLETE — database only, no key ring (exit 2)."
    exit 2
fi
exit 0
