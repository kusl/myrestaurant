#!/usr/bin/env bash
#
# export.sh — repository context dump for LLM consumption, at docs/llm/dump.txt.
#
#   bash export.sh
#
# Dumps only git-tracked files, excluding itself; its source appears once, in the
# self-documentation section. Three kinds of path are held out, and the kinds are distinct
# because other gates care about the difference:
#
#   GENERATED_DIRECTORIES  tool output, never authored. scripts/check_tree.sh skips these too.
#   ARCHIVED_DIRECTORIES   authored history, withheld to keep the dump small. Still tracked, still
#                          hygiene-checked. A session cannot see these, so every archived document
#                          must be linked by path from a document the dump does contain.
#   ELIDED_FILES           metadata and SHA-256 only, body replaced by one line.
#
# Emits per-file relative path, size and SHA-256 — deliberately no absolute path, mtime,
# permissions, owner or last commit. Builds into a temporary file and renames atomically, so a
# reader can never observe a partially written dump.
#

set -euo pipefail
IFS=$'\n\t'

export LC_ALL=C

SCRIPT_PATH="$(readlink -f "${BASH_SOURCE[0]}")"
SCRIPT_DIRECTORY="$(dirname "$SCRIPT_PATH")"
SCRIPT_NAME="$(basename "$SCRIPT_PATH")"

if ! git -C "$SCRIPT_DIRECTORY" rev-parse --is-inside-work-tree &>/dev/null; then
    exit 0
fi
if ! git -C "$SCRIPT_DIRECTORY" status --porcelain &>/dev/null; then
    exit 0
fi
REPOSITORY_ROOT="$(git -C "$SCRIPT_DIRECTORY" rev-parse --show-toplevel 2>/dev/null)" || exit 0

GENERATED_DIRECTORIES=("docs/llm")

ARCHIVED_DIRECTORIES=("docs/progress")

ELIDED_FILES=("LICENSE")

EXCLUDED_FILES=("$SCRIPT_NAME")
EXCLUDED_FILES_DISPLAY="$(printf '%s, ' "${EXCLUDED_FILES[@]}")"
EXCLUDED_FILES_DISPLAY="${EXCLUDED_FILES_DISPLAY%, }"

EXCLUDED_PATHS_DISPLAY="$(printf '%s/, ' "${GENERATED_DIRECTORIES[@]}" "${ARCHIVED_DIRECTORIES[@]}")"
EXCLUDED_PATHS_DISPLAY="${EXCLUDED_PATHS_DISPLAY%, }"

ELIDED_FILES_DISPLAY="$(printf '%s, ' "${ELIDED_FILES[@]}")"
ELIDED_FILES_DISPLAY="${ELIDED_FILES_DISPLAY%, }"

OUTPUT_DIRECTORY="${REPOSITORY_ROOT}/${GENERATED_DIRECTORIES[0]}"
OUTPUT_FILE="${OUTPUT_DIRECTORY}/dump.txt"

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
    candidate_in_excluded_tree=0
    for excluded_prefix in "${GENERATED_DIRECTORIES[@]}" "${ARCHIVED_DIRECTORIES[@]}"; do
        [[ "$candidate_file" == "${excluded_prefix}/"* ]] && { candidate_in_excluded_tree=1; break; }
    done
    (( candidate_in_excluded_tree )) && continue
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
if (( FILE_COUNT == 1 )); then
    FILE_COUNT_NOUN="file"
else
    FILE_COUNT_NOUN="files"
fi

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
    if [[ -s "$absolute_path" ]] && (( $(tail -c 1 "$absolute_path" | wc -l) == 0 )); then
        printf '\n'
    fi
}

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

    cat <<TREE_HEADER

################################################################################
# FILE TREE  (${FILE_COUNT} included ${FILE_COUNT_NOUN})
################################################################################
TREE_HEADER

    build_file_tree
    echo ""

    local total_bytes=0
    local relative_path absolute_path file_size sha256_value
    for relative_path in "${INCLUDED_FILES[@]}"; do
        absolute_path="${REPOSITORY_ROOT}/${relative_path}"

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

mkdir -p "$OUTPUT_DIRECTORY"

TEMPORARY_FILE="$(mktemp "${OUTPUT_DIRECTORY}/.dump.XXXXXX")"
trap 'rm -f "$TEMPORARY_FILE"' EXIT

generate_dump > "$TEMPORARY_FILE"

chmod 644 "$TEMPORARY_FILE"
mv -f "$TEMPORARY_FILE" "$OUTPUT_FILE"
trap - EXIT

cat "$OUTPUT_FILE"
