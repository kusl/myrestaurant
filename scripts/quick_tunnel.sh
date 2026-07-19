#!/usr/bin/env bash
#
# Quick tunnel helper (TECHNICAL_SPECIFICATION §14.3) — DEMOS ONLY.
#
# A *.trycloudflare.com hostname is on the Public Suffix List and is random per run. A passkey's
# relying-party ID binds to that subdomain, so passkeys registered during a quick-tunnel demo die
# when the tunnel stops. Demos must therefore authenticate with password + TOTP, not passkeys.
#
# The tunnel only exists while this process runs — there is deliberately no "print a URL and exit"
# mode, because exiting kills the URL. Bring the stack up FIRST (e.g. `./run.sh --containers-only`),
# then run this script and leave it in the foreground; Ctrl+C closes the tunnel. Override the target
# with TUNNEL_TARGET if needed.

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

# ---------------------------------------------------------------------------------------------------
# Pre-flight: refuse to open a tunnel onto nothing. A quick tunnel to a dead port "works" from
# cloudflared's point of view and then 502s in front of the audience — fail here instead, with the
# command that fixes it.
# ---------------------------------------------------------------------------------------------------
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

probe_rc=0
http_ok "${TARGET%/}/healthz/ready" || probe_rc=$?
if (( probe_rc == 3 )); then
    echo "warning: neither curl nor wget is installed; skipping the readiness pre-check." >&2
elif (( probe_rc != 0 )); then
    # /healthz/ready may 503 while migrations settle; accept any answer at the root as "alive".
    if ! http_ok "$TARGET"; then
        echo "error: nothing is answering at $TARGET — there is no point opening a tunnel to it." >&2
        echo "hint : bring the stack up first, then re-run this script:" >&2
        echo "           ./run.sh --containers-only" >&2
        echo "       (or point TUNNEL_TARGET at wherever the app is listening)" >&2
        exit 1
    fi
    echo "warning: $TARGET answers, but /healthz/ready is not 200 yet — the demo may need a moment." >&2
fi

# ---------------------------------------------------------------------------------------------------
# Locate a cloudflared (binary first, then a containerized fallback on the host network).
# ---------------------------------------------------------------------------------------------------
if command -v cloudflared >/dev/null 2>&1; then
    RUNNER=(cloudflared)
elif command -v podman >/dev/null 2>&1; then
    RUNNER=(podman run --rm --network host docker.io/cloudflare/cloudflared)
elif command -v docker >/dev/null 2>&1; then
    RUNNER=(docker run --rm --network host docker.io/cloudflare/cloudflared)
else
    echo "error: need cloudflared, podman, or docker on PATH." >&2
    exit 1
fi

echo "info: opening a quick tunnel to $TARGET (foreground — Ctrl+C closes the tunnel and kills the URL) ..." >&2

# ---------------------------------------------------------------------------------------------------
# Run cloudflared in the foreground, passing its log through unchanged, and surface the assigned
# *.trycloudflare.com URL prominently the moment it appears (it is otherwise buried in log noise).
# The pipeline keeps cloudflared as the long-lived process; its exit status is the script's.
# ---------------------------------------------------------------------------------------------------
"${RUNNER[@]}" tunnel --url "$TARGET" 2>&1 | {
    url_announced=0
    while IFS= read -r line; do
        printf '%s\n' "$line"
        if (( url_announced == 0 )) && [[ "$line" =~ (https://[A-Za-z0-9.-]+\.trycloudflare\.com) ]]; then
            url_announced=1
            printf '\n────────────────────────────────────────────────────────────────────────────\n' >&2
            printf '  PUBLIC DEMO URL:  %s\n' "${BASH_REMATCH[1]}" >&2
            printf '  Password + TOTP only — passkeys registered here die with this tunnel.\n' >&2
            printf '  The URL lives exactly as long as this process. Ctrl+C ends the demo.\n' >&2
            printf '────────────────────────────────────────────────────────────────────────────\n\n' >&2
        fi
    done
}
