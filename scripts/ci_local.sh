#!/usr/bin/env bash
#
# Run the CI gates locally (TECHNICAL_SPECIFICATION §16.4). Idempotent; changes nothing in the tree.
#
#   scripts/ci_local.sh              tree, governance, shell lint, restore, strict build, tests
#   scripts/ci_local.sh --with-smoke ...and then `bash run.sh --smoke`
#   scripts/ci_local.sh --with-e2e   ...and then the §16.3 Playwright scenarios (browser required)
#   scripts/ci_local.sh --with-all   both of the above
#   scripts/ci_local.sh --help       this text
#
# CI builds with -p:ContinuousIntegrationBuild=true, which flips TreatWarningsAsErrors. A plain
# `dotnet build` is deliberately more forgiving, so this asks the stricter question on purpose.
# It cannot reproduce CI's boot-smoke job, which builds the Containerfile.
#

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
        awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$0"
        exit 0
        ;;
    *)
        echo "error: unknown argument '$1' (expected --with-smoke, --with-e2e, --with-all, --help, or nothing)." >&2
        exit 1
        ;;
esac

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

announce "tree hygiene"
bash scripts/check_tree.sh

announce "repository governance"
bash scripts/check_repository.sh

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
dotnet test --solution MyRestaurant.slnx \
    --configuration Release \
    --no-build \
    -p:ContinuousIntegrationBuild=true

announce "vulnerable package audit (advisory)"
dotnet list MyRestaurant.slnx package --vulnerable --include-transitive || true

if (( WITH_E2E )); then
    announce "end to end (§16.3 Playwright scenarios)"
    echo "MYRESTAURANT_E2E=1 — each scenario creates its own database and boots the built app."
    echo "The first run downloads Chromium into ~/.cache/ms-playwright."
    MYRESTAURANT_E2E=1 dotnet test --project tests/MyRestaurant.EndToEnd.Tests/MyRestaurant.EndToEnd.Tests.csproj \
        --configuration Release \
        --no-build \
        -p:ContinuousIntegrationBuild=true
fi

if (( WITH_SMOKE )); then
    announce "boot smoke (bash run.sh --smoke)"
    bash run.sh --smoke
fi

echo
echo "────────────────────────────────────────────────────────────────────────────"
echo "  all local CI gates passed."
if (( ! WITH_E2E )); then
    echo "  (not run: the §16.3 end-to-end scenarios — add --with-e2e)"
fi
if (( ! WITH_SMOKE )); then
    echo "  (not run: the boot smoke — add --with-smoke, or bash run.sh --containers-only)"
fi
echo "────────────────────────────────────────────────────────────────────────────"
