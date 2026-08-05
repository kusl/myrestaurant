#!/usr/bin/env bash
#
# Repository hygiene (TECHNICAL_SPECIFICATION §16.4). Reads the tree; changes nothing in it.
#
#   scripts/check_tree.sh        run every gate
#   scripts/check_tree.sh --help this text
#
# WHY THIS EXISTS. On 2026-08-05 the whole solution stopped building. The message was
# MSB4024 — "the imported project file Directory.Build.props could not be loaded. Data at the
# root level is invalid. Line 86, position 1" — and it appeared on `dotnet clean`, on
# `restore`, on `build`, on `test` and inside the container build, because every one of those
# imports that file before it does anything else. Line 86 was a line of eighty '#' characters
# that had been appended after </Project>.
#
# It was not the only one. Twenty-one tracked files had acquired the same trailing line, and
# docs/BUILD_PROGRESS.md had acquired a second one buried mid-document, where nothing that
# looks at the end of a file would ever have found it. The line is the section separator
# export.sh writes between files in a context dump: a tool had read a dump back and taken the
# separator for content. The authoritative end of a file in that format is the byte count in
# its METADATA block, not the separator that follows it — the separator is a decoration, and
# treating a decoration as a delimiter is how twenty-one files got one.
#
# What makes that worth a permanent gate is not the mistake, it is the failure MODE. Of the
# twenty-one files, exactly six broke anything: one XML file, which broke everything, and five
# C# files, which would have failed the compile that never got to run. The other fifteen
# absorbed it in silence. In YAML, Containerfile and .env the line is a comment. In Markdown
# it renders as a heading rule. In Razor markup it is literal text on the page. In CSS it is a
# dangling selector the browser discards along with the rule that follows it. A class of
# damage that corrupts fifteen files invisibly and one file catastrophically is a class of
# damage that should be found by something that runs on every push, and the cost of finding it
# is one grep.
#
# So this script asserts the properties that make the tree machine-readable at all, before any
# tool that would report their absence as something else:
#
#   1. no context-dump separator anywhere in a tracked file
#   2. no line made only of whitespace
#   3. LF endings, and a final newline on every file
#   4. every MSBuild/solution XML file parses
#   5. every YAML file parses
#
# Gates 1 to 4 need nothing but git, grep and the Python standard library, so they are
# blocking everywhere. Gate 5 needs a YAML parser; where there is none it says so and skips,
# the way the shellcheck gate does. Nothing here is a style opinion: every one of these is a
# property some later tool silently depends on.

set -euo pipefail
cd "$(dirname "$0")/.."

case "${1:-}" in
    "")
        ;;
    --help | -h)
        # Print the header comment block and stop at the first line of real code, so the help
        # text cannot drift out of step with a hard-coded line range.
        awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$0"
        exit 0
        ;;
    *)
        echo "error: unknown argument '$1' (expected --help, or nothing)." >&2
        exit 1
        ;;
esac

# ---------------------------------------------------------------------------------------------------
# Reporting. Every gate announces itself, so a failure is attributable at a glance.
# ---------------------------------------------------------------------------------------------------
GATE_NUMBER=0
FAILURES=0

announce() {
    GATE_NUMBER=$(( GATE_NUMBER + 1 ))
    echo
    echo "  ${GATE_NUMBER}. $1"
}

# Records a failure and keeps going. Every gate reports everything it found before the script
# exits, because "the tree is malformed in four places" is a different morning's work from
# "the tree is malformed in one place", and a gate that stops at the first hit cannot tell the
# difference.
note_failure() {
    echo "     FAIL: $1" >&2
    FAILURES=$(( FAILURES + 1 ))
}

if ! command -v git >/dev/null 2>&1; then
    echo "error: git is required (the file list comes from 'git ls-files')." >&2
    exit 1
fi

tracked_files=()
while IFS= read -r tracked_path; do
    [[ -n "$tracked_path" ]] && tracked_files+=("$tracked_path")
done < <(git ls-files)

if (( ${#tracked_files[@]} == 0 )); then
    echo "error: no tracked files found; is this the repository root?" >&2
    exit 1
fi

echo "checking ${#tracked_files[@]} tracked file(s)"

# ---------------------------------------------------------------------------------------------------
# 1. No context-dump separator.
#
# export.sh is the one file allowed to contain the separator, because writing it is that
# script's job: the string appears in its heredocs. The exemption is by path rather than by
# some cleverer rule so that it is obvious, auditable, and impossible to widen by accident.
#
# The threshold is twenty rather than eighty. Markdown's deepest heading is six '#' and no
# language in this tree has a use for a line of twenty consecutive ones, so anything at or
# above that is a separator — including one that got re-wrapped, truncated or hand-typed on
# the way in, which an exact-length match would wave through.
# ---------------------------------------------------------------------------------------------------
announce "no context-dump separators"

separator_hits=0
for tracked_path in "${tracked_files[@]}"; do
    [[ "$tracked_path" == "export.sh" ]] && continue
    [[ -f "$tracked_path" ]] || continue
    if hit=$(grep -I -n -E '^#{20,}$' -- "$tracked_path"); then
        while IFS= read -r hit_line; do
            note_failure "${tracked_path}:${hit_line%%:*} is a context-dump separator, not content"
            separator_hits=$(( separator_hits + 1 ))
        done <<<"$hit"
    fi
done
if (( separator_hits == 0 )); then
    echo "     none (export.sh exempt: it writes them)"
else
    echo "     ${separator_hits} separator line(s) found. A tool read a context dump and kept the" >&2
    echo "     decoration between files. The end of a file in that format is the byte count in" >&2
    echo "     its METADATA block; delete these lines." >&2
fi

# ---------------------------------------------------------------------------------------------------
# 2. No whitespace-only lines.
#
# .editorconfig sets trim_trailing_whitespace for every file, and nothing enforced it. The
# gate is deliberately narrower than that setting: it fails only on lines made ENTIRELY of
# spaces or tabs, never on trailing whitespace after real content. That is the difference
# between an accident and a technique — two spaces at the end of a Markdown line are a hard
# line break, and a gate that forbade those would be wrong about Markdown rather than right
# about whitespace. A line with nothing but indentation on it has no such defence.
# ---------------------------------------------------------------------------------------------------
announce "no whitespace-only lines"

whitespace_hits=0
for tracked_path in "${tracked_files[@]}"; do
    [[ -f "$tracked_path" ]] || continue
    if hit=$(grep -I -n -E '^[[:space:]]+$' -- "$tracked_path"); then
        while IFS= read -r hit_line; do
            note_failure "${tracked_path}:${hit_line%%:*} is blank but not empty (indentation left behind)"
            whitespace_hits=$(( whitespace_hits + 1 ))
        done <<<"$hit"
    fi
done
(( whitespace_hits == 0 )) && echo "     none"

# ---------------------------------------------------------------------------------------------------
# 3. LF endings and a final newline.
#
# .editorconfig asks for both (end_of_line = lf, insert_final_newline = true), and both are
# load-bearing rather than cosmetic. A CRLF that reaches a shell script produces
# "bad interpreter: /usr/bin/env bash^M", which names the wrong problem. A file with no final
# newline is what a truncated transfer looks like, so the check doubles as the cheapest
# available detector of the other way a delivered tree can arrive damaged.
# ---------------------------------------------------------------------------------------------------
announce "LF endings and a final newline"

ending_hits=0
for tracked_path in "${tracked_files[@]}"; do
    [[ -f "$tracked_path" ]] || continue
    # An empty file has no final newline to have, and no CR to carry.
    [[ -s "$tracked_path" ]] || continue

    if grep -I -q $'\r' -- "$tracked_path"; then
        note_failure "${tracked_path} contains a carriage return (this tree is LF only)"
        ending_hits=$(( ending_hits + 1 ))
    fi

    # `tail -c 1 | wc -l` counts the newlines in the last byte: 1 when the file ends with one,
    # 0 when it does not. Command substitution strips trailing newlines, so comparing
    # "$(tail -c1 ...)" against a newline would always compare equal — the trap export.sh
    # documents having fallen into once already.
    if (( $(tail -c 1 -- "$tracked_path" | wc -l) == 0 )); then
        note_failure "${tracked_path} has no final newline (truncated, or an editor that does not add one)"
        ending_hits=$(( ending_hits + 1 ))
    fi
done
(( ending_hits == 0 )) && echo "     all files end with exactly one LF"

# ---------------------------------------------------------------------------------------------------
# 4. Every MSBuild and solution file is well-formed XML.
#
# This is the gate that would have turned the eight-hour version of 2026-08-05 into a
# thirty-second one. MSBuild imports Directory.Build.props before it evaluates anything, so a
# malformed one fails `clean`, `restore`, `build`, `test` and the container build with the same
# message, and the message says "Data at the root level is invalid" — which sounds like a
# problem with MSBuild rather than with a stray line somebody appended.
#
# xml.etree is in the Python standard library, so this needs no package and no network. It is
# a well-formedness check, not a schema check: MSBuild is the authority on whether a project
# means anything, and this only asserts that MSBuild will get far enough to have an opinion.
# ---------------------------------------------------------------------------------------------------
announce "MSBuild and solution XML parses"

if ! command -v python3 >/dev/null 2>&1; then
    echo "     SKIP: python3 is not installed, so the XML parse did not run." >&2
    echo "           CI runs it regardless." >&2
else
    xml_paths=()
    while IFS= read -r xml_path; do
        [[ -n "$xml_path" ]] && xml_paths+=("$xml_path")
    done < <(git ls-files '*.props' '*.targets' '*.csproj' '*.slnx' '*.sln')

    if (( ${#xml_paths[@]} == 0 )); then
        note_failure "no MSBuild or solution files are tracked, which cannot be right"
    else
        printf '     %d file(s)\n' "${#xml_paths[@]}"
        if ! python3 -c '
import sys
import xml.etree.ElementTree as ElementTree

failed = 0
for path in sys.argv[1:]:
    try:
        ElementTree.parse(path)
    except Exception as error:
        print(f"     FAIL: {path} is not well-formed XML: {error}", file=sys.stderr)
        failed += 1
sys.exit(1 if failed else 0)
' "${xml_paths[@]}"; then
            note_failure "one or more MSBuild or solution files are not well-formed XML (above)"
        fi
    fi
fi

# ---------------------------------------------------------------------------------------------------
# 5. Every YAML file parses.
#
# Advisory when no parser is present, blocking when one is — the same shape as the shellcheck
# gate, and for the same reason: a check that cannot run should say so rather than pass
# quietly. GitHub's Ubuntu runners ship PyYAML, so in CI this is blocking.
#
# Worth noting what this gate could NOT have caught in the incident above: a trailing '#' line
# in ci.yml and release.yml is a valid YAML comment, so both workflows parsed perfectly while
# carrying the same damage as Directory.Build.props. Gate 1 is what finds that. This gate is
# here for the other failure — a workflow that was truncated or badly re-indented, which YAML
# will reject and which nothing else in the pipeline reads early enough to blame correctly.
# ---------------------------------------------------------------------------------------------------
announce "YAML parses"

yaml_paths=()
while IFS= read -r yaml_path; do
    [[ -n "$yaml_path" ]] && yaml_paths+=("$yaml_path")
done < <(git ls-files '*.yml' '*.yaml')

if (( ${#yaml_paths[@]} == 0 )); then
    echo "     none tracked"
elif ! command -v python3 >/dev/null 2>&1 || ! python3 -c 'import yaml' 2>/dev/null; then
    echo "     SKIP: no YAML parser available, so ${#yaml_paths[@]} file(s) went unchecked." >&2
    echo "           CI runs it regardless; install it with 'sudo dnf install python3-pyyaml'." >&2
else
    printf '     %d file(s)\n' "${#yaml_paths[@]}"
    if ! python3 -c '
import sys
import yaml

failed = 0
for path in sys.argv[1:]:
    try:
        with open(path, encoding="utf-8") as handle:
            yaml.safe_load(handle)
    except Exception as error:
        print(f"     FAIL: {path} is not valid YAML: {error}", file=sys.stderr)
        failed += 1
sys.exit(1 if failed else 0)
' "${yaml_paths[@]}"; then
        note_failure "one or more YAML files do not parse (above)"
    fi
fi

# ---------------------------------------------------------------------------------------------------
# Verdict.
# ---------------------------------------------------------------------------------------------------
echo
if (( FAILURES > 0 )); then
    echo "tree hygiene FAILED: ${FAILURES} problem(s). Nothing was modified." >&2
    exit 1
fi
echo "tree hygiene passed."
