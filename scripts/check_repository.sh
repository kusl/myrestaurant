#!/usr/bin/env bash
#
# Repository governance (TECHNICAL_SPECIFICATION §16.4). Reads; changes nothing.
#
#   scripts/check_repository.sh           both halves
#   scripts/check_repository.sh --offline the tree half only, no network, no token
#   scripts/check_repository.sh --help    this text
#
# Gates: a security policy exists and is findable; the governance documents cross-reference;
# no tracked file asserts a GitHub repository setting it cannot check; and an advisory look at
# the published repository. See DOCUMENTATION_REVIEW.md F-42 and F-46 for why each is here.
#

set -euo pipefail
cd "$(dirname "$0")/.."

OFFLINE=0

case "${1:-}" in
    "")
        ;;
    --offline)
        OFFLINE=1
        ;;
    --help | -h)
        awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$0"
        exit 0
        ;;
    *)
        echo "error: unknown argument '$1' (expected --offline, --help, or nothing)." >&2
        exit 1
        ;;
esac

RECORD_FILES=(
    "docs/DOCUMENTATION_REVIEW.md"
    "docs/BUILD_PROGRESS.md"
    "scripts/check_repository.sh"
    "docs/llm/*"
    "docs/progress/*"
)

PLATFORM_STATE_CLAIMS=(
    "issues are disabled"
    "issues are turned off"
    "the issue tracker is disabled"
    "pull requests are disabled"
    "discussions are disabled"
    "the wiki is disabled"
    "the image(s)? (is|are) public"
    "the package(s)? (is|are) public"
    "no registry (login|sign-in|authentication)"
    "private vulnerability reporting is (on|enabled)"
)

GATE_NUMBER=0
FAILURES=0
WARNINGS=0

announce() {
    GATE_NUMBER=$(( GATE_NUMBER + 1 ))
    echo
    echo "  ${GATE_NUMBER}. $1"
}

note_failure() {
    echo "     FAIL: $1" >&2
    FAILURES=$(( FAILURES + 1 ))
}

note_warning() {
    echo "     WARN: $1" >&2
    WARNINGS=$(( WARNINGS + 1 ))
}

note_warning_detail() {
    echo "           $1" >&2
}

is_record_file() {
    local candidate="$1" record
    for record in "${RECORD_FILES[@]}"; do
        [[ "$candidate" == $record ]] && return 0
    done
    return 1
}

if ! command -v git >/dev/null 2>&1; then
    echo "error: git is required (the file list comes from 'git ls-files')." >&2
    exit 1
fi

announce "a security policy exists"

if ! git ls-files --error-unmatch SECURITY.md >/dev/null 2>&1; then
    note_failure "SECURITY.md is not tracked."
    echo "           A repository that publishes its source and refuses pull requests must" >&2
    echo "           still name a channel for a security report." >&2
elif [[ ! -s SECURITY.md ]]; then
    note_failure "SECURITY.md is empty."
else
    printf '     SECURITY.md, %d line(s)\n' "$(wc -l < SECURITY.md)"

    if ! grep -q -i -F 'Report a vulnerability' SECURITY.md; then
        note_failure "SECURITY.md does not name a reporting channel ('Report a vulnerability')."
    fi

    if ! grep -q -F '§17' SECURITY.md; then
        note_failure "SECURITY.md does not point a reporter at §17, the accepted-risks register."
    fi
fi

announce "the governance documents cross-reference"

check_reference() {
    local from="$1" to="$2"
    if [[ ! -f "$from" ]]; then
        note_failure "${from} is missing, so it cannot point at ${to}."
        return
    fi
    if ! grep -q -F "$to" "$from"; then
        note_failure "${from} does not mention ${to}."
    fi
}

check_reference README.md SECURITY.md
check_reference CONTRIBUTING.md SECURITY.md
check_reference SECURITY.md CONTRIBUTING.md
(( FAILURES == 0 )) && echo "     README -> SECURITY, CONTRIBUTING -> SECURITY, SECURITY -> CONTRIBUTING"

announce "no document asserts a repository setting"

claim_pattern="$(printf '%s|' "${PLATFORM_STATE_CLAIMS[@]}")"
claim_pattern="${claim_pattern%|}"

claim_hits=0
while IFS= read -r tracked_path; do
    [[ -n "$tracked_path" ]] || continue
    [[ -f "$tracked_path" ]] || continue
    is_record_file "$tracked_path" && continue
    if hit=$(grep -n -i -I -E "$claim_pattern" -- "$tracked_path"); then
        while IFS= read -r hit_line; do
            note_failure "${tracked_path}:${hit_line%%:*} asserts a repository setting it cannot check"
            claim_hits=$(( claim_hits + 1 ))
        done <<<"$hit"
    fi
done < <(git ls-files)

if (( claim_hits == 0 )); then
    echo "     none (exempt: ${RECORD_FILES[*]} — recording what the tree said is their job)"
else
    echo "     State the policy instead. 'Issues are not triaged' is true wherever it is read;" >&2
    echo "     a claim about a settings page is true only until somebody changes the page, and" >&2
    echo "     nothing in this tree can tell you when that happened." >&2
fi

# ---------------------------------------------------------------------------------------------------
# The platform half. Advisory from here down: every finding is a WARN and the exit code does not
# move.
# ---------------------------------------------------------------------------------------------------
announce "the published repository (advisory)"

if (( OFFLINE )); then
    echo "     SKIP: --offline was given."
elif [[ -z "${GITHUB_TOKEN:-${GH_TOKEN:-}}" ]] && ! command -v gh >/dev/null 2>&1; then
    echo "     SKIP: no GitHub token (set GITHUB_TOKEN or GH_TOKEN, or install the gh CLI)."
    echo "           CI passes one, so this half runs there."
elif ! command -v curl >/dev/null 2>&1 || ! command -v python3 >/dev/null 2>&1; then
    echo "     SKIP: curl and python3 are both needed to read the API."
else
    api_token="${GITHUB_TOKEN:-${GH_TOKEN:-}}"
    if [[ -z "$api_token" ]]; then
        api_token="$(gh auth token 2>/dev/null || true)"
    fi

    # GITHUB_REPOSITORY is set in Actions. Off a workstation the origin remote is the honest
    # answer, and a remote that is not GitHub means there is nothing here to inspect — which is a
    # perfectly good state for a fork on somebody's own Gitea to be in.
    repository="${GITHUB_REPOSITORY:-}"
    if [[ -z "$repository" ]]; then
        origin_url="$(git remote get-url origin 2>/dev/null || true)"
        case "$origin_url" in
            *github.com[:/]*)
                repository="${origin_url##*github.com}"
                repository="${repository#:}"
                repository="${repository#/}"
                repository="${repository%.git}"
                ;;
        esac
    fi

    if [[ -z "$repository" || "$repository" != */* ]]; then
        echo "     SKIP: no GitHub repository to inspect (origin is not on github.com)."
    elif [[ -z "$api_token" ]]; then
        echo "     SKIP: a token was expected but none could be read."
    else
        echo "     ${repository}"

        # Two calls. The settings live on the repository object; private vulnerability reporting
        # has its own endpoint and needs a token with administration:read, which a fork's
        # pull-request token will not have — hence 403 being reported as unknown rather than as a
        # finding.
        api_get() {
            local path="$1"
            curl --silent --show-error --max-time 15 \
                --write-out '\n%{http_code}' \
                --header "Authorization: Bearer ${api_token}" \
                --header "Accept: application/vnd.github+json" \
                --header "X-GitHub-Api-Version: 2022-11-28" \
                "https://api.github.com/repos/${repository}${path}" 2>/dev/null || true
        }

        repository_response="$(api_get '')"
        repository_status="${repository_response##*$'\n'}"
        repository_body="${repository_response%$'\n'*}"

        if [[ "$repository_status" != "200" ]]; then
            note_warning "the repository API answered ${repository_status:-nothing}; settings unchecked."
        else
            settings="$(printf '%s' "$repository_body" | python3 -c '
import json
import sys

try:
    document = json.load(sys.stdin)
except Exception:
    print("unreadable")
    raise SystemExit(0)

print("has_issues=%s" % ("yes" if document.get("has_issues") else "no"))
print("has_wiki=%s" % ("yes" if document.get("has_wiki") else "no"))
print("description=%s" % ("set" if (document.get("description") or "").strip() else "empty"))
' 2>/dev/null || echo unreadable)"

            if [[ "$settings" == "unreadable" ]]; then
                note_warning "the repository API response could not be parsed."
            else
                while IFS= read -r settings_line; do
                    [[ -n "$settings_line" ]] && printf '     %s\n' "$settings_line"
                done <<<"$settings"

                # Not a failure either way. An open tab with an untriaged-issues policy is a
                # defensible choice and so is a closed one; what is not defensible is a document
                # claiming one while the other is true, and gate 3 is what forbids that.
                if [[ "$settings" == *"has_issues=yes"* ]]; then
                    echo "     note: the issue tracker is open. SECURITY.md relies on that for the"
                    echo "           'I cannot reach the advisory form' fallback, so this is"
                    echo "           consistent — but issues arriving there are nobody's queue"
                    echo "           unless somebody is watching."
                fi
                if [[ "$settings" == *"has_wiki=yes"* ]]; then
                    note_warning "the wiki is on, and every document in this project is in the tree."
                    note_warning_detail "A wiki is a second place for documentation to be wrong, outside the"
                    note_warning_detail "atomic-documentation rule and with no gate over it."
                fi
                if [[ "$settings" == *"description=empty"* ]]; then
                    note_warning "the repository has no description."
                    note_warning_detail "It is the first line anybody reads, and the release note tells people"
                    note_warning_detail "to pull an image from it."
                fi
            fi
        fi

        reporting_response="$(api_get '/private-vulnerability-reporting')"
        reporting_status="${reporting_response##*$'\n'}"
        reporting_body="${reporting_response%$'\n'*}"

        case "$reporting_status" in
            200)
                if printf '%s' "$reporting_body" | grep -q '"enabled"[[:space:]]*:[[:space:]]*true'; then
                    echo "     private vulnerability reporting=on"
                else
                    note_warning "private vulnerability reporting is OFF, and SECURITY.md sends"
                    note_warning_detail "reporters to it. Enable it at Settings -> Advanced Security ->"
                    note_warning_detail "Private vulnerability reporting. This is the one warning here that"
                    note_warning_detail "leaves a real gap rather than an untidy one."
                fi
                ;;
            403 | 404)
                echo "     private vulnerability reporting=unknown (token lacks administration:read)"
                ;;
            *)
                note_warning "the private-reporting endpoint answered ${reporting_status:-nothing}."
                ;;
        esac
    fi
fi

# ---------------------------------------------------------------------------------------------------
# Verdict. Only the tree half moves the exit code.
# ---------------------------------------------------------------------------------------------------
echo
if (( FAILURES > 0 )); then
    echo "repository governance FAILED: ${FAILURES} problem(s) in the tree. Nothing was modified." >&2
    exit 1
fi
if (( WARNINGS > 0 )); then
    printf 'repository governance passed, with %d advisory warning(s) about the published repository.\n' \
        "$WARNINGS"
    exit 0
fi
echo "repository governance passed."
