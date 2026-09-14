#!/usr/bin/env python3
"""Execute `no-git-stash-with-worktrees.md`'s two-dot diff recipe against the defect it claims to detect (#3955).

This is the pinning test `tools/test_rule_recipes_executed.py` requires for that
rule's inline recipe, and the worked example of what pinning means: not "the
command parses" -- the wrong one parsed fine -- but **reproduce the failure the
recipe claims to catch, then confirm the recipe catches it and the rejected
alternative does not.**

## What the rule claims

`.claude/rules/no-git-stash-with-worktrees.md`, section "`origin/main` is a LOCAL
ref with a remote-looking name":

    **`git diff --stat origin/main...HEAD` does NOT catch it -- use two dots.**
    ... three-dot `1 file changed, 1 insertion(+)`; two-dot `2 files changed,
    1 insertion(+), 50 deletions(-)`.

PR #3954 shipped the three-dot form as the remedy. A reviewer built four scratch
repositories and found it does not detect the defect. This suite re-derives that
in the repository, so the next edit to that line is measured rather than believed.

## The ordering matters, and most orderings do not discriminate

Three independent choices produce eight orderings: where the branch was built
(`base` or `ahead` of the other PR), what `reset --soft` targeted, and what
`origin/main` held when the diff was read. Measured, all eight:

    built reset read  | three-dot                  | two-dot
    base  base  base  | 1 file,  1 ins             | 1 file,  1 ins
    base  base  ahead | 1 file,  1 ins             | 2 files, 1 ins, 50 del   <-- THE ONE
    base  ahead base  | 1 file,  1 ins             | 1 file,  1 ins
    base  ahead ahead | 2 files, 1 ins, 50 del     | 2 files, 1 ins, 50 del
    ahead base  base  | 2 files, 51 ins            | 2 files, 51 ins
    ahead base  ahead | 2 files, 51 ins            | 1 file,  1 ins
    ahead ahead base  | 2 files, 51 ins            | 2 files, 51 ins
    ahead ahead ahead | 1 file,  1 ins             | 1 file,  1 ins

Only row 2 is the rule's claim. **Six of the eight orderings show the two forms
agreeing**, so a test that picked an ordering without checking would have
reported the recipe fine whichever dots it used -- passing for the wrong reason,
which `tdd.md` calls noise. That is why this suite asserts the whole matrix and
not just the one row: the discriminating ordering is pinned as discriminating,
and the non-discriminating ones are pinned as non-discriminating, so a future
git whose behaviour drifts in either direction fails here.

Row 6 is worth its line: there the **three-dot** form is the misleading one in
the opposite direction, inflating a one-line change to 51 insertions. The rule
does not mention it and does not need to -- but it means "three dots is always
wrong" would be as unfounded as "three dots is fine".

Run: python3 tools/test_stale_origin_main_diff_recipe.py
"""
from __future__ import annotations

import os
import re
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)

from scratch_git_config import isolate  # noqa: E402

isolate()

RULE = os.path.join(ROOT, ".claude", "rules", "no-git-stash-with-worktrees.md")

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


def git(repo: str, *args: str) -> str:
    out = subprocess.run(
        ["git", "-C", repo, "-c", "user.email=a@b", "-c", "user.name=A", *args],
        capture_output=True, text=True)
    if out.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} failed: {out.stderr.strip()}")
    return out.stdout


def build(tmp: str, built: str, reset: str, read: str) -> tuple[str, str]:
    """Return (three_dot_stat, two_dot_stat) for one ordering."""
    up = os.path.join(tmp, "up")
    work = os.path.join(tmp, "work")
    os.makedirs(up)
    subprocess.run(["git", "init", "-q", "--initial-branch=main", up], check=True)
    with open(os.path.join(up, "f.md"), "w") as fh:
        fh.write("base\n")
    git(up, "add", "-A")
    git(up, "commit", "-qm", "base")
    subprocess.run(["git", "clone", "-q", up, work], check=True)
    base = git(work, "rev-parse", "HEAD").strip()

    # another pull request lands 50 lines upstream
    with open(os.path.join(up, "other.md"), "w") as fh:
        fh.write("".join(f"l{i}\n" for i in range(1, 51)))
    git(up, "add", "-A")
    git(up, "commit", "-qm", "other PR: 50 lines")
    git(work, "fetch", "-q", "origin")
    ahead = git(work, "rev-parse", "origin/main").strip()

    ref = {"base": base, "ahead": ahead}
    git(work, "checkout", "-q", "-B", "mybranch", ref[built])
    with open(os.path.join(work, "f.md"), "a") as fh:
        fh.write("my line\n")
    git(work, "add", "-A")
    git(work, "commit", "-qm", "my docs change")

    # the incident: reset --soft to a ref that is not what you think it is
    git(work, "reset", "-q", "--soft", ref[reset])
    git(work, "commit", "-qm", "rewritten: docs change")

    # what origin/main holds at the moment the diff is read
    git(work, "update-ref", "refs/remotes/origin/main", ref[read])

    three = git(work, "diff", "--stat", "origin/main...HEAD").strip().splitlines()
    two = git(work, "diff", "--stat", "origin/main..HEAD").strip().splitlines()
    return (three[-1].strip() if three else "no change",
            two[-1].strip() if two else "no change")


def parse(stat: str) -> tuple[int, int, int]:
    """(files, insertions, deletions) from a --stat summary line."""
    def n(pat: str) -> int:
        m = re.search(pat, stat)
        return int(m.group(1)) if m else 0
    return (n(r"(\d+) files? changed"), n(r"(\d+) insertions?"), n(r"(\d+) deletions?"))


# built, reset, read -> (three_dot, two_dot) as (files, ins, del)
EXPECTED = {
    ("base", "base", "base"):   ((1, 1, 0), (1, 1, 0)),
    ("base", "base", "ahead"):  ((1, 1, 0), (2, 1, 50)),   # the rule's claim
    ("base", "ahead", "base"):  ((1, 1, 0), (1, 1, 0)),
    ("base", "ahead", "ahead"): ((2, 1, 50), (2, 1, 50)),
    ("ahead", "base", "base"):  ((2, 51, 0), (2, 51, 0)),
    ("ahead", "base", "ahead"): ((2, 51, 0), (1, 1, 0)),
    ("ahead", "ahead", "base"): ((2, 51, 0), (2, 51, 0)),
    ("ahead", "ahead", "ahead"): ((1, 1, 0), (1, 1, 0)),
}

DISCRIMINATING = ("base", "base", "ahead")


def main() -> int:
    if shutil.which("git") is None:
        # guards-need-a-third-state.md: unmeasurable is not the success state.
        print("  UNMEASURABLE: git is not on PATH; this suite cannot run here")
        return 3

    print("executing the recipe against the defect it claims to detect")
    measured: dict[tuple[str, str, str], tuple[str, str]] = {}
    for key in EXPECTED:
        tmp = tempfile.mkdtemp(prefix="stale-ref-recipe-")
        try:
            measured[key] = build(tmp, *key)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    for key, (want3, want2) in EXPECTED.items():
        got3, got2 = measured[key]
        label = "/".join(key)
        check(f"three-dot on {label}", parse(got3) == want3, f"want {want3} got {got3!r}")
        check(f"two-dot on {label}", parse(got2) == want2, f"want {want2} got {got2!r}")

    # The claim the rule actually makes, asserted as a claim rather than as two numbers.
    three, two = measured[DISCRIMINATING]
    f3, i3, d3 = parse(three)
    f2, i2, d2 = parse(two)
    check("on the discriminating ordering the three-dot form reports no deletions",
          d3 == 0, f"three-dot said {three!r}")
    check("on the discriminating ordering the two-dot form reports the lost content",
          d2 == 50, f"two-dot said {two!r}")
    check("so the two forms disagree there, which is what makes the recipe worth stating",
          (f3, i3, d3) != (f2, i2, d2), f"{three!r} vs {two!r}")

    # Six of eight agree: the ordering was chosen, not stumbled into.
    agreeing = sum(1 for k in EXPECTED if parse(measured[k][0]) == parse(measured[k][1]))
    check("six of the eight orderings do not discriminate between the two forms",
          agreeing == 6, f"{agreeing} agreed")

    # The rule still says what this suite measured. Drift in either direction is a
    # failure: a rule edited to three dots, or a suite left pinning a deleted claim.
    # Match on the claim, never on layout: an earlier cut of this check spanned a
    # line wrap ("...HEAD**, two\ndots") and failed on unedited prose -- the same
    # brittleness this whole suite exists to catch, one level up.
    text = open(RULE, encoding="utf-8").read()
    flat = re.sub(r"\s+", " ", text)
    check("the rule still tells the reader to use two dots",
          "git diff --stat origin/main..HEAD" in text
          and re.search(r"origin/main\.\.HEAD`\*\*,? two dots", flat) is not None,
          "the two-dot remedy is no longer the rule's stated remedy")
    check("the rule still names the three-dot form as the one that does not catch it",
          re.search(r"`git diff --stat origin/main\.\.\.HEAD` does NOT catch it", text)
          is not None,
          "the rejected form is no longer named")

    if FAILURES:
        print(f"\nFAILED: {len(FAILURES)} check(s)")
        return 1
    print(f"\nall {len(EXPECTED) * 2 + 6} recipe-execution checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
