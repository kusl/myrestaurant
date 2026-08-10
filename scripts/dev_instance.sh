#!/usr/bin/env bash
#
# A long-lived demo instance behind a Cloudflare quick tunnel, on a host with no .NET SDK
# (TECHNICAL_SPECIFICATION §14.3a, ADR-0004, ADR-0005). Everything runs in containers; this
# script exits and leaves them running.
#
#   scripts/dev_instance.sh              bring it up, prove it answers, print the URL, EXIT
#   scripts/dev_instance.sh up           the same thing, said out loud
#   scripts/dev_instance.sh url          print the current public URL and nothing else
#   scripts/dev_instance.sh status       what is running, and where
#   scripts/dev_instance.sh logs [what]  one container's log: web (default), postgres, or tunnel
#   scripts/dev_instance.sh diagnose     why it is not serving: both logs, and how to read them
#   scripts/dev_instance.sh down         stop the tunnel and the stack, KEEPING the named volumes
#   scripts/dev_instance.sh reset        down, and then DESTROY the named volumes — see below
#   scripts/dev_instance.sh --help       this text
#
# Flags:  --no-build  --no-cache  --new-url  --follow/-f  --tail N  --yes
#
# EXIT STATUS IS A CLAIM ABOUT THE INSTANCE, NOT ABOUT THIS SCRIPT (F-55)
#
# `up` exits 0 only when the application answered /healthz/ready on this host. It exits 1 when the
# stack was started and the application never answered — and it leaves everything running when it
# does, because the containers and their logs are the evidence. So `time bash scripts/dev_instance.sh`
# now fails loudly on a broken instance instead of printing a URL banner over a dead application.
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
# NO WAIT MAY OUTLIVE ITS OWN EVIDENCE, AND A FAILURE MUST PRINT THE LOG (F-55)
#
# The second run did not hang. It did something worse, and it took six minutes and fifty-five
# seconds to do it: `postgres` restarted in a loop, `web` exited 1, and this script sat out the
# full 300-second readiness deadline against a container that was already dead — then printed the
# DEV INSTANCE banner with a public URL in it, held twenty more seconds probing that URL, warned
# that it had not answered, and exited 0. The one thing it never did was print either container's
# log, which is where the reason was the entire time. `logs` would not have helped: it showed the
# tunnel's log and had no way to ask for the application's.
#
# F-53 was a wait with no deadline. This was a wait with a deadline and no evidence — and the two
# call for opposite fixes, which is why the rule is stated separately: a deadline stops a wait that
# cannot end, and only a liveness check stops a wait that cannot succeed. Every wait below is
# bounded AND watched:
#
#   • the database wait ends early when postgres has restarted CRASH_LOOP_RESTARTS times, because
#     a container that keeps exiting is not going to start accepting connections at second 179;
#   • both waits start a stopped container again first, up to START_ATTEMPTS times, and then give
#     up on it — because the application's own database retry is bounded at sixty seconds (ADR-0012),
#     so a slow first postgres boot outlives it and leaves a correctly built image stopped with
#     nothing wrong, and because a container that will not stay started will not start later either;
#   • the settle phase is SKIPPED when readiness already failed, because probing a public URL for
#     an application that is not answering on loopback cannot produce information;
#   • and when anything fails, `up` prints both containers' log tails and a key for reading them,
#     which is what turns "it did not come up" into a cause.
#
# THE ONE FAILURE THIS SCRIPT CANNOT REPAIR, AND THE COMMAND THAT CAN
#
# `down` deliberately keeps the named volumes: the database and the Data Protection key ring are
# what make a test instance worth returning to. But a PostgreSQL data directory that cannot start —
# an interrupted first initdb, a half-written checkpoint after a hard reboot, a directory written by
# a different major version — survives `down` and `up` for exactly the same reason, and no amount of
# restarting fixes it. `podman system prune -a` does not touch volumes either, so an operator can
# reasonably believe they have cleared everything and still be starting the same poisoned directory.
#
# `reset` is the escape hatch, and it is destructive on purpose: it removes this project's named
# volumes, which is the database AND the key ring — every account, every passkey, every enrolled
# TOTP secret on this instance. It asks before doing it, and needs --yes when stdin is not a
# terminal. Take a backup first (OPERATIONS §6) if the data mattered.
#
# ADDRESSES ARE LITERALS HERE, NOT NAMES (F-56)
#
# `compose.yaml` publishes the web port as `127.0.0.1:8080:8080` — an IPv4 loopback address and
# nothing else. So everything that dials it names `127.0.0.1`, never `localhost`: a name that
# resolves to `::1` first depends on every client falling back to the second address, and BusyBox
# `wget` — a real possibility on a minimal host, and the second entry in this script's own probe
# chain — does not. `run.sh` has probed the literal since M1 and the two tunnel helpers named the
# host; that is a rule applied to one example and never generalised.
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
#   TUNNEL_TARGET                  what cloudflared points at (default http://127.0.0.1:8080)
#   TUNNEL_URL_WAIT                seconds to wait for the tunnel URL          (default 120)
#   DEV_INSTANCE_COMPOSE_WAIT      seconds any one compose command may take    (default 240)
#   DEV_INSTANCE_BUILD_WAIT        seconds the image build may take            (default 5400)
#   DEV_INSTANCE_DATABASE_WAIT     seconds to wait for postgres to accept connections (default 180)
#   DEV_INSTANCE_READY_WAIT        seconds to wait for /healthz/ready          (default 300)
#   DEV_INSTANCE_SETTLE_SECONDS    seconds to re-probe the public URL before exiting (default 20)
#   DEV_INSTANCE_LOG_TAIL          log lines printed per container on a failure (default 40)
#   DEV_INSTANCE_START_ATTEMPTS    times a stopped container is started again before giving up
#                                  (default 3)
#   DEV_INSTANCE_CRASH_LOOP_RESTARTS restarts that count as a crash loop       (default 3)
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

log()  { printf '[dev-instance] %s\n' "$*" >&2; }
warn() { printf '[dev-instance] warning: %s\n' "$*" >&2; }
die()  { printf '[dev-instance] error: %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------------------------------
# 0. Arguments
# ---------------------------------------------------------------------------------------------------
COMMAND="up"
COMMAND_GIVEN=0
NO_BUILD=0
NO_CACHE=0
NEW_URL=0
FOLLOW=0
ASSUME_YES=0
LOG_TARGET=""
TAIL_LINES=""

while (( $# > 0 )); do
    case "$1" in
        up | url | status | logs | diagnose | down | reset)
            if (( COMMAND_GIVEN )); then
                die "'$COMMAND' and '$1' are both commands; give one (try --help)."
            fi
            COMMAND="$1"
            COMMAND_GIVEN=1
            ;;
        web | postgres | database | tunnel)
            if [[ -n "$LOG_TARGET" ]]; then
                die "'$LOG_TARGET' and '$1' are both log targets; give one (try --help)."
            fi
            if [[ "$1" == "database" ]]; then
                LOG_TARGET="postgres"
            else
                LOG_TARGET="$1"
            fi
            ;;
        --no-build)      NO_BUILD=1 ;;
        --no-cache)      NO_CACHE=1 ;;
        --new-url)       NEW_URL=1 ;;
        --follow | -f)   FOLLOW=1 ;;
        --yes | -y)      ASSUME_YES=1 ;;
        --tail)
            if (( $# < 2 )); then
                die "--tail needs a number of lines."
            fi
            TAIL_LINES="$2"
            shift
            ;;
        --tail=*)
            TAIL_LINES="${1#--tail=}"
            ;;
        --help | -h)
            awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$0"
            exit 0
            ;;
        *)
            die "unknown argument '$1' (try --help)."
            ;;
    esac
    shift
done

if [[ -n "$TAIL_LINES" && ! "$TAIL_LINES" =~ ^[0-9]+$ ]]; then
    die "--tail takes a number of lines (got '${TAIL_LINES}')."
fi

if [[ -n "$LOG_TARGET" && "$COMMAND" != "logs" ]]; then
    die "'${LOG_TARGET}' only means something to 'logs' (try: $0 logs ${LOG_TARGET})."
fi

# ---------------------------------------------------------------------------------------------------
# 1. Settings
#
# TUNNEL_TARGET names 127.0.0.1 rather than localhost, deliberately (F-56): compose publishes the web
# port on that address and no other, and this value is dialled by cloudflared, by curl, and by wget —
# the last of which, in its BusyBox form, does not try a second address when the first refuses.
# ---------------------------------------------------------------------------------------------------
TUNNEL_TARGET="${TUNNEL_TARGET:-http://127.0.0.1:8080}"
URL_WAIT="${TUNNEL_URL_WAIT:-120}"
COMPOSE_WAIT="${DEV_INSTANCE_COMPOSE_WAIT:-240}"
BUILD_WAIT="${DEV_INSTANCE_BUILD_WAIT:-5400}"
DATABASE_WAIT="${DEV_INSTANCE_DATABASE_WAIT:-180}"
READY_WAIT="${DEV_INSTANCE_READY_WAIT:-300}"
SETTLE_SECONDS="${DEV_INSTANCE_SETTLE_SECONDS:-20}"
LOG_TAIL="${DEV_INSTANCE_LOG_TAIL:-40}"
START_ATTEMPTS="${DEV_INSTANCE_START_ATTEMPTS:-3}"
CRASH_LOOP_RESTARTS="${DEV_INSTANCE_CRASH_LOOP_RESTARTS:-3}"
TUNNEL_CONTAINER="${DEV_INSTANCE_TUNNEL_CONTAINER:-myrestaurant_quicktunnel}"
CLOUDFLARED_IMAGE="${CLOUDFLARED_IMAGE:-docker.io/cloudflare/cloudflared:latest}"
STATE_DIRECTORY="${DEV_INSTANCE_STATE_DIRECTORY:-${XDG_STATE_HOME:-${HOME}/.local/state}/myrestaurant}"
STATE_FILE="${STATE_DIRECTORY}/dev-instance.env"

PUBLIC_URL=""

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
# overrides it. It is used to NARROW container discovery below and to find this project's volumes;
# discovery falls back to the service label on its own if the derivation is wrong.
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
# 4. Container facts
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

# The exit code of the last run, or empty when the engine will not say. Printed rather than
# interpreted: 1 from this application means it refused its own configuration and said so on stderr
# (Program.cs returns 1 for that and nothing else), which is exactly the kind of thing the log tail
# below answers and a status word does not.
container_exit_code() {
    "$ENGINE" inspect --format '{{.State.ExitCode}}' "$1" 2>/dev/null || true
}

# How many times the engine has restarted this container under its restart policy. The crash-loop
# signal: a number that keeps climbing while nothing else changes is a container that cannot start,
# and it is the reason the database wait can end at second thirty instead of second one hundred and
# eighty. Absent or unparseable reads as 0, so a host whose engine omits the field simply loses the
# early exit rather than misreporting one.
container_restarts() {
    local value
    value="$("$ENGINE" inspect --format '{{.RestartCount}}' "$1" 2>/dev/null || true)"
    if [[ ! "$value" =~ ^[0-9]+$ ]]; then
        value=0
    fi
    printf '%s\n' "$value"
}

# The health status of a container, or empty when it has no healthcheck. REPORTED, never waited on,
# and that distinction is the whole of F-53: a health status of "starting" that never advances is
# exactly what hung the documented command, so this prints it and moves on.
container_health() {
    "$ENGINE" inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{end}}' "$1" 2>/dev/null || true
}

# One line describing a service, on stdout. Callers redirect: `status` writes to stdout because that
# is its output, and `up` writes to stderr because `up` puts nothing on stdout at all.
#
# A stopped container reports its EXIT CODE and its restart count, and does not report health — the
# previous version printed "(stopped, health: starting)" for a container that had exited 1 six
# minutes earlier, which reads as a container still on its way up (F-55).
describe_service() {
    local service="$1" name state health code restarts detail

    name="$(compose_container "$service")"
    if [[ -z "$name" ]]; then
        printf '  %-10s not created\n' "${service}:"
        return 0
    fi

    state="$(container_state "$name")"
    restarts="$(container_restarts "$name")"

    if [[ "$state" == "running" ]]; then
        health="$(container_health "$name")"
        detail="running"
        if [[ -n "$health" ]]; then
            detail="running, health: ${health}"
        fi
    else
        code="$(container_exit_code "$name")"
        detail="${state:-unknown}"
        if [[ -n "$code" ]]; then
            detail="${detail}, exit code ${code}"
        fi
    fi

    if (( restarts > 0 )); then
        detail="${detail}, restarted ${restarts}x"
    fi

    printf '  %-10s %s (%s)\n' "${service}:" "$name" "$detail"
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
# 5. Reading the logs, which is the part that was missing (F-55)
#
# Captured and reprinted rather than streamed, so that both containers' output can be framed, and so
# that an empty log is reported as an empty log instead of as a blank gap. Bounded by --tail, because
# the point is the last thing that happened.
# ---------------------------------------------------------------------------------------------------
print_container_log() {
    local service="$1" lines="$2" name output

    if [[ "$service" == "tunnel" ]]; then
        name="$TUNNEL_CONTAINER"
        if ! tunnel_exists; then
            name=""
        fi
    else
        name="$(compose_container "$service")"
    fi

    printf '\n' >&2
    if [[ -z "$name" ]]; then
        printf '  ── %s: no container exists, so there is no log ──\n' "$service" >&2
        return 0
    fi

    printf '  ── %s (%s), last %s line(s) ──\n' "$service" "$name" "$lines" >&2
    output="$("$ENGINE" logs --tail "$lines" "$name" 2>&1 || true)"
    if [[ -z "$output" ]]; then
        printf '  (empty — the container was created and never wrote anything, which usually means\n' >&2
        printf '   it never ran. Its exit code above is the thing to read.)\n' >&2
        return 0
    fi

    printf '%s\n' "$output" | sed 's/^/  | /' >&2
}

# The reading key. Every line of it is a symptom this project can actually produce, paired with what
# it means and what to do — because "the app did not answer" is not a diagnosis, and an operator who
# has just watched a bring-up fail should not have to guess which of two logs matters.
print_reading_key() {
    cat >&2 <<'KEY'

  HOW TO READ THOSE TWO LOGS

  web says 'Configuration error: <VARIABLE> …'
      The application refused its own environment and exited 1 before opening a socket. That line
      names the variable and the value it got. Nothing retries this — fix the value (in .env, or in
      the environment you ran this from) and run 'up' again.

  web says 'Database not ready (attempt n/30)' and then stops
      postgres never accepted a connection within sixty seconds, which is where ADR-0012's bounded
      boot retry gives up. The cause is in the POSTGRES log above, not in this one.

  postgres says 'database files are incompatible', 'directory … exists but is not empty',
  'could not locate a valid checkpoint record', or PANIC
      The named volume holds a data directory this image cannot start — an interrupted first initdb,
      a hard reboot mid-write, or a directory from another major version. Restarting cannot fix it,
      and neither 'down' nor 'podman system prune -a' removes it: both keep volumes on purpose.
      'scripts/dev_instance.sh reset' destroys them. That takes the database AND the Data Protection
      key ring with it — every account, passkey and enrolled TOTP secret on this instance. Back up
      first (OPERATIONS §6) if the data mattered.

  either log says 'address already in use' or 'rootlessport'
      Something else on this host already holds 127.0.0.1:8080 or 127.0.0.1:5432 — very often a
      container from an earlier run of this stack under a different project name, or a system
      PostgreSQL. 'podman ps --all' and 'ss -ltnp' name it.

  postgres is healthy, web is running, and /healthz/ready still does not answer
      Then the application is up and the probe is not reaching it. Check that the published port is
      the one being dialled: this script dials TUNNEL_TARGET, and compose publishes 127.0.0.1:8080.

KEY
}

# Everything an operator needs in one screen, and the body of `diagnose`. Written to stderr for the
# same reason the rest of `up` is: stdout belongs to `url`.
print_diagnosis() {
    local lines="${1:-$LOG_TAIL}"

    printf '\n' >&2
    printf '%s\n' '──────────────────────────── WHY IT IS NOT SERVING ────────────────────────────' >&2
    printf '\n' >&2
    printf '  engine:   %s\n' "$ENGINE" >&2
    printf '  compose:  %s\n' "${COMPOSE[*]}" >&2
    printf '  project:  %s\n' "${PROJECT_NAME:-<unknown>}" >&2
    printf '  probing:  %s\n' "${TUNNEL_TARGET%/}/healthz/ready" >&2
    printf '\n' >&2
    printf '  containers (read from the engine, not from compose):\n' >&2
    describe_service postgres >&2
    describe_service web >&2
    if tunnel_exists; then
        printf '  %-10s %s (%s)\n' "tunnel:" "$TUNNEL_CONTAINER" "$(container_state "$TUNNEL_CONTAINER")" >&2
    else
        printf '  %-10s not created\n' "tunnel:" >&2
    fi

    print_container_log postgres "$lines"
    print_container_log web "$lines"
    print_reading_key
    printf '%s\n' '──────────────────────────────────────────────────────────────────────────────' >&2
}

# ---------------------------------------------------------------------------------------------------
# 6. HTTP probing without assuming the host has an HTTP client
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
    [[ "$(container_state "$web")" == "running" ]] || return 2
    "$ENGINE" exec "$web" curl --fail --silent --output /dev/null --max-time 10 "$url" >/dev/null 2>&1
}

# ---------------------------------------------------------------------------------------------------
# 7. The two waits that watch what they are waiting for (F-55)
# ---------------------------------------------------------------------------------------------------

# Does the database answer a connection request? pg_isready is in the postgres image and answers
# before authentication, so this needs no credentials and cannot be fooled by a password mismatch
# into reporting a server that is down.
database_accepts_connections() {
    "$ENGINE" exec "$1" pg_isready --quiet >/dev/null 2>&1
}

# Wait for postgres to accept connections, watching the container while it does.
#   0 accepting | 1 deadline passed | 2 no container | 3 it will not run (ended early)
#
# Two ways out early, and they are different failures: a container that keeps being restarted by the
# engine is crash-looping, and one that stays stopped after being started again is not coming up. Both
# used to be "the app did not answer /healthz/ready" five minutes later (F-55).
wait_for_database() {
    local seconds="$1" name deadline baseline churn state starts=0
    name="$(compose_container postgres)"
    [[ -n "$name" ]] || return 2

    baseline="$(container_restarts "$name")"
    deadline=$(( $(date +%s) + seconds ))

    while (( $(date +%s) < deadline )); do
        state="$(container_state "$name")"

        if [[ "$state" != "running" ]]; then
            if (( starts >= START_ATTEMPTS )); then
                warn "postgres has stopped ${starts} time(s) after being started again; it is not"
                warn "  going to run, so this is not waiting out the rest of the ${seconds}s deadline."
                return 3
            fi
            starts=$(( starts + 1 ))
            log "postgres is ${state:-absent} (exit code $(container_exit_code "$name")); starting it again (${starts}/${START_ATTEMPTS})."
            "$ENGINE" start "$name" >/dev/null 2>&1 || true
            sleep 5
            continue
        fi

        if database_accepts_connections "$name"; then
            return 0
        fi

        churn=$(( $(container_restarts "$name") - baseline ))
        if (( churn >= CRASH_LOOP_RESTARTS )); then
            warn "postgres has restarted ${churn} times since this bring-up began: it is crash-looping."
            warn "  Waiting out the remaining deadline cannot help — a container that keeps exiting is"
            warn "  not going to start accepting connections later. Its log is below."
            return 3
        fi

        sleep 3
    done

    return 1
}

# Wait for the application to answer /healthz/ready on loopback, watching the container while it does.
#   0 ready | 1 deadline passed | 2 no way to probe | 3 web will not stay running
#
# `web` is started again rather than merely reported, up to START_ATTEMPTS times, and that repair is
# specific: the application's database retry is bounded at thirty attempts two seconds apart
# (ADR-0012), so a first postgres boot slower than sixty seconds outlives it and leaves a correctly
# built image stopped with nothing wrong. The engine's own restart policy usually covers this; usually
# is not a thing to wait 300 seconds on.
wait_for_application() {
    local url="$1" seconds="$2" name deadline starts=0 state outcome
    name="$(compose_container web)"
    [[ -n "$name" ]] || return 2

    deadline=$(( $(date +%s) + seconds ))

    while (( $(date +%s) < deadline )); do
        state="$(container_state "$name")"
        if [[ "$state" != "running" ]]; then
            if (( starts >= START_ATTEMPTS )); then
                warn "web has exited ${starts} time(s) after being started again; it will not come up"
                warn "  on its own, so this is not waiting out the rest of the ${seconds}s deadline."
                return 3
            fi
            starts=$(( starts + 1 ))
            log "web is ${state:-absent} (exit code $(container_exit_code "$name")); starting it again (${starts}/${START_ATTEMPTS})."
            "$ENGINE" start "$name" >/dev/null 2>&1 || true
            sleep 5
            continue
        fi

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
# 8. Commands other than `up`
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

# The volumes this project owns, one per line, as the engine names them. Enumerated rather than
# assumed: the compose convention is "<project>_<volume>", and enumerating means `reset` reports
# exactly what it removed instead of guessing at two names and silently missing a third.
project_volumes() {
    [[ -n "$PROJECT_NAME" ]] || return 0
    "$ENGINE" volume ls --format '{{.Name}}' 2>/dev/null \
        | grep "^${PROJECT_NAME}_" || true
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

        # A container that is not running is the answer to "why is this not serving", so say where the
        # rest of the answer is rather than leaving an exit code sitting on the line above.
        for status_service in postgres web; do
            status_container="$(compose_container "$status_service")"
            [[ -n "$status_container" ]] || continue
            if [[ "$(container_state "$status_container")" != "running" ]]; then
                echo
                echo "'${status_service}' is not running. Read why:"
                echo "  $0 logs ${status_service}      its own log"
                echo "  $0 diagnose                    both logs, with a key for reading them"
                break
            fi
        done

        echo
        status_result=0
        compose_guarded "$COMPOSE_WAIT" ps || status_result=$?
        if (( status_result == 124 )); then
            report_deadline "$COMPOSE_WAIT" ps
        fi
        exit 0
        ;;

    logs)
        # Defaults to `web`, and that is the fix rather than a preference (F-55): when this instance
        # is not serving, the application's log is where the reason is, and the previous version could
        # only ever show the tunnel's. `logs tunnel` still says what cloudflared thinks.
        log_target="${LOG_TARGET:-web}"
        case "$log_target" in
            tunnel)
                tunnel_exists || die "no tunnel container named '${TUNNEL_CONTAINER}'."
                log_container="$TUNNEL_CONTAINER"
                ;;
            *)
                log_container="$(compose_container "$log_target")"
                [[ -n "$log_container" ]] \
                    || die "no '${log_target}' container exists (try: $0 status)."
                ;;
        esac

        log "showing the ${log_target} container's log (${log_container})."
        logs_command=("$ENGINE" logs)
        if (( FOLLOW )); then
            logs_command+=(--follow)
        fi
        if [[ -n "$TAIL_LINES" ]]; then
            logs_command+=(--tail "$TAIL_LINES")
        fi
        logs_command+=("$log_container")
        exec "${logs_command[@]}"
        ;;

    diagnose)
        print_diagnosis "${TAIL_LINES:-$LOG_TAIL}"
        exit 0
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
        log "  'reset' is what removes them, and it is the only thing that will."
        exit 0
        ;;

    reset)
        # The escape hatch for a data directory that cannot start, and destructive by definition.
        # Confirmed interactively, or with --yes; refused outright when neither, because a script
        # that silently destroys a key ring because stdin was a pipe would be worse than useless.
        volumes="$(project_volumes)"

        cat >&2 <<'RESETWARNING'

  RESET DESTROYS DATA. It removes this project's named volumes, which is:

    • the PostgreSQL data directory — every person, table, sitting, order and event;
    • the Data Protection key ring — so every enrolled TOTP secret becomes undecryptable and
      every passkey, session cookie and join grant issued by this instance stops verifying.

  This is the right answer to a postgres data directory that cannot start, and the wrong answer
  to almost anything else. 'down' keeps both. Take a backup first if the data mattered:
  OPERATIONS §6, scripts/backup.sh.

RESETWARNING

        if [[ -n "$volumes" ]]; then
            printf '  Volumes that will be removed:\n' >&2
            printf '%s\n' "$volumes" | sed 's/^/    /' >&2
        else
            printf '  No volumes with the prefix %s_ exist; there may be nothing to remove.\n' "$PROJECT_NAME" >&2
        fi
        printf '\n' >&2

        if (( ! ASSUME_YES )); then
            [[ -t 0 ]] || die "reset needs --yes when stdin is not a terminal. Nothing was removed."
            answer=""
            read -r -p "  Type 'destroy' to confirm: " answer || true
            [[ "$answer" == "destroy" ]] || die "not confirmed. Nothing was removed."
        fi

        if tunnel_exists; then
            log "closing the quick tunnel (${TUNNEL_CONTAINER})."
            "$ENGINE" rm --force "$TUNNEL_CONTAINER" >/dev/null 2>&1 || true
        fi

        log "stopping the stack and removing its volumes…"
        reset_result=0
        compose_guarded "$COMPOSE_WAIT" down --volumes || reset_result=$?
        if (( reset_result != 0 )); then
            if (( reset_result == 124 )); then
                report_deadline "$COMPOSE_WAIT" down --volumes
            else
                warn "'${COMPOSE[*]} down --volumes' exited ${reset_result}; finishing with the engine."
            fi
            for service in web postgres; do
                container="$(compose_container "$service")"
                [[ -n "$container" ]] || continue
                "$ENGINE" rm --force "$container" >/dev/null 2>&1 || true
            done
        fi

        # Asked of the engine either way, because `down --volumes` is the part most likely to have
        # been the thing that was cut short, and a volume that survived a reset is the whole defect
        # this command exists to fix.
        while IFS= read -r volume; do
            [[ -n "$volume" ]] || continue
            if "$ENGINE" volume rm "$volume" >/dev/null 2>&1; then
                log "removed volume ${volume}."
            else
                warn "could not remove volume ${volume} — something may still be using it."
                warn "  '${ENGINE} volume rm ${volume}' will say why."
            fi
        done <<< "$volumes"

        rm -f "$STATE_FILE" 2>/dev/null || true
        log "reset. The next 'up' initialises an empty database and mints a new key ring."
        log "  /setup will be reachable again, because there are no administrators (§3.6)."
        exit 0
        ;;
esac

# ---------------------------------------------------------------------------------------------------
# 9. `up` — preflight
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
# 10. `up` — build the image before anything announces a URL
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
# 11. `up` — the tunnel, as a detached container this script does not own
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
            print_container_log tunnel "$LOG_TAIL"
            die "cloudflared exited before announcing a URL (its log is above)."
        fi
        PUBLIC_URL="$(tunnel_url)"
        [[ -n "$PUBLIC_URL" ]] && break
        sleep 2
    done
    [[ -n "$PUBLIC_URL" ]] \
        || die "timed out waiting for the tunnel URL. See '$0 logs tunnel'."
fi

log "public URL: ${PUBLIC_URL}"

# ---------------------------------------------------------------------------------------------------
# 12. `up` — the stack, created with the real origin already in hand
#
# One invocation, under a deadline, with the build already done. No extra flags: podman-compose and
# docker compose both build only what is missing on a plain `up`, so the image built above is
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
        # Reported, started once, and NOT fatal here. The waits below start it again a bounded number
        # of times and then end early with the log — one failure path, one banner, one exit code —
        # rather than two places that each decide what a stopped container means (F-55).
        start_if_stopped "$service" \
            || warn "'${service_container}' would not start on the first try; the wait below will say why."
    fi
done

# ---------------------------------------------------------------------------------------------------
# 13. `up` — record where this instance lives
#
# Written BEFORE readiness is decided, and that ordering is deliberate: the tunnel is open and its
# hostname is real whatever the application is doing, so `url` and `down` must work on a bring-up
# that failed. A URL nobody recorded is a tunnel nobody can close.
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
# 14. `up` — the database first, then the application, each watched while waited on (F-55)
#
# The two waits are separate because their failures are different questions with different answers,
# and the previous single wait could not tell them apart: it timed out after five minutes and said
# "the app did not answer", which is true of a crash-looping database, a rejected configuration and a
# build that never started, and useful for none of them.
# ---------------------------------------------------------------------------------------------------
log "waiting for postgres to accept connections (up to ${DATABASE_WAIT}s)…"
database=0
wait_for_database "$DATABASE_WAIT" || database=$?
case "$database" in
    0) log "postgres is accepting connections." ;;
    2) warn "no postgres container to ask; readiness will be attempted anyway." ;;
    3) warn "postgres is not going to come up. Everything below is diagnosis." ;;
    *)
        warn "postgres did not accept a connection within ${DATABASE_WAIT}s."
        warn "  The application retries for sixty seconds and then exits (ADR-0012), so it has"
        warn "  very likely already given up. Its log will say so."
        ;;
esac

READY=0
if (( database == 0 || database == 2 )); then
    log "waiting for ${TUNNEL_TARGET%/}/healthz/ready (up to ${READY_WAIT}s; first boot runs the migrations)…"
    wait_for_application "${TUNNEL_TARGET%/}/healthz/ready" "$READY_WAIT" || READY=$?
else
    READY=4
fi

case "$READY" in
    0) log "the app is ready on this host." ;;
    2) warn "no HTTP client and no running web container to borrow one from; readiness was not verified." ;;
esac

# ---------------------------------------------------------------------------------------------------
# 15. `up` — the banner, which says what is true
#
# Two banners, not one with a warning bolted to it. The previous version printed the DEV INSTANCE
# banner unconditionally, so an operator whose application had exited 1 six minutes earlier was
# handed a public URL, a sentence about passkeys, and exit status 0 (F-55).
# ---------------------------------------------------------------------------------------------------
if (( READY != 0 && READY != 2 )); then
    cat >&2 <<BANNER

────────────────────────────────────────────────────────────────────────────
  DEV INSTANCE — NOT SERVING

  The tunnel is open at:  ${PUBLIC_URL}
  The application is not answering ${TUNNEL_TARGET%/}/healthz/ready on this host,
  so that URL will not serve anything either.

  Everything is left running, deliberately: the containers and their logs are
  the evidence. Nothing is destroyed by reading them.

  $0 diagnose        both logs, and how to read them
  $0 logs web        the application's own log, in full
  $0 logs postgres   the database's
  $0 down            stop it all (the named volumes survive)
  $0 reset           stop it all and DESTROY the volumes — read the warning
────────────────────────────────────────────────────────────────────────────

BANNER

    print_diagnosis "$LOG_TAIL"

    warn "exiting 1: the stack was started and the application never answered."
    exit 1
fi

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

  scripts/dev_instance.sh url        the URL again, on stdout, for scripts
  scripts/dev_instance.sh status     what is running
  scripts/dev_instance.sh logs -f    the application's log, streamed
  scripts/dev_instance.sh diagnose   if it stops serving later
────────────────────────────────────────────────────────────────────────────

BANNER

if (( FOLLOW )); then
    log "--follow: streaming the web log. Ctrl+C stops WATCHING, not the instance."
    web_container="$(compose_container web)"
    if [[ -n "$web_container" ]]; then
        exec "$ENGINE" logs --follow "$web_container"
    fi
    warn "the web container went away; nothing to follow."
fi

# The settle phase. The point is not the delay, it is that the terminal is released on evidence:
# the public URL is probed from outside this host's loopback for a few seconds, so a tunnel that
# came up and immediately fell over is reported here rather than discovered by a tester.
#
# Reached only when the application already answers on loopback. Probing the public URL for an
# application that is not answering locally cannot produce information — the previous version spent
# twenty seconds doing exactly that and then warned about the result (F-55).
if (( SETTLE_SECONDS > 0 )); then
    log "holding ${SETTLE_SECONDS}s to confirm the public URL answers, then releasing this terminal…"
    settle_deadline=$(( $(date +%s) + SETTLE_SECONDS ))
    probes=0
    answered=0
    unprobeable=0
    while (( $(date +%s) < settle_deadline )); do
        if ! tunnel_is_running; then
            die "the tunnel container stopped during the settle window. See '$0 logs tunnel'."
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
        warn "the public URL did not answer in ${probes} attempt(s) over ${SETTLE_SECONDS}s, although"
        warn "  the application does answer on this host — so this is the tunnel or the edge, not the"
        warn "  app. Cloudflare sometimes needs a moment on a brand-new hostname. Check it with:"
        warn "     $0 status   and   $0 logs tunnel"
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
