#!/usr/bin/env bash
#
# Quick tunnel helper (TECHNICAL_SPECIFICATION §14.3) — DEMOS ONLY.
#
# A *.trycloudflare.com hostname is on the Public Suffix List and is random per run. A passkey's
# relying-party ID binds to that subdomain, so passkeys registered during a quick-tunnel demo die
# when the tunnel stops. Demos must therefore authenticate with password + TOTP, not passkeys.
#
# Point the tunnel at the containerized web port (bring the stack up first, e.g. ./run.sh
# --containers-only). Override the target with TUNNEL_TARGET if needed.

set -euo pipefail

TARGET="${TUNNEL_TARGET:-http://localhost:8080}"

cat >&2 <<'WARNING'
────────────────────────────────────────────────────────────────────────────
  QUICK TUNNEL — DEMO ONLY
  • The *.trycloudflare.com domain is random per run and on the Public Suffix List.
  • Passkeys registered here will STOP WORKING when the tunnel closes.
  • Sign in with password + TOTP for the demo.
  • For anything persistent, use the production named tunnel (CLOUDFLARE_TUNNEL_TOKEN).
────────────────────────────────────────────────────────────────────────────
WARNING

echo "info: opening a quick tunnel to $TARGET ..." >&2

if command -v cloudflared >/dev/null 2>&1; then
    exec cloudflared tunnel --url "$TARGET"
elif command -v podman >/dev/null 2>&1; then
    exec podman run --rm --network host docker.io/cloudflare/cloudflared tunnel --url "$TARGET"
elif command -v docker >/dev/null 2>&1; then
    exec docker run --rm --network host docker.io/cloudflare/cloudflared tunnel --url "$TARGET"
else
    echo "error: need cloudflared, podman, or docker on PATH." >&2
    exit 1
fi
