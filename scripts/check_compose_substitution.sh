#!/usr/bin/env bash
#
# Does this host's compose engine apply the DEFAULT VALUES in compose.yaml?
# (TECHNICAL_SPECIFICATION §14.1, §16.4, F-57.) Reads the tree and the engine; changes nothing.
#
#   scripts/check_compose_substitution.sh          check, and explain a failure
#   scripts/check_compose_substitution.sh --quiet  say nothing unless it fails
#   scripts/check_compose_substitution.sh --help   this text
#
# Exit status:
#   0  nothing depends on the engine's defaults, or the engine applies them
#   3  THE FINDING: the engine leaves `${NAME:-default}` in place as literal text
#   2  could not be determined here (no compose, or no usable `config` subcommand)
#   1  usage error, or this is not a checkout of the repository
#
# WHY THIS EXISTS
#
# `compose.yaml` sets twenty-three values with the `${NAME:-default}` form. On 2026-08-10, on Debian
# trixie's podman-compose — which ADR-0004 calls the canonical engine — every one of those whose
# variable was not already set in the environment reached the container AS THE PLACEHOLDER TEXT:
#
#   Configuration error: RESTAURANT_TIME_ZONE '${RESTAURANT_TIME_ZONE:-America/New_York}' is not a
#   resolvable time zone on this host.
#
# Five of those the application validates, so it printed five errors and exited 1. The rest arrived
# just as wrong and silently: `RESTAURANT_NAME` would have rendered the placeholder text as the
# restaurant's name on every page, the four Argon2 parameters fell back to compiled-in defaults
# because an unparseable integer is indistinguishable from an absent one, and
# `OTEL_EXPORTER_OTLP_ENDPOINT` — whose default is EMPTY, and whose emptiness is what switches the
# exporter off — arrived non-empty, which switches it on and points it at a hostname made of braces.
#
# And the database did not come up at all. `POSTGRES_USER` reached initdb as literal text, so the
# bootstrap statement failed on the punctuation in it:
#
#   FATAL:  invalid character in extension owner: must not contain any of ""$'\"
#   STATEMENT:  CREATE EXTENSION plpgsql;
#   initdb: removing contents of data directory "/var/lib/postgresql/data"
#
# — after which the container exited, the engine restarted it, and initdb wiped and retried, forever.
# One engine behaviour, two containers, and no message anywhere naming the cause.
#
# WHAT IS AND IS NOT KNOWN ABOUT THE BEHAVIOUR
#
# Known, because it was observed in the same run: substitution WORKS when the variable is set in the
# process environment. `RESTAURANT_PUBLIC_ORIGIN` is exported by scripts/dev_instance.sh and it was
# the one variable that arrived correct. So it is specifically the DEFAULT branch — the part after
# `:-` — that is not applied.
#
# Not known from a transcript, and the reason this script asks the engine rather than assuming: which
# releases behave this way, and whether assigning a variable EMPTY in `.env` counts as supplying it.
# So nothing here predicts. It renders the file through the engine and reads the answer.
#
# WHAT A FAILURE MEANS FOR AN OPERATOR
#
# It is not a formatting problem, and it is not confined to the five variables that produce an error.
# Everything the stack is configured with is affected, and most of it fails silently. Do not run an
# instance you care about on an engine that fails this check.

set -euo pipefail
cd "$(dirname "$0")/.."

QUIET=0
while (( $# > 0 )); do
    case "$1" in
        --quiet | -q) QUIET=1 ;;
        --help | -h)
            awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$0"
            exit 0
            ;;
        *)
            printf 'error: unknown argument %s (try --help).\n' "$1" >&2
            exit 1
            ;;
    esac
    shift
done

say()  { (( QUIET )) || printf '[compose-substitution] %s\n' "$*" >&2; }
tell() { printf '[compose-substitution] %s\n' "$*" >&2; }

[[ -f "compose.yaml" ]] || {
    tell "compose.yaml is not here; run this from a checkout of the repository."
    exit 1
}

# ---------------------------------------------------------------------------------------------------
# 1. The engine's compose command, chosen the way scripts/dev_instance.sh chooses it (F-43): the
#    engine first, then a compose that matches it.
# ---------------------------------------------------------------------------------------------------
if [[ -n "${CONTAINER_ENGINE:-}" ]] && command -v "$CONTAINER_ENGINE" >/dev/null 2>&1; then
    ENGINE="$CONTAINER_ENGINE"
elif command -v podman >/dev/null 2>&1; then
    ENGINE="podman"
elif command -v docker >/dev/null 2>&1; then
    ENGINE="docker"
else
    tell "no podman and no docker on PATH; nothing to ask."
    exit 2
fi

if [[ "$ENGINE" == "podman" ]]; then
    if command -v podman-compose >/dev/null 2>&1; then
        COMPOSE=(podman-compose)
    elif podman compose version >/dev/null 2>&1; then
        COMPOSE=(podman compose)
    else
        tell "podman is on PATH but no compose is; nothing to ask."
        exit 2
    fi
elif docker compose version >/dev/null 2>&1; then
    COMPOSE=(docker compose)
else
    tell "docker is on PATH but 'docker compose' is not available; nothing to ask."
    exit 2
fi

# ---------------------------------------------------------------------------------------------------
# 2. Which variables actually depend on the engine's defaults right now
#
# A placeholder whose variable is set in the environment, or assigned in `.env`, does not need the
# default branch — and the set branch is the half that was observed working. So the question is only
# about the remainder. When the remainder is empty there is nothing to check and this exits 0, which
# is the case on any host where `.env` has been copied and every key filled in.
# ---------------------------------------------------------------------------------------------------
placeholders="$(grep --only-matching --extended-regexp '\$\{[A-Za-z_][A-Za-z0-9_]*' compose.yaml \
    | cut -c3- | sort --unique || true)"

if [[ -z "$placeholders" ]]; then
    say "compose.yaml interpolates nothing; there is no default to apply."
    exit 0
fi

env_assigned=""
if [[ -f ".env" ]]; then
    env_assigned="$(grep --extended-regexp '^[[:space:]]*[A-Za-z_][A-Za-z0-9_]*=' .env \
        | sed --expression='s/^[[:space:]]*//' --expression='s/=.*//' | sort --unique || true)"
fi

depends_on_defaults=""
while IFS= read -r name; do
    [[ -n "$name" ]] || continue
    # Set and non-empty in this process's environment?
    if [[ -n "${!name:-}" ]]; then
        continue
    fi
    # Assigned in .env, even to an empty value? Whether an empty assignment is enough is exactly what
    # is not known from outside the engine, so it counts as supplied here and the render below is what
    # settles it.
    if [[ -n "$env_assigned" ]] && printf '%s\n' "$env_assigned" | grep --quiet --line-regexp --fixed-strings "$name"; then
        continue
    fi
    depends_on_defaults="${depends_on_defaults}${name}"$'\n'
done <<< "$placeholders"

depends_count="$(printf '%s' "$depends_on_defaults" | grep --count . || true)"

if (( depends_count == 0 )); then
    say "every variable compose.yaml interpolates is supplied by the environment or .env;"
    say "  nothing here depends on the engine applying a default."
    exit 0
fi

say "${depends_count} of the variables compose.yaml interpolates are not supplied by the environment"
say "  or by .env, so they depend on the engine applying the default after ':-'. Asking the engine…"

# ---------------------------------------------------------------------------------------------------
# 3. Ask the engine, under a deadline
#
# `config` renders the file the way the engine will hand it to the containers. A surviving `${` in
# that render is the finding, stated by the engine itself rather than predicted from a version number.
# ---------------------------------------------------------------------------------------------------
rendered=""
render_status=0
if command -v timeout >/dev/null 2>&1; then
    rendered="$(timeout --kill-after=10s 60s "${COMPOSE[@]}" config 2>/dev/null)" || render_status=$?
else
    rendered="$("${COMPOSE[@]}" config 2>/dev/null)" || render_status=$?
fi

if (( render_status != 0 )) || [[ -z "$rendered" ]]; then
    tell "'${COMPOSE[*]} config' did not render (exit ${render_status}), so this could not be decided here."
    tell "  scripts/dev_instance.sh re-checks it from the containers' own environment after 'up',"
    tell "  which is ground truth and needs no subcommand. Nothing is wrong yet."
    exit 2
fi

surviving="$(printf '%s\n' "$rendered" \
    | grep --only-matching --extended-regexp '\$\{[A-Za-z_][A-Za-z0-9_]*' \
    | cut -c3- | sort --unique || true)"

if [[ -z "$surviving" ]]; then
    say "the engine applied every default. Nothing to do."
    exit 0
fi

# ---------------------------------------------------------------------------------------------------
# 4. The finding
# ---------------------------------------------------------------------------------------------------
tell ""
tell "THIS ENGINE DOES NOT APPLY THE DEFAULTS IN compose.yaml (F-57)."
tell ""
tell "  engine:   ${ENGINE}"
tell "  compose:  ${COMPOSE[*]}"
tell ""
tell "  These variables would reach the containers as literal placeholder text:"
while IFS= read -r name; do
    [[ -n "$name" ]] || continue
    tell "    \${${name}:-…}"
done <<< "$surviving"
tell ""
tell "  What that costs, from the run that found it: the application refuses to start and names"
tell "  five of them; POSTGRES_USER reaches initdb as punctuation, so the database never"
tell "  initialises and crash-loops; and the rest are wrong in silence — RESTAURANT_NAME renders"
tell "  the placeholder as the restaurant's name, an unparseable integer is indistinguishable from"
tell "  an absent one, and OTEL_EXPORTER_OTLP_ENDPOINT arrives non-empty, which turns the exporter"
tell "  ON and points it at a hostname made of braces."
tell ""
tell "  Two remediations, in order:"
tell ""
tell "    1. cp .env.example .env"
tell "       Every variable compose.yaml interpolates is assigned in that file, so nothing is left"
tell "       depending on a default. Then run this check again — it will tell you whether it was"
tell "       enough on your engine, which is not something this repository can know for you."
tell ""
tell "    2. Use a compose that applies defaults:"
tell "         podman compose …            the Docker Compose provider, if it is installed"
tell "         pipx install podman-compose a newer release than the distribution's"
tell "       or export the variables in the shell that runs the stack."
tell ""
tell "  Do not run an instance you care about on an engine that fails this check. Only five of the"
tell "  affected settings are ones this application validates; the others do not announce themselves."
tell ""
exit 3
