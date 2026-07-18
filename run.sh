#!/usr/bin/env bash
#
# Dev entry point (TECHNICAL_SPECIFICATION §14.4). Idempotent.
#
#   ./run.sh                     Start postgres in a container, then `dotnet watch` the web app on the
#                                host (Kestrel serves https://localhost:8443 via the dev cert). This is a
#                                FOREGROUND hot-reload server: it keeps running until you press Ctrl+C.
#                                It does not exit on its own — that is the point of a watch loop.
#
#   ./run.sh --smoke             Start postgres in a container, boot the web app once on the host, wait
#                                for /healthz/ready to return 200, print the URLs, shut the app back
#                                down, and EXIT. No hot reload, never holds the terminal — this is the
#                                mode to use at the end of a build/test sweep or in CI to confirm the app
#                                actually boots (config binds, migrations apply, database is reachable).
#
#   ./run.sh --containers-only   Bring the full dev stack up in containers (postgres + caddy + web),
#                                wait for /healthz/ready, print the URLs, and EXIT (no host watch loop).
#
# The database always runs in a container; only what happens to the web app differs. Running the web app
# on the host (default and --smoke) gives fast iteration; --containers-only mirrors the container
# topology end to end.
#
# This script never opens a Cloudflare quick tunnel and never prints a *.trycloudflare.com URL. That is a
# separate, demo-only concern (TECHNICAL_SPECIFICATION §14.3): run `scripts/quick_tunnel.sh` AFTER the
# stack is up (e.g. `./run.sh --containers-only`). Quick-tunnel hostnames are random per run and on the
# Public Suffix List, so passkeys registered through them die with the tunnel — demos use password+TOTP.

set -euo pipefail
cd "$(dirname "$0")"

# ---------------------------------------------------------------------------------------------------
# 0. Parse arguments
# ---------------------------------------------------------------------------------------------------
MODE="watch"
case "${1:-}" in
    "")                MODE="watch" ;;
    --smoke)           MODE="smoke" ;;
    --containers-only) MODE="containers" ;;
    *)
        echo "error: unknown argument '$1' (expected --smoke, --containers-only, or nothing)." >&2
        exit 1
        ;;
esac

# ---------------------------------------------------------------------------------------------------
# 1. Locate a compose command (Podman first, Docker as a fallback)
# ---------------------------------------------------------------------------------------------------
if command -v podman-compose >/dev/null 2>&1; then
    COMPOSE=(podman-compose)
elif command -v podman >/dev/null 2>&1 && podman compose version >/dev/null 2>&1; then
    COMPOSE=(podman compose)
elif command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
    COMPOSE=(docker compose)
else
    echo "error: need podman-compose, 'podman compose', or 'docker compose' on PATH." >&2
    exit 1
fi

# ---------------------------------------------------------------------------------------------------
# 2. Helpers (health waits, per F-17: "start the stack -> health wait -> developer watch/URLs")
# ---------------------------------------------------------------------------------------------------

# True when an HTTP probe tool (curl or wget) is available.
have_http_probe() { command -v curl >/dev/null 2>&1 || command -v wget >/dev/null 2>&1; }

# Return 0 when $1 responds 2xx, non-zero otherwise. Uses curl if present, else wget.
http_ok() {
    local url="$1"
    if command -v curl >/dev/null 2>&1; then
        curl -fsS -o /dev/null --max-time 5 "$url" >/dev/null 2>&1
    else
        wget -q -T 5 -O /dev/null "$url" >/dev/null 2>&1
    fi
}

# Poll $1 until it returns 200, the deadline ($2 seconds) passes, or the optional pid ($3) exits.
#   returns 0 ready | 1 timed out | 2 watched process exited | 3 no probe tool
wait_for_http_ready() {
    local url="$1" timeout_seconds="$2" watch_pid="${3:-}"
    if ! have_http_probe; then
        return 3
    fi
    local deadline=$(( SECONDS + timeout_seconds ))
    while (( SECONDS < deadline )); do
        if [[ -n "$watch_pid" ]] && ! kill -0 "$watch_pid" 2>/dev/null; then
            return 2
        fi
        if http_ok "$url"; then
            return 0
        fi
        sleep 2
    done
    return 1
}

# Best-effort wait for postgres to accept connections. Never fails the script: the application's own
# bounded boot retry (SchemaMigrationRunner) tolerates a database that is still coming up. Prefers a
# real `pg_isready` when the host has the client tools, and otherwise settles for a TCP connect on the
# loopback port compose publishes (127.0.0.1:5432).
wait_for_postgres() {
    local user="${POSTGRES_USER:-myrestaurant}" db="${POSTGRES_DB:-myrestaurant}"
    local host="127.0.0.1" port="5432"
    local deadline=$(( SECONDS + 60 ))
    echo "info: waiting for postgres on ${host}:${port} ..."
    while (( SECONDS < deadline )); do
        if command -v pg_isready >/dev/null 2>&1; then
            if pg_isready -h "$host" -p "$port" -U "$user" -d "$db" >/dev/null 2>&1; then
                echo "info: postgres is ready."
                return 0
            fi
        elif (exec 3<>"/dev/tcp/${host}/${port}") 2>/dev/null; then
            echo "info: postgres port is accepting connections."
            return 0
        fi
        sleep 2
    done
    echo "warning: postgres was not confirmed ready within 60s; continuing (the app retries its first connection)." >&2
    return 0
}

# Stop a backgrounded web app (and any children) as cleanly as possible.
stop_web_app() {
    local pid="${1:-}"
    [[ -z "$pid" ]] && return 0
    kill -0 "$pid" 2>/dev/null || return 0
    kill -TERM -- "-$pid" 2>/dev/null || true   # whole process group, if it is a session leader
    pkill -TERM -P "$pid" 2>/dev/null || true   # otherwise its direct children
    kill -TERM "$pid" 2>/dev/null || true
    local waited=0
    while (( waited < 10 )); do
        kill -0 "$pid" 2>/dev/null || return 0
        sleep 0.5
        waited=$(( waited + 1 ))
    done
    kill -KILL -- "-$pid" 2>/dev/null || true
    kill -KILL "$pid" 2>/dev/null || true
    return 0
}

# ---------------------------------------------------------------------------------------------------
# 3. Optional Uptrace -> standard OTLP translation (§12, §14.4)
# ---------------------------------------------------------------------------------------------------
if [[ -n "${UPTRACE_DSN:-}" && -z "${OTEL_EXPORTER_OTLP_ENDPOINT:-}" ]]; then
    export OTEL_EXPORTER_OTLP_ENDPOINT="https://otlp.uptrace.dev"
    export OTEL_EXPORTER_OTLP_HEADERS="uptrace-dsn=${UPTRACE_DSN}"
    export OTEL_SERVICE_NAME="${OTEL_SERVICE_NAME:-myrestaurant}"
    echo "info: translated UPTRACE_DSN into OTEL_EXPORTER_OTLP_* for this run."
fi

# ---------------------------------------------------------------------------------------------------
# 4. --containers-only: full stack in containers, wait for health, then exit
# ---------------------------------------------------------------------------------------------------
if [[ "$MODE" == "containers" ]]; then
    echo "info: bringing up the full dev stack (postgres + caddy + web) in containers..."
    "${COMPOSE[@]}" --profile dev up -d --build

    echo "info: waiting for the web container to report ready at http://localhost:8080/healthz/ready ..."
    ready_rc=0
    wait_for_http_ready "http://127.0.0.1:8080/healthz/ready" 180 || ready_rc=$?
    case "$ready_rc" in
        0)
            echo "info: dev stack up and healthy."
            echo "info:   https://localhost:8443  (Caddy TLS — trust its local CA on first use)"
            echo "info:   http://localhost:8080   (web container, loopback only)"
            echo "info: the stack keeps running in the background. Stop it with: ${COMPOSE[*]} --profile dev down"
            exit 0
            ;;
        3)
            echo "warning: neither curl nor wget is installed, so readiness could not be auto-verified." >&2
            echo "info: the stack was started; check it with: ${COMPOSE[*]} ps  and  ${COMPOSE[*]} logs web"
            exit 0
            ;;
        *)
            echo "error: the web container did not become ready within 180s." >&2
            echo "info: inspect it with: ${COMPOSE[*]} ps  and  ${COMPOSE[*]} logs web" >&2
            echo "info: the stack is left running so you can debug. Stop it with: ${COMPOSE[*]} --profile dev down" >&2
            exit 1
            ;;
    esac
fi

# ---------------------------------------------------------------------------------------------------
# 5. Host modes (default watch and --smoke) both need the .NET SDK and a running postgres
# ---------------------------------------------------------------------------------------------------
if ! command -v dotnet >/dev/null 2>&1; then
    echo "error: the .NET SDK is required for host-dev. Install it or use ./run.sh --containers-only." >&2
    exit 1
fi

echo "info: starting postgres in a container..."
"${COMPOSE[@]}" up -d postgres
wait_for_postgres

# Dev defaults. These mirror the application's own fallbacks; set explicitly so intent is visible.
export ASPNETCORE_ENVIRONMENT="Development"
export RESTAURANT_PUBLIC_ORIGIN="${RESTAURANT_PUBLIC_ORIGIN:-https://localhost:8443}"
export RESTAURANT_TIME_ZONE="${RESTAURANT_TIME_ZONE:-America/New_York}"
export RESTAURANT_CURRENCY_CODE="${RESTAURANT_CURRENCY_CODE:-USD}"
export RESTAURANT_DATABASE_CONNECTION_STRING="${RESTAURANT_DATABASE_CONNECTION_STRING:-Host=localhost;Port=5432;Database=${POSTGRES_DB:-myrestaurant};Username=${POSTGRES_USER:-myrestaurant};Password=${POSTGRES_PASSWORD:-myrestaurant}}"
export DATA_PROTECTION_KEYS_DIRECTORY="${DATA_PROTECTION_KEYS_DIRECTORY:-$PWD/.dataprotection}"
mkdir -p "$DATA_PROTECTION_KEYS_DIRECTORY"

# Ensure the ASP.NET Core dev certificate exists so Kestrel can serve https://localhost:8443.
dotnet dev-certs https >/dev/null 2>&1 || true

# ---------------------------------------------------------------------------------------------------
# 6. --smoke: boot once on the host, verify /healthz/ready, then shut down and exit
# ---------------------------------------------------------------------------------------------------
if [[ "$MODE" == "smoke" ]]; then
    health_url="http://127.0.0.1:8080/healthz/ready"
    smoke_log="$(mktemp "${TMPDIR:-/tmp}/myrestaurant-smoke.XXXXXX")"
    APP_PID=""
    # Always clean up the app and the temp log, however we leave (success, failure, or Ctrl+C).
    trap 'stop_web_app "${APP_PID:-}"; rm -f "$smoke_log"' EXIT INT TERM

    echo "info: booting the web app once for a health check (no hot reload)..."
    # A new session (when setsid exists) lets stop_web_app terminate the whole process group cleanly.
    if command -v setsid >/dev/null 2>&1; then
        setsid dotnet run --project src/MyRestaurant.WebApplication --launch-profile https >"$smoke_log" 2>&1 &
    else
        dotnet run --project src/MyRestaurant.WebApplication --launch-profile https >"$smoke_log" 2>&1 &
    fi
    APP_PID=$!

    echo "info: waiting for ${health_url} (up to 120s) ..."
    smoke_rc=0
    wait_for_http_ready "$health_url" 120 "$APP_PID" || smoke_rc=$?
    case "$smoke_rc" in
        0)
            echo "info: /healthz/ready returned 200 — config binds, migrations are current, and the database is reachable."
            echo "info:   https://localhost:8443  (Kestrel dev cert)"
            echo "info:   http://localhost:8080   (plain HTTP, loopback)"
            echo "info: smoke check passed; shutting the app down and exiting."
            exit 0
            ;;
        2)
            echo "error: the web app exited before it became ready. Last 40 log lines:" >&2
            tail -n 40 "$smoke_log" >&2
            exit 1
            ;;
        3)
            echo "warning: neither curl nor wget is installed, so /healthz/ready could not be probed." >&2
            sleep 5
            if kill -0 "$APP_PID" 2>/dev/null; then
                echo "info: the app is still running after 5s (looks healthy); shutting it down and exiting."
                exit 0
            fi
            echo "error: the app exited during boot. Last 40 log lines:" >&2
            tail -n 40 "$smoke_log" >&2
            exit 1
            ;;
        *)
            echo "error: /healthz/ready did not return 200 within 120s. Last 40 log lines:" >&2
            tail -n 40 "$smoke_log" >&2
            exit 1
            ;;
    esac
fi

# ---------------------------------------------------------------------------------------------------
# 7. Default: host watch loop (foreground hot reload)
# ---------------------------------------------------------------------------------------------------

# --- keep `dotnet watch` working under a low inotify instance limit -----------------------------
# `dotnet watch` opens several inotify instances to watch the source tree. A busy workstation can hit
# the kernel's per-user cap (default 128), and the watcher then dies with:
#   "The configured user limit (128) on the number of inotify instances has been reached".
# When the cap looks low and the caller has not already chosen a watcher, fall back to the polling
# file watcher for this run so hot reload keeps working WITHOUT root. The native (inotify) watcher is
# snappier and lighter on CPU; to prefer it, raise the cap once (needs root):
#   sudo sysctl fs.inotify.max_user_instances=1024
#   echo 'fs.inotify.max_user_instances=1024' | sudo tee /etc/sysctl.d/99-inotify.conf   # persist
# To force the native watcher regardless of the cap, run with DOTNET_USE_POLLING_FILE_WATCHER=0.
if [[ -z "${DOTNET_USE_POLLING_FILE_WATCHER:-}" ]]; then
    inotify_instance_limit="$(cat /proc/sys/fs/inotify/max_user_instances 2>/dev/null || echo 0)"
    [[ "$inotify_instance_limit" =~ ^[0-9]+$ ]] || inotify_instance_limit=0
    if (( inotify_instance_limit < 256 )); then
        export DOTNET_USE_POLLING_FILE_WATCHER=1
        echo "info: inotify instance limit is ${inotify_instance_limit} (< 256); using the polling file watcher for hot reload."
        echo "info: for the snappier native watcher, raise it once: sudo sysctl fs.inotify.max_user_instances=1024"
    fi
fi

cat <<'BANNER'
────────────────────────────────────────────────────────────────────────────
  FOREGROUND DEV SERVER (hot reload) — this will NOT exit on its own.
    • App:  https://localhost:8443   (Kestrel dev cert)
            http://localhost:8080    (plain HTTP, loopback)
    • Edit source and it hot-reloads. Press Ctrl+C to stop
      (dotnet watch may ask for a second Ctrl+C to force-exit).
    • Want a run that boots, verifies /healthz/ready, and EXITS instead?
        ./run.sh --smoke             (host, fast)
        ./run.sh --containers-only   (full container stack)
    • Need a public demo URL? That is a separate, demo-only step:
        scripts/quick_tunnel.sh      (*.trycloudflare.com — password+TOTP only)
────────────────────────────────────────────────────────────────────────────
BANNER

echo "info: starting the web app with hot reload at https://localhost:8443 ..."
exec dotnet watch --project src/MyRestaurant.WebApplication run --launch-profile https
