#!/usr/bin/env bash
#
# Run the CI gates locally (TECHNICAL_SPECIFICATION §16.4). Idempotent, and it changes nothing in
# the working tree.
#
#   scripts/ci_local.sh              shell lint, restore, strict Release build, full test suite
#   scripts/ci_local.sh --with-smoke ...and then ./run.sh --smoke (boots the app once, checks health)
#   scripts/ci_local.sh --with-e2e   ...and then the §16.3 Playwright scenarios (browser required)
#   scripts/ci_local.sh --with-all   both of the above
#   scripts/ci_local.sh --help       this text
#
# Why this exists: .github/workflows/ci.yml builds with -p:ContinuousIntegrationBuild=true, which
# flips TreatWarningsAsErrors in Directory.Build.props. A plain `dotnet build` is deliberately more
# forgiving than that, so "it builds here" and "it builds in CI" are two different questions unless
# something asks the second one on purpose. This asks it.
#
# The one gate this cannot reproduce is CI's boot-smoke job, which builds the Containerfile and
# boots the resulting image against a real PostgreSQL. `--with-smoke` is the closest local
# equivalent: same migrations, same readiness probe, but the app runs on the host rather than in the
# image. For the real thing, `./run.sh --containers-only`.
#
# `--with-e2e` sets MYRESTAURANT_E2E=1, which is the only thing that turns the §16.3 end-to-end
# scenarios from skips into scenarios. They need a container engine and a Chromium build; the first
# run downloads roughly 150 MB into ~/.cache/ms-playwright, which is why nothing does this by
# default. On a minimal host the browser's shared libraries may also be missing — install them once
# with `playwright install --with-deps chromium` (that step needs root, so the harness never tries).

set -euo pipefail
cd "$(dirname "$0")/.."

WITH_SMOKE=0
WITH_E2E=0

case "${1:-}" in
    "")
        ;;
    --with-smoke)
        WITH_SMOKE=1
        ;;
    --with-e2e)
        WITH_E2E=1
        ;;
    --with-all)
        WITH_SMOKE=1
        WITH_E2E=1
        ;;
    --help | -h)
        # Print the header comment block and stop at the first line of real code, so the help text
        # cannot drift out of step with a hard-coded line range.
        awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$0"
        exit 0
        ;;
    *)
        echo "error: unknown argument '$1' (expected --with-smoke, --with-e2e, --with-all, --help, or nothing)." >&2
        exit 1
        ;;
esac

# ---------------------------------------------------------------------------------------------------
# Reporting helpers. Every gate announces itself, so a failure is attributable at a glance.
# ---------------------------------------------------------------------------------------------------
STEP_NUMBER=0

announce() {
    STEP_NUMBER=$(( STEP_NUMBER + 1 ))
    echo
    echo "────────────────────────────────────────────────────────────────────────────"
    echo "  ${STEP_NUMBER}. $1"
    echo "────────────────────────────────────────────────────────────────────────────"
}

fail() {
    echo "error: $1" >&2
    exit 1
}

# ---------------------------------------------------------------------------------------------------
# 1. Shell scripts: every tracked *.sh must parse, and pass shellcheck when it is installed.
# ---------------------------------------------------------------------------------------------------
announce "shell scripts"

if ! command -v git >/dev/null 2>&1; then
    fail "git is required (the script list comes from 'git ls-files')."
fi

script_paths=()
while IFS= read -r script_path; do
    [[ -n "$script_path" ]] && script_paths+=("$script_path")
done < <(git ls-files '*.sh')

if (( ${#script_paths[@]} == 0 )); then
    fail "no tracked *.sh files found; is this the repository root?"
fi

printf 'checking %d script(s)\n' "${#script_paths[@]}"
for script_path in "${script_paths[@]}"; do
    printf '  bash -n %s\n' "$script_path"
    bash -n "$script_path"
done

if command -v shellcheck >/dev/null 2>&1; then
    echo "  shellcheck --severity=warning (blocking, as in CI)"
    shellcheck --severity=warning "${script_paths[@]}"
    echo "  shellcheck --severity=style (advisory)"
    shellcheck --severity=style "${script_paths[@]}" || true
else
    echo "warning: shellcheck is not installed, so only the parse check ran here." >&2
    echo "         CI runs it regardless; install it with 'sudo dnf install ShellCheck'." >&2
fi

# ---------------------------------------------------------------------------------------------------
# 2. Restore, build strictly, test. Same flags CI uses, in the same order.
# ---------------------------------------------------------------------------------------------------
if ! command -v dotnet >/dev/null 2>&1; then
    fail "the .NET SDK is required for the build and test gates."
fi

announce "restore"
dotnet restore MyRestaurant.slnx

announce "build (Release, warnings as errors)"
dotnet build MyRestaurant.slnx \
    --configuration Release \
    --no-restore \
    -p:ContinuousIntegrationBuild=true

announce "test"
# The data-access tests need a reachable container engine and skip without one. On rootless Podman
# that means the user API socket must be active: systemctl --user enable --now podman.socket
dotnet test MyRestaurant.slnx \
    --configuration Release \
    --no-build \
    -p:ContinuousIntegrationBuild=true

# ---------------------------------------------------------------------------------------------------
# 3. Optional: the §16.3 end-to-end scenarios, in a real browser.
# ---------------------------------------------------------------------------------------------------
if (( WITH_E2E )); then
    announce "end to end (§16.3 Playwright scenarios)"
    echo "MYRESTAURANT_E2E=1 — each scenario creates its own database and boots the built app."
    echo "The first run downloads Chromium into ~/.cache/ms-playwright."
    MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests/MyRestaurant.EndToEnd.Tests.csproj \
        --configuration Release \
        --no-build \
        -p:ContinuousIntegrationBuild=true
fi

# ---------------------------------------------------------------------------------------------------
# 4. Optional: boot once and probe /healthz/ready.
# ---------------------------------------------------------------------------------------------------
if (( WITH_SMOKE )); then
    announce "boot smoke (./run.sh --smoke)"
    ./run.sh --smoke
fi

echo
echo "────────────────────────────────────────────────────────────────────────────"
echo "  all local CI gates passed."
if (( ! WITH_E2E )); then
    echo "  (not run: the §16.3 end-to-end scenarios — add --with-e2e)"
fi
if (( ! WITH_SMOKE )); then
    echo "  (not run: the boot smoke — add --with-smoke, or ./run.sh --containers-only)"
fi
echo "────────────────────────────────────────────────────────────────────────────"
