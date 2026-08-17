#!/usr/bin/env bash
#
# Repository governance (TECHNICAL_SPECIFICATION §16.4). Reads; changes nothing.
#
#   scripts/check_repository.sh           both halves
#   scripts/check_repository.sh --offline the tree half only, no network, no token
#   scripts/check_repository.sh --help    this text
#
# WHY THIS EXISTS. On 2026-08-05, with every other gate green and a first tag about to be cut,
# the published repository was asked what it looked like from outside. The answers:
#
#   has_issues                              : true
#   SECURITY.md                             : 404 at the root, in .github/ and in docs/
#   private vulnerability reporting          : disabled
#   description                             : null
#
# CONTRIBUTING.md had told every reader since rev 1 that the issue tracker was switched off.
# It was not, and had never been. So the repository invited people to read the source — which is
# what the AGPL is for, and which is exactly the population that finds security defects — and
# offered them one channel: a public tab that the only document addressing the question said did
# not exist. The first thing anyone would have done with a join-token forgery is publish it.
#
# What makes this a gate rather than a fix is the CATEGORY. Every gate before this one inspects
# the tree, and the tree was right. What was wrong lived in a settings page: state that no file
# in this repository can see, that no test can reach, and that a document nonetheless made a
# claim about in the indicative mood. F-38's lesson was that a row in the embodiment column
# should name something executable. This is that lesson applied to the one layer where nothing
# executable existed.
#
# TWO HALVES, DELIBERATELY UNEQUAL.
#
#   1. The tree half is BLOCKING and needs nothing but git and grep. It asserts that the
#      governance surface is coherent — a security policy exists, and the three documents a
#      reader arrives through point at each other so none can be rewritten into isolation — and
#      that no tracked file asserts a repository setting. That last rule is F-42 made
#      unrepeatable: a document may state POLICY ("issues are not triaged"), which is true
#      wherever it is read, and must not state PLATFORM STATE, which it cannot check.
#
#      F-46 is why that list has a second group. This gate landed green and was already wrong:
#      it enumerated the settings on the repository page, and the claim that had been sitting in
#      OPERATIONS §14 the whole time was about the PACKAGE page — "the images are public, so no
#      registry login is needed to pull", written in the indicative about a package that did not
#      exist yet. A rule stated as a rule and enforced as a list of examples is enforced as a
#      list of examples.
#
#   2. The platform half is ADVISORY and needs the GitHub API. It reports what the published
#      repository actually says. Advisory for a reason that is not squeamishness: a fork's
#      settings are the fork's business, and a gate that failed a fork's build over the
#      maintainer's disclosure preferences would be wrong about the licence this project ships
#      under. It also degrades to a WARN with no token, no network, no curl and no python3 —
#      the same shape as the shellcheck and YAML gates, and for the same reason. A check that
#      cannot run should say so rather than pass quietly.
#
# Considered and rejected: folding this into scripts/check_tree.sh. That script's five gates all
# assert that a file somebody wrote is machine-readable, they are all offline, and they are all
# blocking. Half of this one is none of those things, and a gate whose halves have different
# authority should not answer to one exit code.

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
        # Print the header comment block and stop at the first line of real code, so the help
        # text cannot drift out of step with a hard-coded line range.
        awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$0"
        exit 0
        ;;
    *)
        echo "error: unknown argument '$1' (expected --offline, --help, or nothing)." >&2
        exit 1
        ;;
esac

# ---------------------------------------------------------------------------------------------------
# Scope of the platform-state rule.
#
# RECORD_FILES are the files whose job is to say what this tree used to say. Quoting a defect is
# how a ledger works, so quoting the sentence that made F-42 possible cannot be the same offence
# as writing it. Literal path equality rather than a pattern, so the exemption is auditable and
# cannot widen by accident — the same choice scripts/check_tree.sh makes about export.sh.
#
# This script is in the list for the obvious reason: the pattern list is below.
# ---------------------------------------------------------------------------------------------------
# docs/progress/* joined the list in Slice 46, with the build log's split, and it is not a widening of
# the rule — it is the same exemption following the same text into a second file (F-96). The archived half
# of the log quotes F-42's own sentence, "Issues are disabled…", because quoting the claim is how a
# ledger records that it was made. Had this entry been left out, the first run after the split would have
# been red for a reason that has nothing to do with anything anybody changed. The glob form is used
# because the archive is a directory that will gain files; ContextDumpExclusionContractTests asserts that
# every directory export.sh archives is exempt here, so the next tranche cannot arrive without it.
RECORD_FILES=(
    "docs/DOCUMENTATION_REVIEW.md"
    "docs/BUILD_PROGRESS.md"
    "scripts/check_repository.sh"
    "docs/llm/*"
    "docs/progress/*"
)

# Assertions about GitHub repository and package settings, which a file in the tree cannot verify.
# Case insensitive extended regular expressions, matched against authored text.
#
# The second group arrived with F-46, and the reason it is here is worth keeping: gate 3 landed in
# Slice 20 to make F-42 unrepeatable, passed on its first run, and was already wrong. It listed the
# settings on the repository page and stopped there, so it never looked at the OTHER settings page
# this project depends on — the one that decides whether a published image can be pulled at all.
# OPERATIONS §14 had asserted that page's value, in the indicative, about a package that did not
# exist yet, and gate 3 reported "none".
#
# A repository's visibility and a package's visibility are separate switches, and GitHub's own
# documentation disagrees with itself about which way the second one falls for a GITHUB_TOKEN
# publish — which is the strongest possible argument for not asserting it in a document. State the
# policy: the intent is that the images be pullable without a login, and an operator who hits a
# 401 needs to be told where that switch lives, not told it was already flipped.
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

# ---------------------------------------------------------------------------------------------------
# Reporting.
# ---------------------------------------------------------------------------------------------------
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

# A continuation line for the warning above. Separate from note_warning so that the count at
# the bottom counts findings rather than lines — "three advisory warnings" and "eight advisory
# warnings" are different sentences about the same three settings, and only one is true.
note_warning_detail() {
    echo "           $1" >&2
}

is_record_file() {
    local candidate="$1" record
    for record in "${RECORD_FILES[@]}"; do
        # Removing quotes around $record allows glob wildcards like 'docs/llm/*'
        # shellcheck disable=SC2053
        [[ "$candidate" == $record ]] && return 0
    done
    return 1
}

if ! command -v git >/dev/null 2>&1; then
    echo "error: git is required (the file list comes from 'git ls-files')." >&2
    exit 1
fi

# ---------------------------------------------------------------------------------------------------
# 1. A security policy exists, and it is findable.
#
# GitHub reads SECURITY.md from the repository root, from .github/ and from docs/, and surfaces it
# in the Security tab and in the "Report a vulnerability" flow. The root is where this tree keeps
# LICENSE and CONTRIBUTING.md, so it goes there and the gate asks for it there specifically —
# "somewhere GitHub will find it" is a weaker assertion than this project's other governance files
# are held to.
# ---------------------------------------------------------------------------------------------------
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

    # §17 is the accepted-risks register. A policy that does not point at it invites a week of
    # somebody's unpaid work on a decision that was already argued and written down.
    if ! grep -q -F '§17' SECURITY.md; then
        note_failure "SECURITY.md does not point a reporter at §17, the accepted-risks register."
    fi
fi

# ---------------------------------------------------------------------------------------------------
# 2. The three documents a reader arrives through point at each other.
#
# A reader lands on README.md, or on CONTRIBUTING.md after finding pull requests are unwelcome, or
# on SECURITY.md from the Security tab. Whichever door they came through has to lead to the others,
# and the way that breaks is not deletion — it is a rewrite that forgets one edge. So the gate
# asserts the edges rather than the files.
# ---------------------------------------------------------------------------------------------------
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

# ---------------------------------------------------------------------------------------------------
# 3. No tracked file asserts a GitHub repository setting.
#
# This is the F-42 rule. "Issues are disabled" was false for as long as it was written down, and
# nothing in the tree could have known: the claim is about a checkbox on a settings page, and a
# grep over the repository cannot see one. The repair is not to keep the sentence in step by hand
# — that is what failed — but to stop making the claim. "Issues are not triaged" says what the
# project will do, is true wherever it is read, and survives somebody toggling the tab.
#
# grep -I keeps this off binary files, as scripts/check_tree.sh does, so the two gates agree about
# what a file is.
# ---------------------------------------------------------------------------------------------------
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
