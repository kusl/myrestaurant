#!/usr/bin/env bash
#
# Restore drill (TECHNICAL_SPECIFICATION §15, OPERATIONS §6). Proves a recovery set can come back,
# without a maintenance window, a scratch host, or touching the live stack.
#
#   scripts/restore_drill.sh               drill the newest set in BACKUP_DIRECTORY
#   scripts/restore_drill.sh --dump <path> drill a specific dump (and its sibling key ring)
#   scripts/restore_drill.sh --from-live   take a fresh set with scripts/backup.sh, then drill it
#   scripts/restore_drill.sh --strict      ignored pg_restore errors and an empty key ring fail
#   scripts/restore_drill.sh --keep        leave the scratch container running for inspection
#   scripts/restore_drill.sh --no-keys     do not expect a key ring beside the dump
#   scripts/restore_drill.sh --help        this text
#
# IT NEVER WRITES TO THE LIVE DATABASE. Every restore targets a container this script creates,
# names before anything happens, and destroys on the way out. Only --from-live goes near the live
# instance, and it delegates to scripts/backup.sh, which only reads.
#

set -euo pipefail
cd "$(dirname "$0")/.."

DUMP=""
FROM_LIVE=0
STRICT=0
KEEP=0
WITH_KEYS=1

while (( $# > 0 )); do
    case "$1" in
        --dump)
            shift
            [[ $# -gt 0 ]] || { echo "error: --dump needs a path." >&2; exit 1; }
            DUMP="$1"
            ;;
        --from-live) FROM_LIVE=1 ;;
        --strict)    STRICT=1 ;;
        --keep)      KEEP=1 ;;
        --no-keys)   WITH_KEYS=0 ;;
        --help | -h)
            awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$0"
            exit 0
            ;;
        *)
            echo "error: unknown argument '$1' (try --help)." >&2
            exit 1
            ;;
    esac
    shift
done

BACKUP_DIRECTORY="${BACKUP_DIRECTORY:-/var/lib/myrestaurant/backups}"
PGUSER="${POSTGRES_USER:-myrestaurant}"
PGDB="${POSTGRES_DB:-myrestaurant}"
DRILL_IMAGE="${DRILL_POSTGRES_IMAGE:-docker.io/library/postgres:17-alpine}"
MIGRATION_DIRECTORY="src/MyRestaurant.DataAccess/Migrations"

PASS_COUNT=0
WARN_COUNT=0
FAIL_COUNT=0

info() { printf '[drill] %s\n' "$*"; }
gate() { printf '\n[drill] ─── %s\n' "$*"; }
pass() { PASS_COUNT=$(( PASS_COUNT + 1 )); printf '[drill]     PASS  %s\n' "$*"; }
soft() { WARN_COUNT=$(( WARN_COUNT + 1 )); printf '[drill]     WARN  %s\n' "$*"; }
bad()  { FAIL_COUNT=$(( FAIL_COUNT + 1 )); printf '[drill]     FAIL  %s\n' "$*"; }
die()  { printf '[drill] error: %s\n' "$*" >&2; exit 1; }

reserve() {
    if (( STRICT )); then
        bad "$*"
    else
        soft "$*"
    fi
}

if [[ -n "${CONTAINER_ENGINE:-}" ]]; then
    command -v "$CONTAINER_ENGINE" >/dev/null 2>&1 \
        || die "CONTAINER_ENGINE='$CONTAINER_ENGINE' is not on PATH."
    ENGINE="$CONTAINER_ENGINE"
elif command -v podman >/dev/null 2>&1; then
    ENGINE="podman"
elif command -v docker >/dev/null 2>&1; then
    ENGINE="docker"
else
    die "need podman or docker on PATH (or set CONTAINER_ENGINE)."
fi

if (( FROM_LIVE )); then
    if [[ -n "$DUMP" ]]; then
        die "--from-live and --dump are mutually exclusive."
    fi
    info "taking a fresh set with scripts/backup.sh…"
    backup_status=0
    bash scripts/backup.sh || backup_status=$?
    case "$backup_status" in
        0 | 2) ;;
        *) die "scripts/backup.sh exited $backup_status — nothing to drill." ;;
    esac
fi

if [[ -z "$DUMP" ]]; then
    mapfile -t candidates < <(ls -1t "$BACKUP_DIRECTORY"/myrestaurant-*.dump 2>/dev/null || true)
    (( ${#candidates[@]} > 0 )) || die "no myrestaurant-*.dump in '$BACKUP_DIRECTORY' (pass --dump, or --from-live)."
    DUMP="${candidates[0]}"
fi

[[ -f "$DUMP" ]] || die "'$DUMP' is not a file."
[[ -s "$DUMP" ]] || die "'$DUMP' is empty."

KEYS_ARCHIVE="${DUMP%.dump}-dataprotection.tar"

mapfile -t MIGRATIONS < <(find "$MIGRATION_DIRECTORY" -maxdepth 1 -name '*.sql' -type f | sort)
(( ${#MIGRATIONS[@]} > 0 )) || die "no migrations under '$MIGRATION_DIRECTORY' — run this from the repository."

echo
info "drilling      $DUMP  ($(du -h -- "$DUMP" | cut -f1))"
info "key ring      ${KEYS_ARCHIVE}$( [[ -f "$KEYS_ARCHIVE" ]] && echo "" || echo "  (missing)" )"
info "migrations    ${#MIGRATIONS[@]} file(s) in $MIGRATION_DIRECTORY"
info "scratch image $DRILL_IMAGE"
info "engine        $ENGINE$( [[ -n "${CONTAINER_ENGINE:-}" ]] && echo "  (CONTAINER_ENGINE)" || echo "" )"
info "strict mode   $( (( STRICT )) && echo "on" || echo "off" )"
info "on exit       $( (( KEEP )) && echo "the scratch container is KEPT (--keep)" || echo "the scratch container is removed" )"

SCRATCH="myrestaurant-restore-drill-$$"
RESTORE_LOG="$(mktemp "${TMPDIR:-/tmp}/myrestaurant-drill.XXXXXX")"

trap 'rm -f -- "$RESTORE_LOG" 2>/dev/null || true
      if (( KEEP )); then
          printf "\n[drill] --keep: %s is still running. Remove it with: %s rm --force %s\n" \
              "$SCRATCH" "$ENGINE" "$SCRATCH"
      else
          "$ENGINE" rm --force "$SCRATCH" >/dev/null 2>&1 || true
      fi' EXIT

gate "scratch database"
info "starting $SCRATCH (no ports published, no volume mounted)…"
"$ENGINE" run --detach --name "$SCRATCH" \
    --env POSTGRES_USER="$PGUSER" \
    --env POSTGRES_PASSWORD="restore-drill-$$" \
    --env POSTGRES_DB="$PGDB" \
    "$DRILL_IMAGE" >/dev/null \
    || die "could not start the scratch container from '$DRILL_IMAGE'."

scratch_ready=0
deadline=$(( $(date +%s) + 90 ))
while (( $(date +%s) < deadline )); do
    if "$ENGINE" exec "$SCRATCH" pg_isready --username "$PGUSER" --dbname "$PGDB" >/dev/null 2>&1; then
        scratch_ready=1
        break
    fi
    sleep 2
done

if (( scratch_ready )); then
    pass "scratch database is accepting connections"
else
    bad "the scratch database never became ready"
    "$ENGINE" logs "$SCRATCH" 2>&1 | tail -n 20 >&2 || true
    die "cannot drill without a scratch database."
fi

scratch_query() {
    "$ENGINE" exec "$SCRATCH" psql \
        --username "$PGUSER" --dbname "$PGDB" \
        --no-align --tuples-only --quiet --set=ON_ERROR_STOP=1 --command "$1"
}

gate "gate A · archive is a readable custom-format dump"

toc=""
if ! toc="$("$ENGINE" exec --interactive "$SCRATCH" pg_restore --list < "$DUMP" 2>&1)"; then
    bad "pg_restore --list could not read the archive"
    printf '%s\n' "$toc" | tail -n 10 >&2
else
    toc_entries="$(printf '%s\n' "$toc" | grep -c -v -e '^;' -e '^$' || true)"
    if (( toc_entries > 0 )); then
        pass "table of contents lists $toc_entries entries"
    else
        bad "the archive listed no objects at all"
    fi
fi

gate "gate B · restores into an empty database"

restore_status=0
"$ENGINE" exec --interactive "$SCRATCH" \
    pg_restore --clean --if-exists --no-owner --username "$PGUSER" --dbname "$PGDB" \
    < "$DUMP" > "$RESTORE_LOG" 2>&1 || restore_status=$?

ignored="$(grep -oE 'errors ignored on restore: [0-9]+' "$RESTORE_LOG" | grep -oE '[0-9]+$' | tail -n1 || true)"
[[ -n "$ignored" ]] || ignored=0

if (( restore_status == 0 )); then
    pass "pg_restore completed with no errors"
else
    reserve "pg_restore exited $restore_status and ignored $ignored error(s)"
    info "    last lines of the restore log:"
    tail -n 8 "$RESTORE_LOG" | sed 's/^/[drill]       /'
fi

gate "gate C · every table and view the migrations declare is present"

mapfile -t expected_tables < <(
    grep -hoE '^CREATE TABLE( IF NOT EXISTS)? [a-z_]+' "${MIGRATIONS[@]}" | awk '{ print $NF }' | sort -u
)
mapfile -t expected_views < <(
    grep -hoE '^CREATE (OR REPLACE )?VIEW( IF NOT EXISTS)? [a-z_]+' "${MIGRATIONS[@]}" | awk '{ print $NF }' | sort -u
)

if (( ${#expected_tables[@]} == 0 )) || (( ${#expected_views[@]} == 0 )); then
    bad "read ${#expected_tables[@]} table(s) and ${#expected_views[@]} view(s) out of the migrations"
    info "    the DDL no longer matches the anchored 'CREATE TABLE x' / 'CREATE VIEW x' patterns this"
    info "    gate reads. Fix the patterns in this script — do not hard-code a list."
fi

mapfile -t actual_tables < <(scratch_query \
    "SELECT table_name FROM information_schema.tables
      WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY table_name" 2>/dev/null || true)
mapfile -t actual_views < <(scratch_query \
    "SELECT table_name FROM information_schema.views
      WHERE table_schema = 'public' ORDER BY table_name" 2>/dev/null || true)

contains() {
    local needle="$1"; shift
    local item
    for item in "$@"; do
        [[ "$item" == "$needle" ]] && return 0
    done
    return 1
}

present_tables=()
missing_tables=()
for expected in "${expected_tables[@]}"; do
    if contains "$expected" ${actual_tables[@]+"${actual_tables[@]}"}; then
        present_tables+=("$expected")
    else
        missing_tables+=("$expected")
    fi
done

missing_views=()
present_views=()
for expected in "${expected_views[@]}"; do
    if contains "$expected" ${actual_views[@]+"${actual_views[@]}"}; then
        present_views+=("$expected")
    else
        missing_views+=("$expected")
    fi
done

if (( ${#missing_tables[@]} == 0 )) && (( ${#expected_tables[@]} > 0 )); then
    pass "all ${#expected_tables[@]} tables present"
else
    bad "${#missing_tables[@]} table(s) missing: ${missing_tables[*]-}"
fi

if (( ${#missing_views[@]} == 0 )) && (( ${#expected_views[@]} > 0 )); then
    pass "all ${#expected_views[@]} views present"
else
    bad "${#missing_views[@]} view(s) missing: ${missing_views[*]-}"
fi

for actual in ${actual_tables[@]+"${actual_tables[@]}"}; do
    if [[ "$actual" == "schemaversions" ]]; then
        continue
    fi
    if ! contains "$actual" ${expected_tables[@]+"${expected_tables[@]}"}; then
        info "    note: '$actual' exists in the dump but no migration declares it"
    fi
done

gate "gate D · DbUp journal carries one row per migration file"

mapfile -t journal < <(scratch_query "SELECT scriptname FROM schemaversions ORDER BY scriptname" 2>/dev/null || true)

if (( ${#journal[@]} == 0 )); then
    bad "the restored database has no readable 'schemaversions' rows"
    info "    either the dump predates DbUp ever running, or the journal table did not come back."
else
    unjournalled=()
    for migration in "${MIGRATIONS[@]}"; do
        migration_name="$(basename -- "$migration")"
        found=0
        for entry in "${journal[@]}"; do
            if [[ "$entry" == *"$migration_name" ]]; then
                found=1
                break
            fi
        done
        (( found )) || unjournalled+=("$migration_name")
    done

    if (( ${#unjournalled[@]} == 0 )); then
        pass "${#MIGRATIONS[@]} migration(s) journalled, ${#journal[@]} row(s) in schemaversions"
    else
        bad "not journalled: ${unjournalled[*]}"
        info "    this code would treat the restored database as needing migration, and"
        info "    /healthz/ready would report not-current until it ran."
    fi
fi

gate "gate E · every projection view resolves"

broken_views=()
for view in ${present_views[@]+"${present_views[@]}"}; do
    if ! scratch_query "SELECT count(*) FROM $view" >/dev/null 2>&1; then
        broken_views+=("$view")
    fi
done

if (( ${#present_views[@]} == 0 )); then
    bad "no views to query"
elif (( ${#broken_views[@]} == 0 )); then
    pass "${#present_views[@]} view(s) queryable"
else
    bad "view(s) that do not resolve: ${broken_views[*]}"
fi

gate "gate F · row census (reported, not asserted)"

if (( ${#present_tables[@]} == 0 )); then
    soft "no tables to count"
else
    census_sql=""
    for table in "${present_tables[@]}"; do
        [[ -n "$census_sql" ]] && census_sql+=" UNION ALL "
        census_sql+="SELECT '${table}' AS relation, count(*) AS row_count FROM ${table}"
    done
    census_sql="SELECT relation, row_count FROM ( $census_sql ) AS census ORDER BY relation"

    if census="$(scratch_query "$census_sql" 2>/dev/null)"; then
        printf '%s\n' "$census" | awk -F'|' '{ printf "[drill]       %-46s %8s\n", $1, $2 }'
        people="$(printf '%s\n' "$census" | awk -F'|' '$1 == "person" { print $2 }')"
        if [[ "${people:-0}" == "0" ]]; then
            soft "the restored database has no person rows — this dump predates /setup"
        else
            pass "census read; $people person row(s)"
        fi
    else
        bad "the census query failed"
    fi
fi

gate "gate G · the Data Protection key ring is beside the dump"

if (( ! WITH_KEYS )); then
    soft "skipped (--no-keys)"
elif [[ ! -f "$KEYS_ARCHIVE" ]]; then
    bad "no key ring at '$KEYS_ARCHIVE'"
    info "    §15 requires the key ring alongside the database. scripts/backup.sh writes it as a"
    info "    sibling tar with the same timestamp; a set without one cannot decrypt a TOTP secret."
elif ! tar -tf "$KEYS_ARCHIVE" >/dev/null 2>&1; then
    bad "'$KEYS_ARCHIVE' is not a readable tar archive"
else
    key_count="$(tar -tf "$KEYS_ARCHIVE" 2>/dev/null | grep -c 'key-.*\.xml$' || true)"
    if (( key_count > 0 )); then
        pass "key ring readable, $key_count key file(s)"
    else
        reserve "the key ring is readable but contains no key-*.xml (nothing has been protected yet)"
    fi
fi

echo
info "────────────────────────────────────────────────────────────────────────"
info "  restore drill: $PASS_COUNT passed, $WARN_COUNT warned, $FAIL_COUNT failed"
info "  dump:          $DUMP"
info "────────────────────────────────────────────────────────────────────────"

if (( FAIL_COUNT > 0 )); then
    info "this set would NOT bring the instance back cleanly. Read the FAIL lines above."
    exit 1
fi

if (( WARN_COUNT > 0 )); then
    info "this set restores, with the reservations above. Re-run with --strict to fail on them."
fi

info "drilled without touching the live database."
exit 0
