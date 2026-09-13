#!/usr/bin/env python3
"""Every DETECTION recipe in `.claude/rules/` is pinned by a test that runs it (#3955).

A docs-only pull request skips the BC matrix, so nothing has ever executed the
commands a rule instructs an agent to run. PR #3954 shipped
`git diff --stat origin/main...HEAD` as the remedy for a stale-`origin/main`
soft reset; three dots does not detect that defect and two dots does. The rule
was written from a correct memory of a real incident, by the person who caused
it, and its one executable line was wrong.

## Why a static sweep does not help, and this guard is not one

The coordinator's own sweep on #3955 `bash -n`-checked all 34 command lines then
in `.claude/rules/`: 30 valid, 4 "invalid" for `<placeholder>` angle brackets,
which is correct documentation style. The line that shipped wrong was **perfectly
valid bash**. It parses, runs, exits 0 and prints a plausible answer -- it just
answers a different question than the prose claims. Nothing static catches that,
because nothing about three-dot-versus-two-dot is static.

What would have caught it is `tdd.md`'s mutation discipline applied to prose:
reproduce the failure the recipe claims to detect, then confirm the recipe
detects it. That is a fixture and an assertion -- a test. So this guard does not
execute recipes itself. It enforces the census that makes such a test exist:

    a DETECTION recipe is pinned by a named test, or it is not a detection
    recipe, and the guard says which for every recipe in the tree.

## The third state is the whole design (`guards-need-a-third-state.md`)

Of the ~44 recipe lines in `.claude/rules/`, most are `gh` calls against live
GitHub state. Executing those here is impossible, and a guard that quietly
counted them as passing would be the defect one level up. They are not silently
skipped: a rule declares them with a reason, and this guard reports the count.

  * pinned        -- a `Recipe-pinned-by:` marker names a test that runs it
  * unpinnable    -- a `Recipe-unpinned:` marker gives the reason it cannot run
  * UNCLASSIFIED  -- neither. This fails, and is the only failing state.

`Recipe-unpinned:` is deliberately cheap to write, because the alternative to a
cheap honest "this reads live GitHub state" is an author who silently omits the
marker. What it buys is that the reason is *written down and countable*: a sweep
can ask how many recipes claim to be unexecutable, and a reviewer can disagree
with any one of them.

## What counts as a DETECTION recipe

Only a recipe the prose claims will *detect*, *catch*, *confirm* or *tell you
whether* something -- the class where being subtly wrong is invisible. A recipe
that merely performs an action (`gh pr edit --add-label`, `git push`) has no
truth value to get wrong: it either does the thing or errors. Scoping to
detection is what keeps this from being ceremony on 44 lines to protect 3.

The classifier is deliberately keyed on the block's own marker, not on prose
sentiment, so a rule author declares intent rather than having it guessed.

## Inline recipes count, and the founding one was inline

A first cut of this guard read fenced blocks only, and **did not see the recipe
that produced #3955**: `git diff --stat origin/main...HEAD` sits in
`no-git-stash-with-worktrees.md` in inline backticks, inside a bold sentence, not
in a fence. A guard that missed its own founding instance is one that would have
reported a clean census while the defect sat two files away. So a backticked
command span inside a detection sentence is a recipe here too, and carries a
marker on the paragraph's own line.

Run: python3 tools/test_rule_recipes_executed.py
"""
from __future__ import annotations

import glob
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RULES_DIR = os.path.join(ROOT, ".claude", "rules")

FAILURES: list[str] = []

# A marker sits on its own line immediately above or below a fenced block, as an
# HTML comment so it renders as nothing. Value together with the marker, one line,
# mirroring the `Corpus-PR:` shape check_corpus_linkage.sh accepts (#3255).
PINNED = re.compile(r"^<!--\s*Recipe-pinned-by:\s*(\S+)\s*-->\s*$")
UNPINNED = re.compile(r"^<!--\s*Recipe-unpinned:\s*(.+?)\s*-->\s*$")

PLACEHOLDER_REASONS = {"n/a", "na", "none", "tbd", "todo", "-", "?", "unknown"}


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


def fenced_blocks(text: str) -> list[tuple[int, int, str]]:
    """(start_line, end_line, body) for every fenced block, 0-based line numbers."""
    lines = text.splitlines()
    out: list[tuple[int, int, str]] = []
    i = 0
    while i < len(lines):
        if lines[i].startswith("```"):
            j = i + 1
            while j < len(lines) and not lines[j].startswith("```"):
                j += 1
            out.append((i, min(j, len(lines) - 1), "\n".join(lines[i + 1:j])))
            i = j + 1
        else:
            i += 1
    return out


def marker_near(lines: list[str], start: int, end: int) -> tuple[str, str] | None:
    """A marker on the line above the opening fence, or below the closing fence."""
    for idx in (start - 1, end + 1):
        if 0 <= idx < len(lines):
            s = lines[idx].strip()
            m = PINNED.match(s)
            if m:
                return ("pinned", m.group(1))
            m = UNPINNED.match(s)
            if m:
                return ("unpinned", m.group(1))
    return None


# Prose immediately around a block that claims the block DETECTS something. Used
# only to report an unmarked detection recipe; a marker always wins over this.
DETECTION_WORDS = re.compile(
    r"\b(detect|catch(?:es)?|confirm(?:s)?|check(?:s)? (?:whether|that)|"
    r"tells? you whether|answers? (?:the )?question|is the check|reads the verdict)\b",
    re.I,
)


# An inline command span: `git ...` / `gh ...` / `tools/x.py ...` in backticks, with
# at least one argument, so a bare `git` or a symbol name in backticks is not a recipe.
INLINE_CMD = re.compile(r"`((?:git|gh|tools/\S+|python3?|command grep|rg)\s+[^`]{4,})`")

# A sentence asserting that the inline command DOES or DOES NOT detect the defect.
# This is the shape of the line #3955 was filed about: the claim and the command in
# one sentence, where the claim is checkable and nothing checks it.
INLINE_CLAIM = re.compile(
    r"\b(does not (?:catch|detect)|catches|detects|use two dots|"
    r"is what (?:catches|detects)|read \*\*|before believing)\b", re.I)


def inline_recipes(lines: list[str], fenced_spans: list[tuple[int, int]]) -> list[int]:
    """Line numbers of paragraphs carrying a detection CLAIM about an inline command."""
    inside = set()
    for s, e in fenced_spans:
        inside.update(range(s, e + 1))
    out = []
    for i, line in enumerate(lines):
        if i in inside:
            continue
        if INLINE_CMD.search(line) and INLINE_CLAIM.search(line):
            out.append(i)
    return out


def main() -> int:
    rule_files = sorted(glob.glob(os.path.join(RULES_DIR, "*.md")))
    check("the rules glob matched files at all", len(rule_files) > 10, str(len(rule_files)))

    pinned: list[tuple[str, str]] = []
    unpinned: list[tuple[str, str]] = []
    unclassified: list[str] = []
    bad_reason: list[str] = []
    missing_test: list[str] = []
    blocks_seen = 0

    for path in rule_files:
        rel = os.path.relpath(path, ROOT).replace(os.sep, "/")
        text = open(path, encoding="utf-8").read()
        lines = text.splitlines()
        blocks = fenced_blocks(text)

        for i in inline_recipes(lines, [(s, e) for s, e, _ in blocks]):
            blocks_seen += 1
            where = f"{rel}:{i + 1}"
            # An inline recipe's marker sits on its own line anywhere in the
            # paragraph that carries it: scan to the nearest blank line each way.
            mark = None
            lo = i
            while lo > 0 and lines[lo - 1].strip():
                lo -= 1
            hi = i
            while hi + 1 < len(lines) and lines[hi + 1].strip():
                hi += 1
            for idx in range(max(0, lo - 1), min(len(lines), hi + 2)):
                s = lines[idx].strip()
                m = PINNED.match(s)
                if m:
                    mark = ("pinned", m.group(1))
                    break
                m = UNPINNED.match(s)
                if m:
                    mark = ("unpinned", m.group(1))
                    break
            if mark is None:
                unclassified.append(f"{where} (inline command carrying a detection claim)")
            elif mark[0] == "pinned":
                pinned.append((where, mark[1]))
                if not os.path.exists(os.path.join(ROOT, mark[1])):
                    missing_test.append(f"{where} -> {mark[1]} does not exist")
            else:
                unpinned.append((where, mark[1]))
                if (mark[1].strip().lower().rstrip(".") in PLACEHOLDER_REASONS
                        or len(mark[1]) < 12):
                    bad_reason.append(f"{where} -> {mark[1]!r}")

        for start, end, body in blocks:
            lang = lines[start][3:].strip().lower()
            if lang not in ("bash", "sh", "shell", ""):
                continue
            # A block with no command line in it is an output sample, not a recipe.
            cmds = [ln for ln in body.splitlines()
                    if ln.strip() and not ln.strip().startswith("#")]
            if not cmds:
                continue
            blocks_seen += 1
            where = f"{rel}:{start + 1}"
            mark = marker_near(lines, start, end)
            if mark is None:
                context = "\n".join(lines[max(0, start - 6):start])
                if DETECTION_WORDS.search(context):
                    unclassified.append(f"{where} (prose claims it detects something)")
                continue
            kind, value = mark
            if kind == "pinned":
                pinned.append((where, value))
                if not os.path.exists(os.path.join(ROOT, value)):
                    missing_test.append(f"{where} -> {value} does not exist")
            else:
                unpinned.append((where, value))
                if value.strip().lower().rstrip(".") in PLACEHOLDER_REASONS or len(value) < 12:
                    bad_reason.append(f"{where} -> {value!r}")

    check("recipe blocks were found at all", blocks_seen > 20, f"{blocks_seen} blocks")

    check("every marked-as-pinned recipe names a test file that exists",
          not missing_test, str(missing_test))
    check("every marked-as-unpinnable recipe gives a real reason",
          not bad_reason, str(bad_reason))
    check("no detection recipe is unclassified",
          not unclassified,
          "\n      " + "\n      ".join(unclassified) if unclassified else "")

    # A pinned recipe's test must actually reference the rule it pins, so a marker
    # cannot point at an unrelated suite and look like coverage (tdd.md: a test that
    # names the thing is not a test that drives it -- the weaker half is caught here,
    # the stronger half only by that test's own mutation).
    orphaned = []
    for where, test_rel in pinned:
        p = os.path.join(ROOT, test_rel)
        if not os.path.exists(p):
            continue
        rule_name = os.path.basename(where.split(":")[0])
        if rule_name not in open(p, encoding="utf-8").read():
            orphaned.append(f"{where} -> {test_rel} never mentions {rule_name}")
    check("every pinning test mentions the rule it pins", not orphaned, str(orphaned))

    print(f"\n  census: {len(pinned)} pinned, {len(unpinned)} declared unpinnable, "
          f"{blocks_seen} recipe blocks total")
    for where, value in pinned:
        print(f"    pinned      {where} -> {value}")
    for where, value in unpinned:
        print(f"    unpinnable  {where} -> {value}")

    if FAILURES:
        print(f"\nFAILED: {len(FAILURES)} check(s): {FAILURES}")
        return 1
    print("\nall rule-recipe census checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
