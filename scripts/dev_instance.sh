#!/usr/bin/env bash
#
# A long-lived demo instance behind a Cloudflare quick tunnel, on a host with no .NET SDK
# (TECHNICAL_SPECIFICATION §14.3a, ADR-0004, ADR-0005). Everything runs in containers; this
# script exits and leaves them running.
#
#   scripts/dev_instance.sh            bring it up, print the public URL, hold briefly, EXIT
#   scripts/dev_instance.sh up         the same thing, said out loud
#   scripts/dev_instance.sh url        print the current public URL and nothing else
#   scripts/dev_instance.sh status     what is running, and where
#   scripts/dev_instance.sh logs       the tunnel's log        (--follow to stream it)
#   scripts/dev_instance.sh down       stop the tunnel and the stack
#   scripts/dev_instance.sh --help     this text
#
# HOW THIS DIFFERS FROM scripts/quick_tunnel.sh, AND WHY BOTH EXIST
#
# `quick_tunnel.sh` runs cloudflared as a CHILD of the shell and blocks on it, so the URL lives
# exactly as long as the terminal does. That is the right shape for a demo you are standing in
# front of, and the wrong shape for the case this script is for: a spare machine on the LAN,
# reached over SSH, serving a build that testers will use for days. There, the terminal is a
# remote session that will be closed, and a tunnel owned by it dies with it.
#
# So the tunnel here is a DETACHED CONTAINER rather than a child process. Nothing this script
# starts is its own descendant, which is what lets it exit while the instance keeps serving.
# `up` therefore ends in a settle phase — it re-probes the public URL for a few seconds after
# printing it, so the terminal is released on evidence that the instance answers from the
# public internet rather than on the fact that a command returned.
#
# Two consequences of the tunnel outliving the shell, both deliberate:
#
#   • The URL survives `up`, so a second `up` REUSES it instead of minting a new one. That is
#     the difference that matters to a tester: a re-registered passkey is worth keeping, and a
#     new random hostname throws every one of them away. Pass --new-url when you want a fresh
#     hostname on purpose.
#   • Nothing else will ever stop it. `down` is not optional housekeeping here — it is the only
#     thing that closes the tunnel.
#
# NO CALL IN HERE MAY OWN THE TERMINAL INDEFINITELY (F-53)
#
# The first run of this script hung, silently, forever — and not in anything below. It hung inside
# `podman-compose up -d`. podman-compose 1.3.0, which is what Debian trixie ships, implements
# `up -d` as `podman run -d` for every container FOLLOWED BY a wait on each dependency's
# `depends_on` condition, in an unbounded `while True:` retry loop that logs at debug level and
# prints nothing at all. So the stack was up, the tunnel was open, the public URL was serving —
# and the command never returned, with no output to say why.
#
# `compose.yaml` no longer asks for a health condition, which removes that cause. The shape of the
# failure is the lasting lesson though: a script whose entire purpose is to hand the terminal back
# must not contain a call that can keep it. So every compose invocation here runs under a deadline,
# and when one trips this script says so, reports what the containers are actually doing, repairs
# anything that was created but not started, and goes on to verify readiness itself — because a
# compose command that did not return is not the same thing as a stack that did not start.
#
# THE HOST THIS TARGETS
#
# Debian, rootless Podman, podman-compose, and no .NET SDK — `run.sh` is unusable there, since
# its default and --smoke modes both need `dotnet` on the host. Nothing below calls `dotnet`:
# the image is built by the SDK container the Containerfile names.
#
# ORDER OF OPERATIONS, AND THE ONE THING IT FIXES
#
# The image is built FIRST, before the tunnel opens. `quick_tunnel.sh` opens the tunnel first
# and builds afterwards, which on a cold host means the URL is printed and then unreachable for
# as long as the build takes — measured at nineteen minutes on the machine this script was
# written for. Building first costs nothing and closes that window to the time it takes to
# start two containers.
#
# The public origin is therefore known BEFORE `web` is created, which is the other half of the
# same reordering: `RESTAURANT_PUBLIC_ORIGIN` is exported and then the stack comes up once,
# rather than coming up with a placeholder origin and being force-recreated with the real one.
# Worth knowing about the flag this avoids: podman-compose implements `up --force-recreate web`
# as a `down` of the WHOLE project followed by an `up` of the named service, so what looks like
# recreating one container restarts the database too. The engine still recreates when it has
# to — the config hash it labels containers with is computed after interpolation, so a changed
# origin is a changed hash — and that is the mechanism this script relies on instead.
#
# LOGGING OUT AFTERWARDS
#
# Rootless containers belong to your user session. Debian's logind default (KillUserProcesses=no)
# leaves them alone when you disconnect, but the durable answer is `loginctl enable-linger`,
# which OPERATIONS §2 already asks for on a production host. This script reports which of the
# two you are relying on rather than assuming.
#
# WHAT A QUICK TUNNEL IS STILL NOT FOR
#
# Passkeys work here — the relying-party ID is derived per request and `https://*.trycloudflare.com`
# is trusted by default (ADR-0005, §3.3) — but the hostname is random per tunnel and on the Public
# Suffix List, so passkeys registered against one URL are worthless against the next. Never
# bootstrap a real instance (§3.6) through a quick tunnel: the first administrator's credentials
# would not survive the first `--new-url`. Use the named tunnel (CLOUDFLARE_TUNNEL_TOKEN) for that.
#
# Environment:
#   TUNNEL_TARGET                  what cloudflared points at (default http://localhost:8080)
#   TUNNEL_URL_WAIT                seconds to wait for the tunnel URL          (default 120)
#   DEV_INSTANCE_COMPOSE_WAIT      seconds any one compose command may take    (default 240)
#   DEV_INSTANCE_BUILD_WAIT        seconds the image build may take            (default 5400)
#   DEV_INSTANCE_READY_WAIT        seconds to wait for /healthz/ready          (default 300)
#   DEV_INSTANCE_SETTLE_SECONDS    seconds to re-probe the public URL before exiting (default 20)
#   DEV_INSTANCE_TUNNEL_CONTAINER  the tunnel container's name (default myrestaurant_quicktunnel)
#   DEV_INSTANCE_STATE_DIRECTORY   where the URL is recorded
#                                  (default ${XDG_STATE_HOME:-~/.local/state}/myrestaurant)
#   CLOUDFLARED_IMAGE              fully qualified, so a short-name registry prompt cannot hang an
#                                  unattended bring-up (default docker.io/cloudflare/cloudflared:latest)
#   CONTAINER_ENGINE               force podman or docker instead of taking the first on PATH
#   COMPOSE_PROJECT_NAME           overrides the project name the engine labels containers with
#   SOURCE_REVISION                stamped into the image; defaults to the checked-out commit

set -euo pipefail
cd "$(dirname "$0")/.."

# ---------------------------------------------------------------------------------------------------
# 0. Arguments
# ---------------------------------------------------------------------------------------------------
COMMAND="up"
COMMAND_GIVEN=0
NO_BUILD=0
NO_CACHE=0
NEW_URL=0
FOLLOW=0

while (( $# > 0 )); do
    case "$1" in
        up | url | status | logs | down)
            if (( COMMAND_GIVEN )); then
                echo "error: '$COMMAND' and '$1' are both commands; give one (try --help)." >&2
                exit 1
            fi
            COMMAND="$1"
            COMMAND_GIVEN=1
            ;;
        --no-build)      NO_BUILD=1 ;;
        --no-cache)      NO_CACHE=1 ;;
        --new-url)       NEW_URL=1 ;;
        --follow | -f)   FOLLOW=1 ;;
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

# ---------------------------------------------------------------------------------------------------
# 1. Settings
# ---------------------------------------------------------------------------------------------------
TUNNEL_TARGET="${TUNNEL_TARGET:-http://localhost:8080}"
URL_WAIT="${TUNNEL_URL_WAIT:-120}"
COMPOSE_WAIT="${DEV_INSTANCE_COMPOSE_WAIT:-240}"
BUILD_WAIT="${DEV_INSTANCE_BUILD_WAIT:-5400}"
READY_WAIT="${DEV_INSTANCE_READY_WAIT:-300}"
SETTLE_SECONDS="${DEV_INSTANCE_SETTLE_SECONDS:-20}"
TUNNEL_CONTAINER="${DEV_INSTANCE_TUNNEL_CONTAINER:-myrestaurant_quicktunnel}"
CLOUDFLARED_IMAGE="${CLOUDFLARED_IMAGE:-docker.io/cloudflare/cloudflared:latest}"
STATE_DIRECTORY="${DEV_INSTANCE_STATE_DIRECTORY:-${XDG_STATE_HOME:-${HOME}/.local/state}/myrestaurant}"
STATE_FILE="${STATE_DIRECTORY}/dev-instance.env"

PUBLIC_URL=""

log()  { printf '[dev-instance] %s\n' "$*" >&2; }
warn() { printf '[dev-instance] warning: %s\n' "$*" >&2; }
die()  { printf '[dev-instance] error: %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------------------------------
# 2. Engine and compose command
#
# The engine is chosen first and the compose command is chosen to MATCH it, rather than each being
# picked independently off PATH. On a host with both engines those two searches can disagree — the
# stack ends up in one engine's store and `logs`, `exec` and `rm` go looking in the other's, which
# is F-43 with a different pair of commands.
# ---------------------------------------------------------------------------------------------------
if [[ -n "${CONTAINER_ENGINE:-}" ]]; then
    command -v "$CONTAINER_ENGINE" >/dev/null 2>&1 \
        || die "CONTAINER_ENGINE is '${CONTAINER_ENGINE}' and that is not on PATH."
    ENGINE="$CONTAINER_ENGINE"
elif command -v podman >/dev/null 2>&1; then
    ENGINE="podman"
elif command -v docker >/dev/null 2>&1; then
    ENGINE="docker"
else
    die "need podman (preferred) or docker on PATH."
fi

if [[ "$ENGINE" == "podman" ]]; then
    if command -v podman-compose >/dev/null 2>&1; then
        COMPOSE=(podman-compose)
    elif podman compose version >/dev/null 2>&1; then
        COMPOSE=(podman compose)
    else
        die "podman is on PATH but no compose is: install podman-compose (Debian: 'sudo apt install podman-compose')."
    fi
elif docker compose version >/dev/null 2>&1; then
    COMPOSE=(docker compose)
else
    die "docker is on PATH but 'docker compose' is not available."
fi

# The project name both engines derive from the directory this file sits in, unless the environment
# overrides it. It is used only to NARROW container discovery below, so nothing breaks if the
# derivation is wrong: discovery falls back to the service label on its own.
PROJECT_NAME="${COMPOSE_PROJECT_NAME:-}"
if [[ -z "$PROJECT_NAME" ]]; then
    PROJECT_NAME="$(basename "$PWD" | tr '[:upper:]' '[:lower:]' | tr --complement --delete 'a-z0-9_-')"
fi

# ---------------------------------------------------------------------------------------------------
# 3. Compose, under a deadline (F-53)
#
# `timeout` is coreutils and present on every host this project targets; where it is not, the call
# runs unguarded and the preflight says so, because silently dropping a safety net is worse than
# not having one. 124 is timeout's own "the deadline passed"; 137 is what it reports when SIGTERM
# was ignored and it escalated to SIGKILL. Both mean the same thing to a caller here.
#
# Killing a compose command is safe in the one way that matters: the containers it has already
# created belong to the engine, not to this shell, so they keep running. That is the same property
# the detached tunnel relies on.
# ---------------------------------------------------------------------------------------------------
HAVE_TIMEOUT=0
if command -v timeout >/dev/null 2>&1; then
    HAVE_TIMEOUT=1
fi

# compose_guarded <seconds> <compose arguments...>
compose_guarded() {
    local seconds="$1"
    shift

    local status=0
    if (( HAVE_TIMEOUT )); then
        timeout --kill-after=15s "${seconds}s" "${COMPOSE[@]}" "$@" || status=$?
        if (( status == 124 || status == 137 )); then
            return 124
        fi
    else
        "${COMPOSE[@]}" "$@" || status=$?
    fi

    return "$status"
}

report_deadline() {
    local seconds="$1"
    shift
    warn "'${COMPOSE[*]} $*' did not return within ${seconds}s and was stopped."
    warn "  That is F-53's shape: podman-compose 1.3.0 starts every container and THEN waits on"
    warn "  each depends_on condition in an unbounded loop, printing nothing while it waits. The"
    warn "  containers are normally already running by then, so this is not fatal by itself."
}

# ---------------------------------------------------------------------------------------------------
# 4. Container helpers
#
# The name of the compose-managed container for a service, or empty. Found by LABEL rather than by
# guessing at "<project>_<service>_1", because the naming scheme is the engine's business and both
# engines label what they create. The project label is tried first, so that a `web` service
# belonging to a different compose project on the same host cannot be mistaken for this one; the
# service label alone is the fallback, because the project name is derived here and the engine is
# the authority on it.
# ---------------------------------------------------------------------------------------------------
compose_container() {
    local service="$1" found=""

    if [[ -n "$PROJECT_NAME" ]]; then
        found="$("$ENGINE" ps --all \
            --filter "label=com.docker.compose.project=${PROJECT_NAME}" \
            --filter "label=com.docker.compose.service=${service}" \
            --format '{{.Names}}' 2>/dev/null | head -n 1 || true)"
    fi

    if [[ -z "$found" ]]; then
        found="$("$ENGINE" ps --all \
            --filter "label=com.docker.compose.service=${service}" \
            --format '{{.Names}}' 2>/dev/null | head -n 1 || true)"
    fi

    printf '%s\n' "$found"
}

container_state() {
    "$ENGINE" inspect --format '{{.State.Status}}' "$1" 2>/dev/null || true
}

# The health status of a container, or empty when it has no healthcheck. REPORTED, never waited on,
# and that distinction is the whole of F-53: a health status of "starting" that never advances is
# exactly what hung the documented command, so this prints it and moves on. It is worth printing
# because it is also the fastest way to tell an operator that their database is genuinely unwell.
container_health() {
    "$ENGINE" inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{end}}' "$1" 2>/dev/null || true
}

# One line describing a service, on stdout. Callers redirect: `status` writes to stdout because that
# is its output, and `up` writes to stderr because `up` puts nothing on stdout at all.
describe_service() {
    local service="$1" name state health
    name="$(compose_container "$service")"
    if [[ -z "$name" ]]; then
        printf '  %-10s not created\n' "${service}:"
        return 0
    fi

    state="$(container_state "$name")"
    health="$(container_health "$name")"
    if [[ -n "$health" ]]; then
        printf '  %-10s %s (%s, health: %s)\n' "${service}:" "$name" "${state:-unknown}" "$health"
    else
        printf '  %-10s %s (%s)\n' "${service}:" "$name" "${state:-unknown}"
    fi
}

# Start a container the engine already knows about but which is not running. Engine-level and
# deliberately not a compose call: the repair needed after a compose command was cut short is
# "start what was created", and asking compose again invites the same wait that was just abandoned.
start_if_stopped() {
    local service="$1" name state
    name="$(compose_container "$service")"
    [[ -n "$name" ]] || return 1

    state="$(container_state "$name")"
    if [[ "$state" == "running" ]]; then
        return 0
    fi

    log "starting ${name} (it is ${state:-absent})…"
    "$ENGINE" start "$name" >/dev/null 2>&1 || return 1
    [[ "$(container_state "$name")" == "running" ]]
}

tunnel_is_running() { [[ "$(container_state "$TUNNEL_CONTAINER")" == "running" ]]; }
tunnel_exists()     { [[ -n "$(container_state "$TUNNEL_CONTAINER")" ]]; }

# The assigned hostname, read out of the tunnel's own log. cloudflared prints it once, inside a box,
# and it is the only *.trycloudflare.com string it ever emits.
tunnel_url() {
    "$ENGINE" logs "$TUNNEL_CONTAINER" 2>&1 \
        | grep --only-matching --extended-regexp 'https://[A-Za-z0-9][A-Za-z0-9.-]*\.trycloudflare\.com' \
        | head -n 1 || true
}

# ---------------------------------------------------------------------------------------------------
# 5. HTTP probing without assuming the host has an HTTP client
#
# A machine chosen for having Podman on it is not necessarily a machine with curl on it. Three ways
# are tried in order of directness, and the last one is the reason this works on a bare host: the
# runtime image installs curl for its own compose healthcheck, so `exec`ing it is a client that is
# guaranteed to exist whenever there is anything to probe. It reaches both the app (it *is* the app)
# and the public URL (it has egress, the same egress the tunnel uses).
# ---------------------------------------------------------------------------------------------------
PROBE=""
if command -v curl >/dev/null 2>&1; then
    PROBE="curl"
elif command -v wget >/dev/null 2>&1; then
    PROBE="wget"
fi

http_ok() {
    local url="$1" web
    case "$PROBE" in
        curl) curl --fail --silent --show-error --output /dev/null --max-time 10 "$url" >/dev/null 2>&1 && return 0 ;;
        wget) wget --quiet --timeout=10 --tries=1 --output-document=/dev/null "$url" >/dev/null 2>&1 && return 0 ;;
    esac

    [[ -n "$PROBE" ]] && return 1

    web="$(compose_container web)"
    [[ -n "$web" ]] || return 2
    "$ENGINE" exec "$web" curl --fail --silent --output /dev/null --max-time 10 "$url" >/dev/null 2>&1
}

# Poll $1 until it answers, $2 seconds pass, or there is no way to ask.
#   0 answered | 1 timed out | 2 no way to probe
wait_for_http() {
    local url="$1" seconds="$2" deadline outcome
    deadline=$(( $(date +%s) + seconds ))
    while (( $(date +%s) < deadline )); do
        outcome=0
        http_ok "$url" || outcome=$?
        case "$outcome" in
            0) return 0 ;;
            2) return 2 ;;
        esac
        sleep 3
    done
    return 1
}

# ---------------------------------------------------------------------------------------------------
# 6. Commands other than `up`
# ---------------------------------------------------------------------------------------------------
read_recorded_url() {
    [[ -f "$STATE_FILE" ]] || return 1
    local line
    while IFS= read -r line; do
        if [[ "$line" == "PUBLIC_URL="* ]]; then
            printf '%s\n' "${line#PUBLIC_URL=}"
            return 0
        fi
    done < "$STATE_FILE"
    return 1
}

case "$COMMAND" in
    url)
        # The running tunnel is the truth; the state file is a cache for when it is gone.
        if tunnel_is_running; then
            PUBLIC_URL="$(tunnel_url)"
        fi
        [[ -n "$PUBLIC_URL" ]] || PUBLIC_URL="$(read_recorded_url || true)"
        [[ -n "$PUBLIC_URL" ]] || die "no quick tunnel is open (nothing recorded in ${STATE_FILE})."
        printf '%s\n' "$PUBLIC_URL"
        exit 0
        ;;

    status)
        echo "engine:   ${ENGINE}"
        echo "compose:  ${COMPOSE[*]}"
        echo "project:  ${PROJECT_NAME:-<unknown>}"
        echo "state:    ${STATE_FILE}"
        if tunnel_exists; then
            echo "tunnel:   ${TUNNEL_CONTAINER} ($(container_state "$TUNNEL_CONTAINER"))"
        else
            echo "tunnel:   not created"
        fi
        if tunnel_is_running; then
            echo "url:      $(tunnel_url)"
        else
            recorded="$(read_recorded_url || true)"
            if [[ -n "$recorded" ]]; then
                echo "url:      ${recorded} (last recorded; the tunnel is not running, so that URL is dead)"
            fi
        fi

        # Read straight from the engine, and before compose is asked anything: these two lines are
        # the facts an operator needs, and they arrive even on a host where compose itself is stuck.
        echo
        echo "containers (engine):"
        describe_service postgres
        describe_service web

        echo
        status_result=0
        compose_guarded "$COMPOSE_WAIT" ps || status_result=$?
        if (( status_result == 124 )); then
            report_deadline "$COMPOSE_WAIT" ps
        fi
        exit 0
        ;;

    logs)
        tunnel_exists || die "no tunnel container named '${TUNNEL_CONTAINER}'."
        if (( FOLLOW )); then
            exec "$ENGINE" logs --follow "$TUNNEL_CONTAINER"
        fi
        exec "$ENGINE" logs "$TUNNEL_CONTAINER"
        ;;

    down)
        if tunnel_exists; then
            log "closing the quick tunnel (${TUNNEL_CONTAINER}) — its URL dies here."
            "$ENGINE" rm --force "$TUNNEL_CONTAINER" >/dev/null 2>&1 || true
        fi

        log "stopping the stack…"
        down_result=0
        compose_guarded "$COMPOSE_WAIT" down || down_result=$?
        if (( down_result == 124 )); then
            report_deadline "$COMPOSE_WAIT" down
            warn "  removing the containers directly instead; the named volumes are untouched."
            for service in web postgres; do
                container="$(compose_container "$service")"
                [[ -n "$container" ]] || continue
                "$ENGINE" rm --force "$container" >/dev/null 2>&1 || true
            done
        fi

        rm -f "$STATE_FILE" 2>/dev/null || true
        log "down. Named volumes are untouched: the database and the Data Protection key ring survive."
        exit 0
        ;;
esac

# ---------------------------------------------------------------------------------------------------
# 7. `up` — preflight
# ---------------------------------------------------------------------------------------------------
[[ -f "compose.yaml" ]] || die "compose.yaml is not here; run this from a checkout of the repository."

if [[ ! -f ".env" ]]; then
    warn "no .env in this checkout, so compose is using the development defaults from compose.yaml —"
    warn "  including POSTGRES_PASSWORD=myrestaurant. Fine for a tester instance on a private network."
    warn "  'cp .env.example .env' first if this box is reachable by anything you do not control."
    warn "  Nothing here writes that file for you (F-54): copying it is a decision, not a formality."
fi

# The origin has to come from this script, and the environment is how it gets there. Compose gives
# the process environment precedence over .env, so a pinned value in .env does not silently win —
# but it does mean .env and the running instance will disagree, which is worth saying out loud.
if [[ -f ".env" ]] && grep --quiet --extended-regexp '^[[:space:]]*RESTAURANT_PUBLIC_ORIGIN=' .env; then
    warn ".env pins RESTAURANT_PUBLIC_ORIGIN. The tunnel URL takes precedence for this instance"
    warn "  (the process environment beats .env), so that line will not be what is served."
fi

# The compose file in this checkout is the one about to be handed to the engine, and a health-gated
# dependency in it is what hung the first run of this script (F-53). One grep buys an operator who
# pulled a branch, or resolved a merge the wrong way, a sentence naming the cause instead of a
# terminal that stops.
if grep --quiet --extended-regexp '^[[:space:]]*condition:[[:space:]]*service_healthy' compose.yaml; then
    warn "compose.yaml declares a 'condition: service_healthy' dependency (F-53)."
    warn "  podman-compose 1.3.0 waits on that condition forever, printing nothing, AFTER it has"
    warn "  already started every container — so 'up -d' can simply never return on this host."
    warn "  The deadline below cuts that short, but the file should say 'service_started'."
fi

if [[ -z "$PROBE" ]]; then
    log "no curl or wget on this host; readiness will be probed with the curl inside the web container."
fi

if (( ! HAVE_TIMEOUT )); then
    warn "no 'timeout' on this host, so compose commands run with no deadline (F-53)."
    warn "  If one stops printing and never returns, Ctrl+C is safe: the containers it already"
    warn "  started belong to the engine and keep running. '$0 status' will show them."
fi

LINGER="unknown"
if command -v loginctl >/dev/null 2>&1; then
    LINGER="$(loginctl show-user "$(id -un)" --property=Linger --value 2>/dev/null || echo "unknown")"
fi

SOURCE_REVISION="${SOURCE_REVISION:-}"
if [[ -z "$SOURCE_REVISION" ]] && command -v git >/dev/null 2>&1; then
    SOURCE_REVISION="$(git rev-parse HEAD 2>/dev/null || true)"
fi
export SOURCE_REVISION

# ---------------------------------------------------------------------------------------------------
# 8. `up` — build the image before anything announces a URL
#
# The build gets its own, far longer deadline: a cold `dotnet publish` inside the SDK image was
# measured at nineteen minutes on this hardware, and a watchdog that cuts off a legitimate build
# would be a worse defect than the one it guards against.
# ---------------------------------------------------------------------------------------------------
if (( NO_BUILD )); then
    log "skipping the build (--no-build); the existing image will be used."
else
    log "building the web image — the .NET SDK runs INSIDE the builder, so this host needs no dotnet."
    log "  on a cold host with nothing cached this is tens of minutes. Nothing is public yet."

    build_arguments=(build web)
    if (( NO_CACHE )); then
        build_arguments=(build --no-cache web)
    fi

    build_result=0
    compose_guarded "$BUILD_WAIT" "${build_arguments[@]}" || build_result=$?
    if (( build_result == 124 )); then
        report_deadline "$BUILD_WAIT" "${build_arguments[@]}"
        die "the image build did not finish within ${BUILD_WAIT}s. Nothing has been published."
    fi
    (( build_result == 0 )) || die "the image build failed (exit ${build_result}). Nothing has been published."
    log "image built."
fi

# ---------------------------------------------------------------------------------------------------
# 9. `up` — the tunnel, as a detached container this script does not own
# ---------------------------------------------------------------------------------------------------
if (( NEW_URL )) && tunnel_exists; then
    log "--new-url: discarding the existing tunnel. Passkeys registered against its URL stop working."
    "$ENGINE" rm --force "$TUNNEL_CONTAINER" >/dev/null 2>&1 || true
fi

if tunnel_is_running; then
    PUBLIC_URL="$(tunnel_url)"
    [[ -n "$PUBLIC_URL" ]] \
        || die "'${TUNNEL_CONTAINER}' is running but never announced a URL. Run '$0 down' and try again."
    log "reusing the tunnel that is already open: ${PUBLIC_URL}"
else
    if tunnel_exists; then
        log "removing a stopped tunnel container from a previous run."
        "$ENGINE" rm --force "$TUNNEL_CONTAINER" >/dev/null 2>&1 || true
    fi

    log "opening a quick tunnel to ${TUNNEL_TARGET} …"
    "$ENGINE" run --detach \
        --name "$TUNNEL_CONTAINER" \
        --network host \
        --label "myrestaurant.role=quick-tunnel" \
        "$CLOUDFLARED_IMAGE" \
        tunnel --no-autoupdate --url "$TUNNEL_TARGET" >/dev/null

    log "waiting for the tunnel URL (up to ${URL_WAIT}s)…"
    deadline=$(( $(date +%s) + URL_WAIT ))
    while (( $(date +%s) < deadline )); do
        if ! tunnel_is_running; then
            "$ENGINE" logs "$TUNNEL_CONTAINER" >&2 2>&1 || true
            die "cloudflared exited before announcing a URL (its log is above)."
        fi
        PUBLIC_URL="$(tunnel_url)"
        [[ -n "$PUBLIC_URL" ]] && break
        sleep 2
    done
    [[ -n "$PUBLIC_URL" ]] \
        || die "timed out waiting for the tunnel URL. See '$0 logs'."
fi

log "public URL: ${PUBLIC_URL}"

# ---------------------------------------------------------------------------------------------------
# 10. `up` — the stack, created with the real origin already in hand
#
# One invocation, under a deadline, with the build already done. No extra flags: podman-compose and
# docker compose both build only what is missing on a plain `up`, so the image built in section 8 is
# reused, and a flag this script does not need is one more thing that can be unsupported on some
# host nobody tested.
# ---------------------------------------------------------------------------------------------------
export RESTAURANT_PUBLIC_ORIGIN="$PUBLIC_URL"

log "starting postgres and web against that origin (deadline ${COMPOSE_WAIT}s)…"
up_result=0
compose_guarded "$COMPOSE_WAIT" up -d || up_result=$?

if (( up_result == 124 )); then
    report_deadline "$COMPOSE_WAIT" up -d
elif (( up_result != 0 )); then
    warn "'${COMPOSE[*]} up -d' exited ${up_result}. What the containers are doing is below."
fi

log "containers (engine):"
describe_service postgres >&2
describe_service web >&2

# Readiness means nothing until both containers are running, and "created but never started" is a
# state a cut-short compose command can leave behind — so it is repaired here rather than reported.
for service in postgres web; do
    service_container="$(compose_container "$service")"
    if [[ -z "$service_container" ]]; then
        die "no '${service}' container exists — '${COMPOSE[*]} up -d' never created it (output above)."
    fi
    if [[ "$(container_state "$service_container")" != "running" ]]; then
        start_if_stopped "$service" \
            || die "'${service_container}' is not running and would not start. Read: ${ENGINE} logs ${service_container}"
    fi
done

log "waiting for ${TUNNEL_TARGET}/healthz/ready (up to ${READY_WAIT}s; first boot runs the migrations)…"
ready=0
wait_for_http "${TUNNEL_TARGET%/}/healthz/ready" "$READY_WAIT" || ready=$?
case "$ready" in
    0) log "the app is ready on this host." ;;
    2) warn "no HTTP client and no web container to borrow one from; readiness was not verified." ;;
    *)
        warn "the app did not answer /healthz/ready within ${READY_WAIT}s."
        warn "  the stack is left running so you can read it: ${COMPOSE[*]} logs web"
        warn "  a database that never came up shows as such in: $0 status"
        ;;
esac

# ---------------------------------------------------------------------------------------------------
# 11. `up` — record where this instance lives
# ---------------------------------------------------------------------------------------------------
mkdir -p "$STATE_DIRECTORY"
{
    echo "# Written by scripts/dev_instance.sh. Read by 'dev_instance.sh url' when the tunnel is gone."
    echo "PUBLIC_URL=${PUBLIC_URL}"
    echo "TUNNEL_CONTAINER=${TUNNEL_CONTAINER}"
    echo "ENGINE=${ENGINE}"
    echo "PROJECT_NAME=${PROJECT_NAME}"
    echo "SOURCE_REVISION=${SOURCE_REVISION:-not recorded}"
    echo "STARTED_AT=$(date --iso-8601=seconds 2>/dev/null || date)"
} > "$STATE_FILE"

# ---------------------------------------------------------------------------------------------------
# 12. `up` — say it, then prove it, then let go of the terminal
# ---------------------------------------------------------------------------------------------------
cat >&2 <<BANNER

────────────────────────────────────────────────────────────────────────────
  DEV INSTANCE — DETACHED

  PUBLIC URL:  ${PUBLIC_URL}

  • This keeps running after this command exits and after you log out.
    Stop it with:  scripts/dev_instance.sh down
  • Re-running 'up' REUSES this URL. Passkeys registered against it keep
    working. '--new-url' throws it away and mints a new one; every passkey
    registered on the old URL stops matching.
  • Passkeys work here (per-request RP ID, ADR-0005). Password + TOTP is the
    baseline that survives a URL change.
  • Do NOT bootstrap a real, long-lived instance through a quick tunnel.

  scripts/dev_instance.sh url      the URL again, on stdout, for scripts
  scripts/dev_instance.sh status   what is running
  scripts/dev_instance.sh logs -f  the tunnel's log
────────────────────────────────────────────────────────────────────────────

BANNER

if (( FOLLOW )); then
    log "--follow: streaming the tunnel log. Ctrl+C stops WATCHING, not the instance."
    exec "$ENGINE" logs --follow "$TUNNEL_CONTAINER"
fi

# The settle phase. The point is not the delay, it is that the terminal is released on evidence:
# the public URL is probed from outside this host's loopback for a few seconds, so a tunnel that
# came up and immediately fell over is reported here rather than discovered by a tester.
if (( SETTLE_SECONDS > 0 )); then
    log "holding ${SETTLE_SECONDS}s to confirm the public URL answers, then releasing this terminal…"
    settle_deadline=$(( $(date +%s) + SETTLE_SECONDS ))
    probes=0
    answered=0
    unprobeable=0
    while (( $(date +%s) < settle_deadline )); do
        if ! tunnel_is_running; then
            die "the tunnel container stopped during the settle window. See '$0 logs'."
        fi
        probes=$(( probes + 1 ))
        result=0
        http_ok "${PUBLIC_URL%/}/healthz/ready" || result=$?
        case "$result" in
            0) answered=$(( answered + 1 )) ;;
            2) unprobeable=1 ;;
        esac
        sleep 5
    done

    if (( unprobeable )); then
        warn "the public URL could not be probed from here; it is published, unverified."
    elif (( answered == 0 )); then
        warn "the public URL did not answer in ${probes} attempt(s) over ${SETTLE_SECONDS}s."
        warn "  the tunnel is open and the app may still be finishing its first boot."
        warn "  check it with: $0 status   and   ${COMPOSE[*]} logs web"
    else
        log "public URL answered ${answered} of ${probes} probe(s). It is live."
    fi
fi

case "$LINGER" in
    yes)
        log "lingering is enabled for $(id -un), so these containers survive logout."
        ;;
    no)
        log "lingering is OFF for $(id -un). Debian's logind default does not kill user processes on"
        log "  logout, so this will very probably keep running — but the guarantee is one command:"
        log "     loginctl enable-linger $(id -un)"
        ;;
    *)
        log "could not read the lingering state; if this instance must survive a logout, run:"
        log "     loginctl enable-linger $(id -un)"
        ;;
esac

log "releasing the terminal. The instance keeps running."
exit 0
