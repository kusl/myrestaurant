#!/usr/bin/env bash
#
# Dev entry point (TECHNICAL_SPECIFICATION §14.4). Idempotent.
#
#   ./run.sh                     start postgres in a container, then `dotnet watch` the web app
#                                on the host (Kestrel serves https://localhost:8443 via the dev cert)
#   ./run.sh --containers-only   bring the full dev stack up in containers (postgres + caddy + web)
#                                and exit, without the host watch loop
#
# The database runs in a container either way; only the web app differs. Running the web app on the
# host gives fast hot reload; --containers-only mirrors the container topology end to end.

set -euo pipefail
cd "$(dirname "$0")"

# --- locate a compose command (Podman first, Docker as a fallback) ------------------------------
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

CONTAINERS_ONLY=0
if [[ "${1:-}" == "--containers-only" ]]; then
    CONTAINERS_ONLY=1
elif [[ -n "${1:-}" ]]; then
    echo "error: unknown argument '$1' (expected --containers-only or nothing)." >&2
    exit 1
fi

# --- optional Uptrace -> standard OTLP translation (§12, §14.4) ---------------------------------
if [[ -n "${UPTRACE_DSN:-}" && -z "${OTEL_EXPORTER_OTLP_ENDPOINT:-}" ]]; then
    export OTEL_EXPORTER_OTLP_ENDPOINT="https://otlp.uptrace.dev"
    export OTEL_EXPORTER_OTLP_HEADERS="uptrace-dsn=${UPTRACE_DSN}"
    export OTEL_SERVICE_NAME="${OTEL_SERVICE_NAME:-myrestaurant}"
    echo "info: translated UPTRACE_DSN into OTEL_EXPORTER_OTLP_* for this run."
fi

if [[ "$CONTAINERS_ONLY" -eq 1 ]]; then
    echo "info: bringing up the full dev stack (postgres + caddy + web) in containers..."
    "${COMPOSE[@]}" --profile dev up -d --build
    echo "info: dev stack up. App: https://localhost:8443  (trust Caddy's local CA on first use)."
    exit 0
fi

# --- host-dev: postgres in a container, web via dotnet watch ------------------------------------
if ! command -v dotnet >/dev/null 2>&1; then
    echo "error: the .NET SDK is required for host-dev. Install it or use ./run.sh --containers-only." >&2
    exit 1
fi

echo "info: starting postgres in a container..."
"${COMPOSE[@]}" up -d postgres

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

echo "info: starting the web app with hot reload at https://localhost:8443 ..."
exec dotnet watch --project src/MyRestaurant.WebApplication run --launch-profile https
