#!/usr/bin/env python3
"""Unit tests for comment-density.py's DIFF mode -- the added-line measurement.

Issue #4347: the comment-vs-code figure quoted in reviews was being taken four
different ways, and at least once with a two-dot `git diff origin/main..HEAD`
that `no-git-stash-with-worktrees.md` already documents as contaminated for
this purpose. These tests pin the parts a shell one-liner gets wrong.

Three properties matter, and a plausible implementation misses each one:

  1. THREE-DOT, resolved to an explicit merge-base commit. Two-dot against a
     moving `origin/main` counts other people's merges as this branch's work.
  2. PER-HUNK classifier state. Added lines from unrelated hunks are not one
     source file; running one scanner state across them lets an unterminated
     `/*` in an early hunk swallow every later one.
  3. A DELTA between two heads, so a review-driven repair's prose cost is
     separable from the author's.

Run: python3 tools/test_comment_density_diff.py
"""
from __future__ import annotations

import importlib.util
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location(
    "comment_density", os.path.join(HERE, "comment-density.py"))
cd = importlib.util.module_from_spec(_spec)
sys.modules["comment_density"] = cd
_spec.loader.exec_module(cd)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


def git(repo: str, *args: str) -> str:
    return subprocess.run(["git", "-C", repo, *args], capture_output=True,
                          text=True, check=True).stdout.strip()


def write(repo: str, rel: str, text: str) -> None:
    path = os.path.join(repo, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(text)


def commit(repo: str, msg: str) -> str:
    git(repo, "add", "-A")
    git(repo, "commit", "-q", "-m", msg)
    return git(repo, "rev-parse", "HEAD")


def new_repo(tmp: str, name: str) -> str:
    repo = os.path.join(tmp, name)
    os.makedirs(repo)
    git(repo, "init", "-q", "-b", "main")
    git(repo, "config", "user.email", "t@example.com")
    git(repo, "config", "user.name", "T")
    return repo


CODE = "var a = 1;\n"

# ---------------------------------------------------------------------------
# Property 1: three-dot against the merge base, not two-dot against a moved main
# ---------------------------------------------------------------------------
print("three-dot against the merge base")

with tempfile.TemporaryDirectory() as tmp:
    repo = new_repo(tmp, "contaminated")
    write(repo, "AlRunner/A.cs", CODE)
    base = commit(repo, "base")

    # The branch adds one comment line and one code line. That is the true figure.
    git(repo, "checkout", "-q", "-b", "feature")
    write(repo, "AlRunner/A.cs", CODE + "// mine\nvar b = 2;\n")
    head = commit(repo, "feature work")

    # main then moves ahead with SOMEBODY ELSE's work: 5 comment, 20 code, in a
    # file the branch never touched. A separate file is what reproduces #4347's
    # shape -- two-dot then shows that work as this branch REMOVING it, and the
    # figure quoted in the review was the sum over a diff carrying both.
    git(repo, "checkout", "-q", "main")
    write(repo, "AlRunner/Theirs.cs",
          CODE + "// theirs\n" * 5 + "var z = 0;\n" * 20)
    commit(repo, "somebody else's merge")

    r = cd.diff_counts(repo, "main", head, ["AlRunner/"])
    check("the branch's own added lines are counted",
          (r.added.comment, r.added.code) == (1, 1),
          f"got comment={r.added.comment} code={r.added.code}, want 1/1")
    check("the resolved base is the merge base, not the ref as given",
          r.base == git(repo, "merge-base", "main", head), f"got {r.base}")
    check("...and the merge base is not main's current head",
          r.base != git(repo, "rev-parse", "main"), r.base)

    # Control: the two forms really do see different diffs here, so the
    # assertions above discriminate rather than agreeing with both. Two-dot
    # drags in the OTHER branch's file; three-dot does not. Counting changed
    # lines of either sign is what exposes it -- #4347's contaminated +13/+36
    # was a sum over a diff carrying another PR's work in both directions.
    def changed(rng: str) -> tuple[set[str], int]:
        out = subprocess.run(
            ["git", "-C", repo, "diff", "--unified=0", "--no-color", rng,
             "--", "AlRunner/"], capture_output=True, text=True).stdout
        # Read BOTH sides: a file the diff DELETES has `+++ /dev/null`, so a
        # `+++ b/` scan alone misses exactly the contamination being measured.
        files = {l[6:] for l in out.splitlines()
                 if l.startswith("+++ b/") or l.startswith("--- a/")}
        n = len([l for l in out.splitlines()
                 if (l.startswith("+") or l.startswith("-"))
                 and not l.startswith("+++") and not l.startswith("---")])
        return files, n

    two_files, two_n = changed(f"main..{head}")
    three_files, three_n = changed(f"main...{head}")
    check("control: two-dot really covers a different diff",
          two_files != three_files and two_n != three_n,
          f"two-dot {sorted(two_files)} ({two_n} lines) vs three-dot "
          f"{sorted(three_files)} ({three_n} lines); if these matched, this "
          "fixture could not tell the two forms apart")
    check("...specifically, two-dot drags in the other branch's file",
          "AlRunner/Theirs.cs" in two_files
          and "AlRunner/Theirs.cs" not in three_files,
          f"two-dot {sorted(two_files)}, three-dot {sorted(three_files)}")

# ---------------------------------------------------------------------------
# Property 2: classifier state resets per hunk
# ---------------------------------------------------------------------------
print()
print("per-hunk classifier state")

FILLER = "".join(f"var l{i} = {i};\n" for i in range(40))

with tempfile.TemporaryDirectory() as tmp:
    repo = new_repo(tmp, "hunks")
    write(repo, "AlRunner/B.cs", FILLER)
    base = commit(repo, "base")

    lines = [f"var l{i} = {i};\n" for i in range(40)]
    # Hunk 1: a line whose /* sits inside a string literal. A scanner that
    # mishandles it opens a block comment that nothing later closes.
    lines.insert(2, 'var s = "/* not a comment";\n')
    # Hunk 2, far away: plain code, which must stay code.
    lines.insert(30, "var tail = 1;\n")
    write(repo, "AlRunner/B.cs", "".join(lines))
    head = commit(repo, "two hunks")

    r = cd.diff_counts(repo, base, head, ["AlRunner/"])
    check("both added lines classify as code",
          (r.added.comment, r.added.code) == (0, 2),
          f"got comment={r.added.comment} code={r.added.code}, want 0/2")
    check("control: the fixture really produced two separate hunks",
          r.hunks >= 2, f"got {r.hunks} hunk(s)")

with tempfile.TemporaryDirectory() as tmp:
    repo = new_repo(tmp, "hunks2")
    write(repo, "AlRunner/C.cs", FILLER)
    base = commit(repo, "base")

    lines = [f"var l{i} = {i};\n" for i in range(40)]
    # Hunk 1 adds a complete block comment.
    lines.insert(2, "/* opened here\n")
    lines.insert(3, "   still comment */\n")
    # Hunk 2, far away: plain code, which must not be swallowed.
    lines.insert(32, "var tail = 1;\n")
    write(repo, "AlRunner/C.cs", "".join(lines))
    head = commit(repo, "block then code")

    r = cd.diff_counts(repo, base, head, ["AlRunner/"])
    check("a block comment in one hunk does not swallow a later hunk",
          (r.added.comment, r.added.code) == (2, 1),
          f"got comment={r.added.comment} code={r.added.code}, want 2/1")

with tempfile.TemporaryDirectory() as tmp:
    repo = new_repo(tmp, "hunks3")
    write(repo, "AlRunner/C2.cs", FILLER)
    base = commit(repo, "base")

    lines = [f"var l{i} = {i};\n" for i in range(40)]
    # The sharper case: the added lines carry an UNTERMINATED opener. Its
    # closer is an untouched context line, so it never appears in the diff.
    # Per-hunk state must still not leak into the next hunk.
    lines.insert(2, "/* opened and never closed within the diff\n")
    lines.insert(32, "var tail = 1;\n")
    write(repo, "AlRunner/C2.cs", "".join(lines))
    head = commit(repo, "unterminated opener then code")

    r = cd.diff_counts(repo, base, head, ["AlRunner/"])
    check("an unterminated opener does not swallow the next hunk's code",
          r.added.code == 1,
          f"got comment={r.added.comment} code={r.added.code}, want code=1")

# ---------------------------------------------------------------------------
# Property 3: the delta between two heads
# ---------------------------------------------------------------------------
print()
print("delta between two heads")

with tempfile.TemporaryDirectory() as tmp:
    repo = new_repo(tmp, "delta")
    write(repo, "AlRunner/D.cs", CODE)
    base = commit(repo, "base")

    git(repo, "checkout", "-q", "-b", "feature")
    write(repo, "AlRunner/D.cs", CODE + "// claim\nvar b = 2;\n")
    round1 = commit(repo, "author's work")

    # The review-driven repair: prose only, no code. This is #4347's shape.
    write(repo, "AlRunner/D.cs",
          CODE + "// claim\n// narrowed\n// further\nvar b = 2;\n")
    round2 = commit(repo, "review-driven narrowing")

    d = cd.delta_counts(repo, "main", round1, round2, ["AlRunner/"])
    check("round 1 is measured from the merge base",
          (d.before.added.comment, d.before.added.code) == (1, 1),
          f"got {d.before.added.comment}/{d.before.added.code}, want 1/1")
    check("round 2 is measured from the same merge base",
          (d.after.added.comment, d.after.added.code) == (3, 1),
          f"got {d.after.added.comment}/{d.after.added.code}, want 3/1")
    check("the delta isolates the review's prose cost",
          (d.comment, d.code) == (2, 0),
          f"got comment={d.comment} code={d.code}, want 2/0")
    check("both heads resolve to one shared base",
          d.before.base == d.after.base,
          f"{d.before.base} vs {d.after.base}")

# ---------------------------------------------------------------------------
# Path scope
# ---------------------------------------------------------------------------
print()
print("path scope")

with tempfile.TemporaryDirectory() as tmp:
    repo = new_repo(tmp, "scope")
    write(repo, "AlRunner/E.cs", CODE)
    write(repo, "AlRunner.Tests/E.cs", CODE)
    write(repo, "AlRunner/notes.md", "text\n")
    base = commit(repo, "base")

    write(repo, "AlRunner/E.cs", CODE + "// in scope\n")
    write(repo, "AlRunner.Tests/E.cs", CODE + "// out\n// out 2\n")
    write(repo, "AlRunner/notes.md", "text\nmore text\n")
    head = commit(repo, "both trees")

    r = cd.diff_counts(repo, base, head, ["AlRunner/"])
    check("AlRunner.Tests/ is excluded by an AlRunner/ scope",
          (r.added.comment, r.added.code) == (1, 0),
          f"got {r.added.comment}/{r.added.code}, want 1/0")
    check("control: the excluded tree really had lines to exclude",
          cd.diff_counts(repo, base, head,
                         ["AlRunner.Tests/"]).added.comment == 2,
          "the fixture added no comment lines under AlRunner.Tests/")
    check("a non-.cs file in scope is not counted as C#",
          r.files == 1, f"got {r.files} file(s), want 1")

# ---------------------------------------------------------------------------
# Refusals. Every arm is paired with a GREEN control on the honest path, so a
# tool that refused EVERYTHING could not pass this section.
# ---------------------------------------------------------------------------
print()
print("refusals, each with a green control")

with tempfile.TemporaryDirectory() as tmp:
    repo = new_repo(tmp, "refuse")
    write(repo, "AlRunner/F.cs", CODE)
    base = commit(repo, "base")
    write(repo, "AlRunner/F.cs", CODE + "// c\n")
    head = commit(repo, "head")

    try:
        cd.diff_counts(repo, "no-such-ref-xyz", head, ["AlRunner/"])
        check("an unknown base ref refuses", False, "no exception raised")
    except cd.Unmeasurable as exc:
        check("an unknown base ref refuses", True)
        check("...and the message names the ref it could not resolve",
              "no-such-ref-xyz" in str(exc), str(exc))

    try:
        cd.diff_counts(repo, base, "0" * 40, ["AlRunner/"])
        check("an unknown head SHA refuses", False, "no exception raised")
    except cd.Unmeasurable:
        check("an unknown head SHA refuses", True)

    # `git rev-parse` without --verify -q ECHOES an unknown SHA back and exits
    # 128, so a string compare reports success for a commit git never heard of.
    check("an unknown SHA does not resolve to itself",
          cd.resolve_commit(repo, base) != "0" * 40)

    # Two histories with no common ancestor: there is no merge base to use, and
    # silently diffing the whole tree would report a huge invented figure.
    git(repo, "checkout", "-q", "--orphan", "unrelated")
    write(repo, "AlRunner/G.cs", "var g = 1;\n")
    orphan = commit(repo, "orphan")
    try:
        cd.diff_counts(repo, "main", orphan, ["AlRunner/"])
        check("unrelated histories refuse", False, "no exception raised")
    except cd.Unmeasurable as exc:
        check("unrelated histories refuse", True)
        check("...and the message says a merge base is what is missing",
              "merge base" in str(exc).lower(), str(exc))

    git(repo, "checkout", "-q", "main")
    # THE GREEN CONTROL for this whole section. Without it, a tool raising
    # Unmeasurable unconditionally would pass every arm above.
    ok = cd.diff_counts(repo, base, head, ["AlRunner/"])
    check("control: the honest path still produces a number",
          (ok.added.comment, ok.added.code) == (1, 0),
          f"got {ok.added.comment}/{ok.added.code}, want 1/0")

with tempfile.TemporaryDirectory() as tmp:
    repo = new_repo(tmp, "emptyscope")
    write(repo, "AlRunner/H.cs", CODE)
    base = commit(repo, "base")
    write(repo, "AlRunner/H.cs", CODE + "// c\n")
    head = commit(repo, "head")

    # A scope matching nothing is NOT a refusal: an honestly empty diff is a
    # legitimate zero (guards-need-a-third-state.md -- a genuinely absent thing
    # stays a pass). What must not happen is that zero reading as an error.
    empty = cd.diff_counts(repo, base, head, ["NoSuchTree/"])
    check("a scope matching nothing reports zero rather than refusing",
          (empty.added.comment, empty.added.code, empty.files) == (0, 0, 0),
          f"got {empty.added.comment}/{empty.added.code}/{empty.files}")

    # And the matching control: the same call over a scope that DOES match is
    # non-zero, so the zero above is about the scope rather than about the tool
    # having stopped counting.
    check("control: the same heads over a matching scope are non-zero",
          cd.diff_counts(repo, base, head, ["AlRunner/"]).added.comment == 1,
          "the matching scope reported zero too")

# ---------------------------------------------------------------------------
# The tool reports; it does not judge.
# ---------------------------------------------------------------------------
print()
print("the tool reports, it does not judge")
# #4347 declines to set a threshold: "A ratio gate would be a policy decision
# nobody has made." Pinned so a later pass adding one has to argue for it.
src = open(os.path.join(HERE, "comment-density.py"), encoding="utf-8").read()
check("diff mode exposes no pass/fail threshold flag",
      "--max-ratio" not in src and "--fail-over" not in src,
      "a threshold flag appeared; #4347 says that is a policy decision")

print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
