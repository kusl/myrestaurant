#!/usr/bin/env bash
#
# Quick tunnel demo (TECHNICAL_SPECIFICATION §14.3, ADR-0005) — one command.
#
# Brings the stack up, opens a Cloudflare Quick Tunnel to it, discovers the assigned
# *.trycloudflare.com URL, sets RESTAURANT_PUBLIC_ORIGIN to that URL so QR join links resolve, and
# holds the tunnel in the foreground. Ctrl+C closes the tunnel (the stack keeps running).
#
# PASSKEYS WORK ON A QUICK TUNNEL. The WebAuthn relying-party ID is derived per request from the
# origin host and RESTAURANT_TRUSTED_ORIGIN_PATTERNS trusts https://*.trycloudflare.com by default
# (ADR-0005), so you can register and sign in with a passkey — including a passkey-only account —
# during the demo. The ONE caveat: a *.trycloudflare.com hostname is random per run, so a NEW run
# gets a NEW URL and passkeys registered on a previous URL will not carry over (re-register them).
# For anything that must persist across runs, use the production named tunnel (CLOUDFLARE_TUNNEL_TOKEN).
#
# Usage:
#   scripts/quick_tunnel.sh
#
# Environment:
#   TUNNEL_TARGET         what cloudflared points at (default http://localhost:8080)
#   TUNNEL_URL_WAIT       seconds to wait for the tunnel URL to appear (default 90)

set -euo pipefail
cd "$(dirname "$0")/.."

TARGET="${TUNNEL_TARGET:-http://localhost:8080}"
URL_WAIT="${TUNNEL_URL_WAIT:-90}"

log()  { printf '[quick-tunnel] %s\n' "$*" >&2; }
die()  { printf '[quick-tunnel] error: %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------------------------------
# Compose engine detection (mirrors scripts/restore.sh).
# ---------------------------------------------------------------------------------------------------
if command -v podman-compose >/dev/null 2>&1; then
    COMPOSE=(podman-compose)
elif command -v podman >/dev/null 2>&1 && podman compose version >/dev/null 2>&1; then
    COMPOSE=(podman compose)
elif command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
    COMPOSE=(docker compose)
else
    die "need podman-compose, 'podman compose', or 'docker compose' on PATH."
fi

# ---------------------------------------------------------------------------------------------------
# cloudflared runner: prefer a host binary; fall back to a container on the host network so
# localhost:8080 (the loopback-published web port) is reachable.
# ---------------------------------------------------------------------------------------------------
if command -v cloudflared >/dev/null 2>&1; then
    TUNNEL_RUNNER=(cloudflared)
elif command -v podman >/dev/null 2>&1; then
    TUNNEL_RUNNER=(podman run --rm --network host docker.io/cloudflare/cloudflared)
elif command -v docker >/dev/null 2>&1; then
    TUNNEL_RUNNER=(docker run --rm --network host docker.io/cloudflare/cloudflared)
else
    die "need cloudflared, podman, or docker on PATH."
fi

http_ok() {
    local url="$1"
    if command -v curl >/dev/null 2>&1; then
        curl -fsS -o /dev/null --max-time 5 "$url" >/dev/null 2>&1
    elif command -v wget >/dev/null 2>&1; then
        wget -q -T 5 -O /dev/null "$url" >/dev/null 2>&1
    else
        return 3
    fi
}

wait_ready() {
    # Poll /healthz/ready (accept any answer at the root as "alive" while migrations settle).
    local deadline=$(( $(date +%s) + 60 ))
    while (( $(date +%s) < deadline )); do
        if http_ok "${TARGET%/}/healthz/ready" || http_ok "$TARGET"; then
            return 0
        fi
        sleep 2
    done
    return 1
}

# ---------------------------------------------------------------------------------------------------
# 1) Database first, then the web app. Compose reads RESTAURANT_PUBLIC_ORIGIN from the environment
#    (see compose.yaml's ${RESTAURANT_PUBLIC_ORIGIN:-...}); we discover and export the real value in
#    step 3, then (re)create web with it so the QR join URLs point at the tunnel. Passkeys do not
#    depend on this — they self-heal from the request origin (ADR-0005) — but join links do.
# ---------------------------------------------------------------------------------------------------
log "starting the database…"
"${COMPOSE[@]}" up -d postgres

# ---------------------------------------------------------------------------------------------------
# 2) Open the quick tunnel in the background and capture its log. cloudflared will log connection
#    errors to the target until web is up (step 4); that is expected and self-corrects.
# ---------------------------------------------------------------------------------------------------
TUNNEL_LOG="$(mktemp -t myrestaurant-quicktunnel.XXXXXX.log)"
log "opening a quick tunnel to $TARGET …"
"${TUNNEL_RUNNER[@]}" tunnel --no-autoupdate --url "$TARGET" >"$TUNNEL_LOG" 2>&1 &
TUNNEL_PID=$!

cleanup() {
    log "closing the tunnel (the stack keeps running; stop it with '${COMPOSE[*]} down')."
    kill "$TUNNEL_PID" 2>/dev/null || true
    wait "$TUNNEL_PID" 2>/dev/null || true
    rm -f "$TUNNEL_LOG" 2>/dev/null || true
}
trap cleanup INT TERM EXIT

# ---------------------------------------------------------------------------------------------------
# 3) Discover the assigned *.trycloudflare.com URL from the tunnel log.
# ---------------------------------------------------------------------------------------------------
log "waiting for the quick tunnel URL (up to ${URL_WAIT}s)…"
PUBLIC_URL=""
deadline=$(( $(date +%s) + URL_WAIT ))
while (( $(date +%s) < deadline )); do
    if ! kill -0 "$TUNNEL_PID" 2>/dev/null; then
        cat "$TUNNEL_LOG" >&2 || true
        die "cloudflared exited before announcing a URL (see log above)."
    fi
    PUBLIC_URL="$(grep -oE 'https://[A-Za-z0-9.-]+\.trycloudflare\.com' "$TUNNEL_LOG" | head -n1 || true)"
    [[ -n "$PUBLIC_URL" ]] && break
    sleep 1
done
[[ -n "$PUBLIC_URL" ]] || die "timed out waiting for the tunnel URL (see $TUNNEL_LOG)."

export RESTAURANT_PUBLIC_ORIGIN="$PUBLIC_URL"
log "public origin: $RESTAURANT_PUBLIC_ORIGIN"

# ---------------------------------------------------------------------------------------------------
# 4) (Re)create web with the discovered origin so join links resolve, then wait until it is ready.
#    Force-recreate so an already-running web (e.g. from ./run.sh --containers-only) picks up the new
#    origin; fall back to a plain up if the engine does not accept the flag.
# ---------------------------------------------------------------------------------------------------
log "starting the web app with the tunnel origin…"
"${COMPOSE[@]}" up -d --force-recreate web 2>/dev/null \
    || { "${COMPOSE[@]}" rm -sf web >/dev/null 2>&1 || true; "${COMPOSE[@]}" up -d web; }

if wait_ready; then
    log "web app is ready."
else
    log "warning: /healthz/ready did not turn green yet — the tunnel may need a moment."
fi

# ---------------------------------------------------------------------------------------------------
# 5) Banner + hold the tunnel in the foreground.
# ---------------------------------------------------------------------------------------------------
cat >&2 <<BANNER

────────────────────────────────────────────────────────────────────────────
  QUICK TUNNEL — DEMO

  PUBLIC URL:  $PUBLIC_URL

  • Passkeys WORK here: register and sign in with a passkey, or run a
    passkey-only account (username + passkey, no password).
  • A new run gets a NEW random URL — passkeys registered on a previous URL
    will not carry over. Re-register them, or use the production named tunnel
    (CLOUDFLARE_TUNNEL_TOKEN) for anything that must persist.
  • Do NOT bootstrap a real, long-lived instance through a quick tunnel.

  The URL lives exactly as long as this process. Ctrl+C closes the tunnel.
────────────────────────────────────────────────────────────────────────────

BANNER

log "streaming cloudflared log (Ctrl+C to stop):"
tail -n +1 -f "$TUNNEL_LOG" &
TAIL_PID=$!
trap 'kill "$TAIL_PID" 2>/dev/null || true; cleanup' INT TERM EXIT

# Block on the tunnel; when it exits (or Ctrl+C), the traps clean up.
wait "$TUNNEL_PID"
