#!/usr/bin/env bash
#
# Quick tunnel demo (TECHNICAL_SPECIFICATION §14.3, ADR-0005) — one command.
#
# Brings the stack up, opens a Cloudflare Quick Tunnel, discovers the assigned *.trycloudflare.com
# URL, sets RESTAURANT_PUBLIC_ORIGIN to it so QR join links resolve, and holds the tunnel in the
# foreground. Ctrl+C closes the tunnel; the stack keeps running.
#
# Every run gets a fresh hostname, so passkeys must be re-registered and a real instance must
# never be bootstrapped through one (§3.6).
#

set -euo pipefail
cd "$(dirname "$0")/.."

TARGET="${TUNNEL_TARGET:-http://127.0.0.1:8080}"
URL_WAIT="${TUNNEL_URL_WAIT:-90}"

CLOUDFLARED_IMAGE="${CLOUDFLARED_IMAGE:-docker.io/cloudflare/cloudflared:latest}"

log()  { printf '[quick-tunnel] %s\n' "$*" >&2; }
die()  { printf '[quick-tunnel] error: %s\n' "$*" >&2; exit 1; }

if command -v podman-compose >/dev/null 2>&1; then
    COMPOSE=(podman-compose)
elif command -v podman >/dev/null 2>&1 && podman compose version >/dev/null 2>&1; then
    COMPOSE=(podman compose)
elif command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
    COMPOSE=(docker compose)
else
    die "need podman-compose, 'podman compose', or 'docker compose' on PATH."
fi

if command -v cloudflared >/dev/null 2>&1; then
    TUNNEL_RUNNER=(cloudflared)
elif command -v podman >/dev/null 2>&1; then
    TUNNEL_RUNNER=(podman run --rm --network host "$CLOUDFLARED_IMAGE")
elif command -v docker >/dev/null 2>&1; then
    TUNNEL_RUNNER=(docker run --rm --network host "$CLOUDFLARED_IMAGE")
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
    local deadline=$(( $(date +%s) + 60 ))
    while (( $(date +%s) < deadline )); do
        if http_ok "${TARGET%/}/healthz/ready" || http_ok "$TARGET"; then
            return 0
        fi
        sleep 2
    done
    return 1
}

substitution_status=0
bash scripts/check_compose_substitution.sh || substitution_status=$?
if (( substitution_status == 3 )); then
    die "this engine does not apply compose.yaml's defaults, so the stack cannot start (see the report above)."
fi

log "starting the database…"
"${COMPOSE[@]}" up -d postgres

TUNNEL_LOG="$(mktemp -t myrestaurant-quicktunnel.XXXXXX.log)"
log "opening a quick tunnel to $TARGET …"
"${TUNNEL_RUNNER[@]}" tunnel --no-autoupdate --url "$TARGET" >"$TUNNEL_LOG" 2>&1 &
TUNNEL_PID=$!

TAIL_PID=""
CLEANED_UP=0

cleanup() {
    if (( CLEANED_UP )); then
        return 0
    fi
    CLEANED_UP=1
    trap - INT TERM EXIT

    log "closing the tunnel (the stack keeps running; stop it with '${COMPOSE[*]} down')."
    if [[ -n "$TAIL_PID" ]]; then
        kill "$TAIL_PID" 2>/dev/null || true
    fi
    kill "$TUNNEL_PID" 2>/dev/null || true
    wait "$TUNNEL_PID" 2>/dev/null || true
    rm -f "$TUNNEL_LOG" 2>/dev/null || true
    return 0
}
trap cleanup INT TERM EXIT

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

log "starting the web app with the tunnel origin…"
"${COMPOSE[@]}" up -d --force-recreate web 2>/dev/null \
    || { "${COMPOSE[@]}" rm -sf web >/dev/null 2>&1 || true; "${COMPOSE[@]}" up -d web; }

if wait_ready; then
    log "web app is ready."
else
    log "warning: /healthz/ready did not turn green yet — the tunnel may need a moment."
fi

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

wait "$TUNNEL_PID"
