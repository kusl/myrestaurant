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
#   TUNNEL_TARGET         what cloudflared points at (default http://127.0.0.1:8080)
#   TUNNEL_URL_WAIT       seconds to wait for the tunnel URL to appear (default 90)
#   CLOUDFLARED_IMAGE     the tunnel client image, used only when no host cloudflared is on PATH
#                         (default docker.io/cloudflare/cloudflared:latest — fully qualified, and
#                         held in a named variable rather than written into the run command, per
#                         §14.1 and F-60; scripts/dev_instance.sh reads the same variable)

set -euo pipefail
cd "$(dirname "$0")/.."

# 127.0.0.1 rather than localhost, and that is a correctness choice rather than a style one (F-56).
# compose.yaml publishes the web port as `127.0.0.1:8080:8080` — one address, IPv4, and no listener on
# ::1. A name that resolves to ::1 first therefore depends on every client that dials it falling back
# to the second address: curl and GNU wget do, BusyBox wget does not, and cloudflared's error names the
# address it failed on, so the operator reads `dial tcp [::1]:8080: connection refused` and goes looking
# for an IPv6 problem that is not there. run.sh has probed the literal since M1; this is that rule
# stated once for every helper instead of applied to one of them.
TARGET="${TUNNEL_TARGET:-http://127.0.0.1:8080}"
URL_WAIT="${TUNNEL_URL_WAIT:-90}"

# Fully qualified, and in a variable rather than in the run command below (§14.1, F-60). Two reasons,
# and the second is the one that made it a variable. A short name is resolved through
# `unqualified-search-registries`, which a stock Debian ships commented out — so `cloudflared` alone
# would fail on the canonical host with a message about aliases. And a reference written inline at a
# `podman run` is a reference no reading of this tree can find: the audit in
# ContainerImageReferenceContractTests reads YAML `image:` keys, `Containerfile`'s `FROM` operands,
# and values assigned to a name ending in `_IMAGE`, so a literal spelled anywhere else is outside
# every gate this project has. scripts/dev_instance.sh has read this same variable since Slice 27.
CLOUDFLARED_IMAGE="${CLOUDFLARED_IMAGE:-docker.io/cloudflare/cloudflared:latest}"

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
# 127.0.0.1:8080 (the loopback-published web port) is reachable.
# ---------------------------------------------------------------------------------------------------
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
# Does this engine apply compose.yaml's defaults? On Debian trixie's podman-compose it does not, and
# the placeholder text itself reaches the containers — the application refuses to start and initdb
# wipes its data directory on a POSTGRES_USER made of braces (F-57). Asked before anything starts,
# because the alternative is a tunnel published over a stack that was never going to come up.
substitution_status=0
bash scripts/check_compose_substitution.sh || substitution_status=$?
if (( substitution_status == 3 )); then
    die "this engine does not apply compose.yaml's defaults, so the stack cannot start (see the report above)."
fi

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

TAIL_PID=""
CLEANED_UP=0

# Installed on INT, TERM and EXIT, which are three independent traps rather than one. On Ctrl+C bash
# runs the INT handler and then runs the EXIT handler on its way out, so a handler registered for both
# executes twice for one keystroke — and this one announces itself, so it printed its closing line
# twice (F-61). Neither the kill nor the rm cared; the message did, because two identical lines read
# like two tunnels or one that would not close.
#
# The guard is what fixes it, not a different set of signals: whichever of the three arrives first,
# the body runs once. `trap -` then disarms the remaining two so the later arrival is absent rather
# than merely quiet, which is also what keeps `wait` from being re-entered after the child is reaped.
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

# No second `trap` here, deliberately. The one installed above already kills whatever $TAIL_PID names,
# and re-installing the handler is how the closing line came to be printed twice (F-61): a second
# `trap` on the same three signals replaces the first but changes nothing about the fact that a signal
# trap and the EXIT trap both run.
#
# Block on the tunnel; when it exits (or Ctrl+C), cleanup runs — once.
wait "$TUNNEL_PID"
