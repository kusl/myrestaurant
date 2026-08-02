#!/usr/bin/env python3
"""Rewrite `Assert.Single(xs.Where(p))` as `Assert.Single(xs, p)` (xUnit2031).

Conservative by construction: it only touches a call whose *entire* single argument is
`<receiver>.Where(<lambda>)`. Anything else — `Assert.Single(xs)`, an already-correct
`Assert.Single(xs, p)`, `Assert.Single(xs.Where(p).ToList())` — is left alone, so the
script is idempotent and safe to run twice.

Usage:  python3 fix_assert_single.py FILE [FILE...] [--expect N] [--dry-run]
"""
import argparse
import sys

OPEN = "([{"
CLOSE = ")]}"


def skip_atom(text, i):
    """If a literal or comment starts at `i`, return the index just past it; else None."""
    n = len(text)
    c = text[i]

    if c == "/" and i + 1 < n and text[i + 1] == "/":
        j = text.find("\n", i)
        return n if j < 0 else j

    if c == "/" and i + 1 < n and text[i + 1] == "*":
        j = text.find("*/", i + 2)
        return n if j < 0 else j + 2

    # Raw string literal, optionally interpolated: $$"""...""" / $"""...""" / """..."""
    k = i
    while k < n and text[k] == "$":
        k += 1
    if text.startswith('"""', k):
        q = k
        while q < n and text[q] == '"':
            q += 1
        fence = '"' * (q - k)
        j = text.find(fence, q)
        return n if j < 0 else j + len(fence)

    # Verbatim string, optionally interpolated: @"..." / $@"..." / @$"..."
    k = i
    while k < n and text[k] in "$@":
        k += 1
    if k > i and k < n and text[k] == '"' and "@" in text[i:k]:
        j = k + 1
        while j < n:
            if text[j] == '"':
                if j + 1 < n and text[j + 1] == '"':
                    j += 2
                    continue
                return j + 1
            j += 1
        return n

    # Regular or interpolated string: "..." / $"..."
    k = i
    while k < n and text[k] == "$":
        k += 1
    if k < n and text[k] == '"':
        j = k + 1
        while j < n:
            if text[j] == "\\":
                j += 2
                continue
            if text[j] == '"':
                return j + 1
            j += 1
        return n

    if c == "'":
        j = i + 1
        while j < n:
            if text[j] == "\\":
                j += 2
                continue
            if text[j] == "'":
                return j + 1
            j += 1
        return n

    return None


def matching_close(text, start):
    """Index just past the bracket matching the one at `start`, or None."""
    depth = 0
    i = start
    n = len(text)
    while i < n:
        step = skip_atom(text, i)
        if step is not None and step > i:
            i = step
            continue
        c = text[i]
        if c in OPEN:
            depth += 1
        elif c in CLOSE:
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1
    return None


def top_level_where(argument):
    """Index of a depth-0 `.Where(` in `argument`, or None."""
    depth = 0
    i = 0
    n = len(argument)
    while i < n:
        step = skip_atom(argument, i)
        if step is not None and step > i:
            i = step
            continue
        c = argument[i]
        if c in OPEN:
            depth += 1
        elif c in CLOSE:
            depth -= 1
        elif depth == 0 and argument.startswith(".Where(", i):
            return i
        i += 1
    return None


def rewrite(text):
    """Return (new_text, [(before, after), ...])."""
    changes = []
    out = []
    i = 0
    last = 0
    n = len(text)
    needle = "Assert.Single("

    while i < n:
        # A call spelled inside a comment or a string literal is text, not code.
        step = skip_atom(text, i)
        if step is not None and step > i:
            i = step
            continue

        if not text.startswith(needle, i):
            i += 1
            continue

        j = i
        open_paren = j + len(needle) - 1
        end = matching_close(text, open_paren)
        if end is None:
            break

        argument = text[open_paren + 1 : end - 1]
        w = top_level_where(argument)
        if w is None:
            i = end
            continue

        receiver_raw = argument[:w]
        where_call = argument[w:]                       # `.Where( ... )`
        inner_open = where_call.index("(")
        inner_end = matching_close(where_call, inner_open)
        if inner_end is None or inner_end != len(where_call):
            # `.Where(...)` is not the whole tail (`.Where(p).ToList()`); leave it alone.
            i = end
            continue

        predicate = where_call[inner_open + 1 : inner_end - 1].strip()
        receiver = receiver_raw.rstrip()
        gap = receiver_raw[len(receiver) :]              # whitespace that preceded `.Where`
        separator = gap if "\n" in gap else " "
        if not receiver or not predicate:
            i = end
            continue

        before = text[j:end]
        after = f"{needle}{receiver},{separator}{predicate})"
        out.append(text[last:j])
        out.append(after)
        changes.append((before, after))
        last = end
        i = end

    out.append(text[last:])
    return "".join(out), changes


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("files", nargs="+")
    parser.add_argument("--expect", type=int, default=None,
                        help="report a mismatch unless exactly this many sites are rewritten")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    total = 0
    for path in args.files:
        with open(path, "r", encoding="utf-8") as handle:
            original = handle.read()
        updated, changes = rewrite(original)
        total += len(changes)
        for before, after in changes:
            print(f"--- {path}")
            print("  - " + before.replace("\n", "\n    "))
            print("  + " + after.replace("\n", "\n    "))
        if changes and not args.dry_run:
            with open(path, "w", encoding="utf-8") as handle:
                handle.write(updated)

    suffix = " (dry run, nothing written)" if args.dry_run else ""
    print(f"\n{total} site(s) rewritten{suffix}.")
    if args.expect is not None and total != args.expect:
        print(f"EXPECTED {args.expect}, got {total}. Review the output above.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
