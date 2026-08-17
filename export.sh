#!/usr/bin/env bash
# =============================================================================
# export.sh — Repository context dump for LLM consumption
#
# Location: <repository root>/export.sh
#
# Usage:  bash export.sh
#         time bash export.sh
#
# Behaviour:
#   • Resolves its own location; works regardless of the caller's directory.
#   • Silently exits if no Git repository is found at that location.
#   • Dumps only Git-tracked files to docs/llm/dump.txt, excluding this script
#     itself — its source appears exactly once, in the self-documentation
#     section.                                                          (F-25)
#   • Three kinds of path are held out of the dump, and the kinds are distinct
#     because other gates care about the difference:                    (F-96)
#       GENERATED_DIRECTORIES  produced by a tool, never authored. docs/llm/ is
#                              where this script writes, and a dump's own
#                              structure is the separator scripts/check_tree.sh
#                              forbids — so that script skips these too.
#       ARCHIVED_DIRECTORIES   authored history, deliberately withheld to keep
#                              the dump small. STILL hygiene-checked, still
#                              tracked, still edited by hand. A session cannot
#                              see these, so nothing in a session may
#                              reconstruct one — which is why every archived
#                              document must be linked by path from a document
#                              the dump does contain.
#       ELIDED_FILES           listed with their metadata and SHA-256, body
#                              replaced by one line. For unmodified boilerplate
#                              a reader gains nothing from: LICENSE is verbatim
#                              AGPL-3.0-only and the hash still pins it.
#   • Emits per-file metadata: relative path, size, SHA-256. Deliberately no
#     absolute path, mtime, permissions, owner, inode, link count, MIME type or
#     last commit — nine lines per file that no session has ever used, 164 KiB
#     across this tree, and four of them name the authoring machine.    (F-96)
#   • Includes a file-tree view of all included files.
#   • Builds the dump in a temporary file using plain redirection, renames it
#     into place atomically, and only then echoes the finished dump to the
#     console — a reader can never observe a partially written dump.txt, and
#     stderr diagnostics go to the console, never into the dump.        (F-27)
# =============================================================================

set -euo pipefail
IFS=$'\n\t'

# Deterministic, locale-independent output: sort(1) collation, prefix grouping
# in the fallback tree renderer, and byte-oriented text handling must not vary
# from machine to machine. C collation also matches Git's own byte ordering.
export LC_ALL=C

# ---------------------------------------------------------------------------
# 0. Resolve the script's own location — immune to the caller's directory
# ---------------------------------------------------------------------------
SCRIPT_PATH="$(readlink -f "${BASH_SOURCE[0]}")"
SCRIPT_DIRECTORY="$(dirname "$SCRIPT_PATH")"
SCRIPT_NAME="$(basename "$SCRIPT_PATH")"

# ---------------------------------------------------------------------------
# 1. Validate: must be inside a working Git repository
#    (F-29: '&>' already covers both streams; the trailing '2>&1' is gone)
# ---------------------------------------------------------------------------
if ! git -C "$SCRIPT_DIRECTORY" rev-parse --is-inside-work-tree &>/dev/null; then
    exit 0
fi
if ! git -C "$SCRIPT_DIRECTORY" status --porcelain &>/dev/null; then
    exit 0
fi
REPOSITORY_ROOT="$(git -C "$SCRIPT_DIRECTORY" rev-parse --show-toplevel 2>/dev/null)" || exit 0

# ---------------------------------------------------------------------------
# 2. Constants & derived paths
# ---------------------------------------------------------------------------
# Tool-produced trees. The first entry is also where this script writes, which
# is why it is the one OUTPUT_DIRECTORY is derived from rather than a second
# spelling of the same path. scripts/check_tree.sh skips exactly this list,
# because a dump's own structure is the separator that script forbids — and the
# two lists are compared by ContextDumpExclusionContractTests rather than kept
# in step by hand, which is what F-50 was about.
GENERATED_DIRECTORIES=("docs/llm")

# Authored history withheld to keep the dump small (F-96). NOT generated: these
# are hand-written, hygiene-checked, and the platform-state rule exempts them as
# record files the way it exempts docs/BUILD_PROGRESS.md. A session working from
# dump.txt cannot see them, so every document here must be linked by path from a
# document that IS in the dump — asserted, not trusted.
ARCHIVED_DIRECTORIES=("docs/progress")

# Files whose metadata and SHA-256 are worth dumping and whose bytes are not:
# unmodified boilerplate that a session reading it would learn nothing from.
# The hash still pins the exact text, so a modified licence is still detectable
# from the dump alone — which is the property that makes the elision safe rather
# than merely cheap. Exact repository-relative paths only; no globs, so the list
# cannot widen by accident.
ELIDED_FILES=("LICENSE")

# Files excluded outright. A bare name (no slash) is excluded wherever it
# appears; a name containing a slash is an exact repository-relative path. The
# script always excludes itself — by whatever name it currently has — so a
# rename can never reintroduce the double emission of F-25. The JavaScript-era
# 'yarn.lock' entry is gone.                                      (F-25, F-32)
EXCLUDED_FILES=("$SCRIPT_NAME")
EXCLUDED_FILES_DISPLAY="$(printf '%s, ' "${EXCLUDED_FILES[@]}")"
EXCLUDED_FILES_DISPLAY="${EXCLUDED_FILES_DISPLAY%, }"

EXCLUDED_PATHS_DISPLAY="$(printf '%s/, ' "${GENERATED_DIRECTORIES[@]}" "${ARCHIVED_DIRECTORIES[@]}")"
EXCLUDED_PATHS_DISPLAY="${EXCLUDED_PATHS_DISPLAY%, }"

ELIDED_FILES_DISPLAY="$(printf '%s, ' "${ELIDED_FILES[@]}")"
ELIDED_FILES_DISPLAY="${ELIDED_FILES_DISPLAY%, }"

OUTPUT_DIRECTORY="${REPOSITORY_ROOT}/${GENERATED_DIRECTORIES[0]}"
OUTPUT_FILE="${OUTPUT_DIRECTORY}/dump.txt"

# GNU date first, BSD-style fallback second — keeps the portability posture
# consistent with the stat(1) fallbacks used below.
iso_timestamp() {
    date --iso-8601=seconds 2>/dev/null || date '+%Y-%m-%dT%H:%M:%S%z'
}

TIMESTAMP="$(iso_timestamp)"
GIT_BRANCH="$(git -C "$REPOSITORY_ROOT" rev-parse --abbrev-ref HEAD 2>/dev/null || echo 'unknown')"
GIT_COMMIT="$(git -C "$REPOSITORY_ROOT" rev-parse HEAD 2>/dev/null || echo 'unknown')"
GIT_COMMIT_SHORT="$(git -C "$REPOSITORY_ROOT" rev-parse --short HEAD 2>/dev/null || echo 'unknown')"
GIT_COMMIT_MESSAGE="$(git -C "$REPOSITORY_ROOT" log -1 --pretty=format:'%s' 2>/dev/null || echo 'unknown')"
GIT_COMMIT_DATE="$(git -C "$REPOSITORY_ROOT" log -1 --pretty=format:'%ci' 2>/dev/null || echo 'unknown')"
GIT_REMOTE="$(git -C "$REPOSITORY_ROOT" remote get-url origin 2>/dev/null || echo 'none')"
GIT_STATUS_SUMMARY="$(git -C "$REPOSITORY_ROOT" status --short 2>/dev/null | head -20 || echo '')"

# The host name, the login name and the kernel string used to be dumped here.
# They are gone with the per-file metadata that named the same machine nine
# lines at a time (F-96): a dump is read to learn what the repository says, and
# the authoring computer is not part of that. The commit is the provenance that
# matters and it is still below.

# ---------------------------------------------------------------------------
# 3. Collect tracked files (staged/committed only), minus exclusions.
#    Untracked and ignored files are never dumped.
# ---------------------------------------------------------------------------
mapfile -t RAW_TRACKED_FILES < <(
    git -C "$REPOSITORY_ROOT" ls-files --cached -z 2>/dev/null \
    | tr '\0' '\n' \
    | sort -u
)

is_elided_file() {
    local candidate="$1" entry
    for entry in "${ELIDED_FILES[@]}"; do
        [[ "$candidate" == "$entry" ]] && return 0
    done
    return 1
}

INCLUDED_FILES=()
for candidate_file in "${RAW_TRACKED_FILES[@]}"; do
    [[ -z "$candidate_file" ]] && continue
    # Skip every held-out directory tree, generated and archived alike. Both are
    # absent from the dump; the distinction between them matters to other gates,
    # not to this loop.
    candidate_in_excluded_tree=0
    for excluded_prefix in "${GENERATED_DIRECTORIES[@]}" "${ARCHIVED_DIRECTORIES[@]}"; do
        [[ "$candidate_file" == "${excluded_prefix}/"* ]] && { candidate_in_excluded_tree=1; break; }
    done
    (( candidate_in_excluded_tree )) && continue
    # Skip any individually-excluded file.
    candidate_excluded=0
    for excluded_entry in "${EXCLUDED_FILES[@]}"; do
        if [[ "$excluded_entry" == */* ]]; then
            [[ "$candidate_file" == "$excluded_entry" ]] && { candidate_excluded=1; break; }
        else
            [[ "$candidate_file" == "$excluded_entry" || "$candidate_file" == */"$excluded_entry" ]] && { candidate_excluded=1; break; }
        fi
    done
    (( candidate_excluded )) && continue
    INCLUDED_FILES+=("$candidate_file")
done

FILE_COUNT="${#INCLUDED_FILES[@]}"
if (( FILE_COUNT == 1 )); then                                        # (F-33)
    FILE_COUNT_NOUN="file"
else
    FILE_COUNT_NOUN="files"
fi

# ---------------------------------------------------------------------------
# 4. Helper: human-readable file size — awk instead of bc, which was an
#    unchecked dependency that killed the whole dump under 'set -e' on any
#    machine without it. awk is already required by file_sha256.        (F-30)
# ---------------------------------------------------------------------------
human_size() {
    local bytes="$1"
    if (( bytes < 1024 )); then
        printf '%d B' "$bytes"
        return 0
    fi
    awk -v bytes="$bytes" 'BEGIN {
        unit_count = split("KiB MiB GiB TiB PiB", units, " ")
        value = bytes
        unit_index = 0
        while (value >= 1024 && unit_index < unit_count) {
            value = value / 1024
            unit_index = unit_index + 1
        }
        printf "%.1f %s", value, units[unit_index]
    }'
}

# ---------------------------------------------------------------------------
# 5. Helper: SHA-256 of a file (portable across Linux distros and macOS)
# ---------------------------------------------------------------------------
file_sha256() {
    local path="$1"
    if command -v sha256sum &>/dev/null; then
        sha256sum "$path" | awk '{print $1}'
    elif command -v shasum &>/dev/null; then
        shasum -a 256 "$path" | awk '{print $1}'
    else
        echo 'unavailable'
    fi
}

# ---------------------------------------------------------------------------
# 6. Helper: binary detection. The previous version keyed everything on
#    file(1); on a machine without it, EVERY file was misclassified as
#    binary and the dump contained no content at all. Now the MIME type is
#    the primary signal and a NUL-byte sniff of the first 8 KiB decides
#    whenever file(1) is missing or inconclusive.
# ---------------------------------------------------------------------------
is_binary_file() {
    local path="$1"
    local mime_type
    if command -v file &>/dev/null; then
        mime_type="$(file --brief --mime-type "$path" 2>/dev/null || echo 'unknown')"
        case "$mime_type" in
            text/*|image/svg+xml|application/json|application/xml|application/x-yaml|application/yaml|application/javascript|application/x-shellscript|application/x-empty|inode/x-empty)
                return 1
                ;;
            image/*|audio/*|video/*|font/*|application/octet-stream|application/zip|application/gzip|application/x-tar|application/pdf)
                return 0
                ;;
        esac
    fi
    local nul_byte_count
    nul_byte_count="$(head -c 8192 "$path" 2>/dev/null | tr -dc '\0' | wc -c)"
    (( nul_byte_count > 0 ))
}

# ---------------------------------------------------------------------------
# 8. File tree — tree(1) when available (charset pinned so LC_ALL=C cannot
#    degrade the connectors to ASCII), otherwise a correct pure-bash renderer:
#    └── for the last entry at a level, ├── otherwise, with │ continuation
#    lines only where an ancestor has further siblings.                 (F-31)
# ---------------------------------------------------------------------------
build_file_tree() {
    if (( FILE_COUNT == 0 )); then
        printf '.\n(no files included)\n'
        return 0
    fi
    if command -v tree &>/dev/null; then
        printf '%s\n' "${INCLUDED_FILES[@]}" \
        | tree --fromfile -a --noreport --charset=UTF-8 2>/dev/null \
        || render_tree_fallback
    else
        render_tree_fallback
    fi
}

render_tree_fallback() {
    printf '.\n'
    render_tree_level "" ""
}

# Recursive renderer over the sorted INCLUDED_FILES list. The children of a
# directory are the unique next path components; because the list is sorted
# with C collation, entries sharing a component are adjacent, so consecutive
# deduplication is sufficient.
render_tree_level() {
    local parent_path="$1"
    local prefix="$2"

    local -a child_names=()
    local previous_child=""
    local entry remainder child_name
    for entry in "${INCLUDED_FILES[@]}"; do
        if [[ -n "$parent_path" ]]; then
            [[ "$entry" == "${parent_path}/"* ]] || continue
            remainder="${entry#"${parent_path}"/}"
        else
            remainder="$entry"
        fi
        child_name="${remainder%%/*}"
        if [[ "$child_name" != "$previous_child" ]]; then
            child_names+=("$child_name")
            previous_child="$child_name"
        fi
    done

    local child_count="${#child_names[@]}"
    local child_index child_path connector descendant_prefix child_is_directory
    for (( child_index = 0; child_index < child_count; child_index++ )); do
        child_name="${child_names[$child_index]}"
        if (( child_index == child_count - 1 )); then
            connector='└── '
            descendant_prefix="${prefix}    "
        else
            connector='├── '
            descendant_prefix="${prefix}│   "
        fi
        if [[ -n "$parent_path" ]]; then
            child_path="${parent_path}/${child_name}"
        else
            child_path="$child_name"
        fi
        child_is_directory=0
        for entry in "${INCLUDED_FILES[@]}"; do
            if [[ "$entry" == "${child_path}/"* ]]; then
                child_is_directory=1
                break
            fi
        done
        if (( child_is_directory )); then
            printf '%s%s%s/\n' "$prefix" "$connector" "$child_name"
            render_tree_level "$child_path" "$descendant_prefix"
        else
            printf '%s%s%s\n' "$prefix" "$connector" "$child_name"
        fi
    done
}

# ---------------------------------------------------------------------------
# 9. Per-file metadata and content blocks — shared by the self-documentation
#    section and the main loop, so the two can never drift apart.
# ---------------------------------------------------------------------------
# Three fields, and the nine that were here are gone (F-96).
#
# WHAT A SESSION ACTUALLY USES. The relative path says where the file goes back
# to — which is the whole point of a dump an LLM writes changes against, and the
# reason it is the one field nothing may drop. The size and the SHA-256 are what
# make a reconstruction checkable: a splitter can slice content by declared byte
# count and verify it against the hash, which is how a session proves it read the
# tree rather than approximately read it.
#
# WHAT WAS DROPPED, AND WHY IT IS NOT A LOSS. Absolute path, last modified,
# permissions, owner, inode, hard links, MIME type, last git commit, and the file
# name — the last being the tail of the relative path one line above it, which is
# the same fact written twice in adjacent lines. Nine lines per file, 164 KiB
# across this tree, 2.6% of the dump. None of them has been read in a session;
# four of them (absolute path, owner, inode, host-local mtime) describe the
# authoring machine rather than the repository, so dropping them makes the dump
# both smaller and less about a computer nobody is asking about.
#
# 'file' and 'git log' are no longer called per file, so a dump of this tree
# makes about 700 fewer subprocess calls. That is a side effect, not the reason.
print_file_metadata() {
    local relative_path="$1"
    local file_size="$2"
    local sha256_value="$3"

    printf '\n--- METADATA ---\n'
    printf '  %-22s %s\n' "Relative path:" "$relative_path"
    printf '  %-22s %s\n' "Size:"          "$(human_size "$file_size") (${file_size} bytes)"
    printf '  %-22s %s\n' "SHA-256:"       "$sha256_value"
    printf '\n--- CONTENT ---\n'
}

print_file_content() {
    local absolute_path="$1"
    local file_size="$2"
    local sha256_value="$3"
    local relative_path="${4:-}"

    if [[ -n "$relative_path" ]] && is_elided_file "$relative_path"; then
        printf '[Elided — unmodified boilerplate, %s, SHA-256 above. The bytes are in the tree.]\n' \
            "$(human_size "$file_size")"
        return 0
    fi

    if is_binary_file "$absolute_path"; then
        printf '[Binary file — content omitted. Size: %s, SHA-256: %s]\n' \
            "$(human_size "$file_size")" "$sha256_value"
        return 0
    fi

    cat "$absolute_path"
    # Append a newline only when the file is non-empty and does not already
    # end with one. The previous version compared "$(tail -c1 ...)" against
    # $'\n' — but command substitution strips trailing newlines, so the test
    # always passed and every newline-terminated file gained a spurious blank
    # line. Counting newlines in the final byte avoids that trap.
    if [[ -s "$absolute_path" ]] && (( $(tail -c 1 "$absolute_path" | wc -l) == 0 )); then
        printf '\n'
    fi
}

# ---------------------------------------------------------------------------
# 10. Assemble the complete dump on stdout
# ---------------------------------------------------------------------------
generate_dump() {
    cat <<BANNER
################################################################################
#                                                                              #
#   REPOSITORY CONTEXT DUMP                                                    #
#   Generated for LLM consumption — do not edit manually                       #
#                                                                              #
################################################################################

DUMP METADATA
═════════════════════════════════════════════════════════════════════════════════
  Generated at   : ${TIMESTAMP}
  Generator      : ${SCRIPT_NAME}

REPOSITORY METADATA
═════════════════════════════════════════════════════════════════════════════════
  Repository root: ${REPOSITORY_ROOT}
  Branch         : ${GIT_BRANCH}
  Commit (full)  : ${GIT_COMMIT}
  Commit (short) : ${GIT_COMMIT_SHORT}
  Commit date    : ${GIT_COMMIT_DATE}
  Commit message : ${GIT_COMMIT_MESSAGE}
  Remote origin  : ${GIT_REMOTE}
  Files included : ${FILE_COUNT}
  Withheld paths : ${EXCLUDED_PATHS_DISPLAY}
  Excluded files : ${EXCLUDED_FILES_DISPLAY}
  Elided files   : ${ELIDED_FILES_DISPLAY} (metadata and SHA-256 only)

NOT EVERYTHING TRACKED IS BELOW
═════════════════════════════════════════════════════════════════════════════════
  The withheld paths above hold tracked, authored files that are NOT in this
  dump. docs/progress/ is the earlier half of the build log; it is real, it is
  in git, and it is linked by path from docs/BUILD_PROGRESS.md. Do not
  reconstruct a withheld file from this dump and do not deliver a withheld
  file's sibling as though it were the whole document.

GIT WORKING TREE STATUS (first 20 lines)
═════════════════════════════════════════════════════════════════════════════════
BANNER

    if [[ -n "$GIT_STATUS_SUMMARY" ]]; then
        printf '%s\n' "$GIT_STATUS_SUMMARY"
    else
        echo "  (clean — no uncommitted changes)"
    fi

    # ── Self-documentation: this script, exactly once ──────────────────────
    cat <<SELF_HEADER

################################################################################
# FILE: ${SCRIPT_NAME}  [THIS SCRIPT — included for full context]
################################################################################
SELF_HEADER

    local script_relative_path script_size script_sha256
    script_relative_path="$(realpath --relative-to="$REPOSITORY_ROOT" "$SCRIPT_PATH" 2>/dev/null || echo "$SCRIPT_PATH")"
    script_size="$(wc -c < "$SCRIPT_PATH")"
    script_sha256="$(file_sha256 "$SCRIPT_PATH")"
    print_file_metadata "$script_relative_path" "$script_size" "$script_sha256"
    print_file_content  "$SCRIPT_PATH" "$script_size" "$script_sha256" "$script_relative_path"

    # ── File tree ───────────────────────────────────────────────────────────
    # (F-28) The header no longer prints its own '.' root — tree(1) and the
    # fallback each print exactly one.
    cat <<TREE_HEADER

################################################################################
# FILE TREE  (${FILE_COUNT} included ${FILE_COUNT_NOUN})
################################################################################
TREE_HEADER

    build_file_tree
    echo ""

    # ── Per-file content dump ───────────────────────────────────────────────
    local total_bytes=0
    local relative_path absolute_path file_size sha256_value
    for relative_path in "${INCLUDED_FILES[@]}"; do
        absolute_path="${REPOSITORY_ROOT}/${relative_path}"

        # Skip files that no longer exist on disk (deleted but still indexed).
        [[ -f "$absolute_path" ]] || continue

        file_size="$(wc -c < "$absolute_path" 2>/dev/null || echo 0)"
        total_bytes=$(( total_bytes + file_size ))
        sha256_value="$(file_sha256 "$absolute_path")"

        printf '\n'
        printf '################################################################################\n'
        printf '# FILE: %s\n' "$relative_path"
        printf '################################################################################\n'
        print_file_metadata "$relative_path" "$file_size" "$sha256_value"
        print_file_content  "$absolute_path" "$file_size" "$sha256_value" "$relative_path"
    done

    cat <<FOOTER

################################################################################
# DUMP SUMMARY
################################################################################
  Files dumped   : ${FILE_COUNT}
  Total size     : $(human_size "$total_bytes") (${total_bytes} bytes)
  Output file    : ${OUTPUT_FILE}
  Completed at   : $(iso_timestamp)
################################################################################
# END OF DUMP
################################################################################
FOOTER
}

# ---------------------------------------------------------------------------
# 11. Write atomically, then echo.                                      (F-27)
#     Plain redirection means every byte is on disk when generate_dump
#     returns; rename() then publishes the finished file in a single step;
#     only afterwards is the result printed — from the final file itself, so
#     the console shows exactly what a reader of dump.txt sees. The previous
#     'exec > >(tee ...)' could reach the rename (and exit) while tee was
#     still draining, letting a reader observe a truncated dump, and its
#     '2>&1' folded stray stderr diagnostics into the dump itself.
# ---------------------------------------------------------------------------
mkdir -p "$OUTPUT_DIRECTORY"

TEMPORARY_FILE="$(mktemp "${OUTPUT_DIRECTORY}/.dump.XXXXXX")"
trap 'rm -f "$TEMPORARY_FILE"' EXIT

generate_dump > "$TEMPORARY_FILE"

# mktemp creates the file 0600; the dump is not a secret, so restore the
# conventional 0644 the previous tee-based version produced via the umask.
chmod 644 "$TEMPORARY_FILE"
mv -f "$TEMPORARY_FILE" "$OUTPUT_FILE"
trap - EXIT

cat "$OUTPUT_FILE"
