#!/usr/bin/env python3
"""Unit tests for tools/corpus-pin.py.

The claim under test is a DIVERGENCE between three readings of the same thing, so
most of this suite builds REAL git repositories with a real submodule and really
moves the shared submodule checkout off the pin. A hand-written fixture cannot
prove it: the whole reason the misreading survives is that every command involved
exits 0 and prints a plausible SHA, which a fake runner would reproduce whether
the tool were right or wrong.

The parsing helpers are tested against captured strings, because `git ls-tree`
output for a path that has STOPPED being a submodule is not a state you can
easily construct on demand, and it is the one where returning a SHA anyway would
be actively wrong.

Run: python3 tools/test_corpus_pin.py
"""
from __future__ import annotations

import importlib.util
import io
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("corpus_pin",
                                               os.path.join(HERE, "corpus-pin.py"))
cp = importlib.util.module_from_spec(_spec)
sys.modules["corpus_pin"] = cp
_spec.loader.exec_module(cp)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


# ------------------------------------------------------------------ parsing
# `git ls-tree` prints one line per entry. A submodule is mode 160000, type
# `commit`. Every other shape must read as "no pin" rather than as a pin -- a
# blob SHA returned here would be a confidently wrong answer of exactly the kind
# this tool exists to prevent.
check("a submodule gitlink line yields its SHA",
      cp.parse_gitlink(
          "160000 commit af01bbbc176b4cc5ca3d7020094103911af51a35\ttests/al-language")
      == "af01bbbc176b4cc5ca3d7020094103911af51a35")

check("a blob line is not a pin",
      cp.parse_gitlink("100644 blob e69de29bb2d1d6434b8b29ae775ad8c2e48c5391\tREADME.md")
      is None)

check("a tree line is not a pin",
      cp.parse_gitlink("040000 tree 4b825dc642cb6eb9a060e54bf8d69288fbee4904\ttests")
      is None)

check("an empty line is not a pin", cp.parse_gitlink("") is None)
check("a truncated line is not a pin", cp.parse_gitlink("160000 commit") is None)

# The mode and the type are BOTH checked. A line carrying one but not the other
# is malformed, and reading a SHA out of it would be inventing one.
check("mode 160000 with the wrong type is not a pin",
      cp.parse_gitlink("160000 blob abc123\ttests/al-language") is None)
check("type commit with the wrong mode is not a pin",
      cp.parse_gitlink("100644 commit abc123\ttests/al-language") is None)


# ------------------------------------------------------------ real git fixture
def git(root, *args, check_rc=True):
    p = subprocess.run(["git", "-C", root, *args], capture_output=True, text=True)
    if check_rc and p.returncode != 0:
        raise AssertionError(f"git {' '.join(args)} failed in {root}: {p.stderr}")
    return p.stdout.strip()


def _init(root):
    subprocess.run(["git", "init", "-q", "-b", "main", root], check=True,
                   capture_output=True)
    git(root, "config", "user.email", "t@example.invalid")
    git(root, "config", "user.name", "T")
    git(root, "config", "commit.gpgsign", "false")


def build_fixture(tmp):
    """A superproject with a real `tests/al-language` submodule at TWO commits.

    Returns (super_root, corpus_root, pin_sha, later_sha). The submodule is
    committed while checked out at `pin_sha`, so the pin recorded in the
    superproject's tree IS pin_sha -- and the test then moves the shared
    submodule working directory to later_sha WITHOUT touching the pin, which is
    precisely the state the tool must catch.
    """
    corpus = os.path.join(tmp, "corpus")
    os.makedirs(corpus)
    _init(corpus)
    with open(os.path.join(corpus, "a.txt"), "w") as fh:
        fh.write("one\n")
    git(corpus, "add", "a.txt")
    git(corpus, "commit", "-qm", "corpus commit one")
    pin_sha = git(corpus, "rev-parse", "HEAD")
    with open(os.path.join(corpus, "b.txt"), "w") as fh:
        fh.write("two\n")
    git(corpus, "add", "b.txt")
    git(corpus, "commit", "-qm", "corpus commit two")
    later_sha = git(corpus, "rev-parse", "HEAD")
    # Back to the commit the superproject will pin, so `submodule add` records it.
    git(corpus, "checkout", "-q", pin_sha)

    sup = os.path.join(tmp, "super")
    os.makedirs(sup)
    _init(sup)
    with open(os.path.join(sup, "README.md"), "w") as fh:
        fh.write("super\n")
    git(sup, "add", "README.md")
    git(sup, "commit", "-qm", "initial")
    git(sup, "-c", "protocol.file.allow=always", "submodule", "add", "-q",
        corpus, "tests/al-language")
    git(sup, "commit", "-qm", "add corpus submodule")
    return sup, corpus, pin_sha, later_sha


def run_tool(argv, cwd_root):
    """main() with stdout captured, returning (exit code, stdout)."""
    buf = io.StringIO()
    old = sys.stdout
    sys.stdout = buf
    try:
        rc = cp.main(argv + ["--root", cwd_root])
    finally:
        sys.stdout = old
    return rc, buf.getvalue()


with tempfile.TemporaryDirectory() as tmp:
    sup, corpus, pin_sha, later_sha = build_fixture(tmp)
    sub = os.path.join(sup, "tests/al-language")

    # ---------------------------------------------------------------- GREEN
    # Everything agrees: the tool reports the pin and exits 0.
    rc, out = run_tool(["--rev", "HEAD"], sup)
    check("agreeing readings exit 0", rc == 0, f"rc={rc}")
    check("an agreeing report says so",
          "is at the commit this checkout expects" in out, out)
    check("an agreeing report does not cry hazard", "HAZARD" not in out, out)

    rc, out = run_tool(["--rev", "HEAD", "--quiet"], sup)
    check("--quiet prints exactly the pin",
          out.strip() == pin_sha, f"{out.strip()!r} != {pin_sha!r}")

    check("read_pin reads the tree, and gets the pin",
          cp.read_pin(cp._default_runner, sup, "HEAD") == pin_sha)

    # ------------------------------------------------------------------ RED
    # THE REAL SHAPE. Move the shared submodule working directory off the pin,
    # exactly as another process on this box would, and change NOTHING else.
    git(sub, "checkout", "-q", later_sha)

    check("the working directory now reads a different commit than the pin",
          cp.read_worktree_head(cp._default_runner, sup) == later_sha
          and cp.read_pin(cp._default_runner, sup, "HEAD") == pin_sha,
          "the fixture did not actually diverge")

    # This is the misreading the issue is about, reproduced: the two commands a
    # person would type give different answers, and both succeed.
    check("the two plausible reads genuinely disagree", pin_sha != later_sha)

    rc, out = run_tool(["--rev", "HEAD"], sup)
    check("a diverged shared checkout exits 1", rc == 1, f"rc={rc}")
    check("the divergence is named as a hazard", "HAZARD" in out, out)
    check("the report shows the wrong commit that is checked out",
          later_sha[:8] in out, out)
    check("the report shows the pin alongside it", pin_sha[:8] in out, out)
    check("the report says which one is not the pin", "NOT THE PIN" in out, out)
    check("the report explains the sharing, not just the mismatch",
          "shared by EVERY worktree" in out, out)

    # --quiet must still answer with the PIN, never the poisoned checkout. A
    # caller that ignores the exit code is the likeliest caller, so the value it
    # captures has to be right on its own.
    rc, out = run_tool(["--rev", "HEAD", "--quiet"], sup)
    check("--quiet still exits 1 when diverged", rc == 1, f"rc={rc}")
    check("--quiet prints the PIN, not the poisoned checkout",
          out.strip() == pin_sha, f"{out.strip()!r} != pin {pin_sha!r}")
    check("--quiet never prints the working-directory commit",
          later_sha not in out, out)

    # ------------------------------------------------- back to agreement again
    # Both directions, on the same fixture: restore the checkout and the verdict
    # must flip back. A guard that only ever fires is not a guard.
    git(sub, "checkout", "-q", pin_sha)
    rc, out = run_tool(["--rev", "HEAD"], sup)
    check("restoring the checkout returns to exit 0", rc == 0, f"rc={rc}")
    check("and the hazard is gone", "HAZARD" not in out, out)

    # ------------------------------------------------------- a bump in progress
    # A branch that is legitimately bumping the pin has moved BOTH its gitlink and
    # its submodule checkout to the new commit. That is correct work, and flagging
    # it would train the reader to ignore this tool.
    git(sub, "checkout", "-q", later_sha)
    git(sup, "add", "tests/al-language")
    rc, out = run_tool(["--rev", "HEAD"], sup)
    check("a staged bump whose checkout matches it is not a hazard",
          rc == 0, f"rc={rc}: {out}")
    check("a staged bump is reported as a bump in progress",
          "bump in progress" in out, out)
    rc, out = run_tool(["--rev", "HEAD", "--quiet"], sup)
    check("--quiet during a bump answers the STAGED pin",
          out.strip() == later_sha, f"{out.strip()!r} != {later_sha!r}")

    # ...but a staged bump whose checkout drifted off it AGAIN is still a hazard.
    git(sub, "checkout", "-q", pin_sha)
    rc, out = run_tool(["--rev", "HEAD"], sup)
    check("a staged bump with a drifted checkout is still a hazard",
          rc == 1, f"rc={rc}: {out}")

    # ------------------------------------ BEHIND is not MID-BUMP (coordinator, #3404)
    # The index records a gitlink whether or not anybody staged one, so "differs
    # from origin/main" does NOT mean "a bump in progress". The common case for an
    # agent worktree is the opposite: a checkout some commits behind, whose index
    # pin equals its own HEAD's tree pin because nothing was staged at all.
    #
    # Getting this wrong is not cosmetic. --quiet preferred the index pin, so on
    # any behind checkout it printed a STALE pin while its own --help promised the
    # right one -- the exact silent-wrong-answer class this tool exists to abolish,
    # in the property documented as its safety guarantee. Measured on the live box:
    # --quiet answered 9ee6bbcd with origin/main's pin at af01bbbc, 45 commits back.
    git(sup, "reset", "-q", "HEAD", "--")
    git(sub, "checkout", "-q", pin_sha)

    # Advance origin/main's pin WITHOUT touching this checkout: the superproject
    # moves on, our HEAD does not. Nothing is staged here -- this is "behind".
    git(sup, "branch", "-f", "other", "HEAD")
    git(sub, "checkout", "-q", later_sha)
    git(sup, "add", "tests/al-language")
    git(sup, "commit", "-qm", "bump the corpus pin on main")
    advanced = git(sup, "rev-parse", "HEAD")
    git(sup, "update-ref", "refs/remotes/origin/main", advanced)
    # Put the checkout back where it was: HEAD behind origin/main, nothing staged.
    git(sup, "reset", "-q", "--hard", "other")
    git(sub, "checkout", "-q", pin_sha)

    check("the fixture is genuinely BEHIND, with nothing staged",
          git(sup, "diff", "--cached", "--name-only", "--", "tests/al-language") == ""
          and cp.read_pin(cp._default_runner, sup, "refs/remotes/origin/main") == later_sha
          and cp.read_pin(cp._default_runner, sup, "HEAD") == pin_sha,
          "the fixture does not reproduce a behind checkout")

    check("an UNSTAGED index pin is not read as a staged bump",
          cp.read_index_pin(cp._default_runner, sup) is None,
          f"got {cp.read_index_pin(cp._default_runner, sup)!r} - nothing was staged")

    rc, out = run_tool(["--rev", "refs/remotes/origin/main", "--quiet"], sup)
    check("--quiet on a BEHIND checkout answers origin/main's pin, not the stale one",
          out.strip() == later_sha,
          f"{out.strip()!r} != origin/main pin {later_sha!r} (stale was {pin_sha!r})")
    check("--quiet on a behind checkout does not print the stale pin",
          pin_sha not in out, out)

    rc, out = run_tool(["--rev", "refs/remotes/origin/main"], sup)
    check("a behind checkout is never labelled as a bump in progress",
          "(a bump in progress)" not in out, out)
    check("...and is not shown a STAGED-pin row it does not have",
          "STAGED in this index" not in out, out)
    check("...and says it is behind, so the reader knows why the pins differ",
          "behind" in out.lower(), out)
    # The submodule sits at this checkout's own recorded pin, which is a perfectly
    # ordinary state for a behind worktree. Calling that a poisoned checkout would
    # fire on every worktree that has not been refreshed.
    check("a behind checkout whose submodule matches its own HEAD is not a hazard",
          rc == 0, f"rc={rc}: {out}")

    # Restore the fixture for the gap tests below.
    git(sup, "update-ref", "-d", "refs/remotes/origin/main")
    git(sup, "branch", "-D", "other")

    # ---------------------------------------------------------------- the gap
    git(sup, "reset", "-q", "HEAD", "--")     # unstage the bump
    git(sub, "checkout", "-q", pin_sha)
    # `origin/master` does not exist in the fixture submodule, so the gap must
    # report that it could NOT measure -- never 0. "the run printed nothing to
    # read" and "the gap is zero" are different facts.
    gap, why = cp.measure_gap(cp._default_runner, sup, pin_sha)
    check("a gap with no origin/master is unmeasurable, not zero",
          gap is None and "origin/master" in why, f"gap={gap} why={why}")

    # Make one, and the count must be exact -- 1 commit, the shape the issue's
    # incident got wrong as 17.
    git(sub, "remote", "add", "up", corpus, check_rc=False)
    git(sub, "fetch", "-q", "up", f"{later_sha}:refs/remotes/origin/master")
    gap, why = cp.measure_gap(cp._default_runner, sup, pin_sha)
    check("the gap from the pin is counted exactly", gap == 1, f"gap={gap} why={why}")

    # And the gap is measured from the PIN, so poisoning the checkout cannot
    # change it. This is the actual defect: 1 commit read as 17.
    git(sub, "checkout", "-q", later_sha)
    gap_poisoned, _ = cp.measure_gap(cp._default_runner, sup, pin_sha)
    check("a poisoned checkout does not change the measured gap",
          gap_poisoned == 1, f"{gap_poisoned} != 1")
    # ...whereas the read the issue warns about would have said 0 from here.
    wrong = git(sub, "rev-list", "--count", "HEAD..refs/remotes/origin/master")
    check("the wrong read really does give a different number",
          wrong != "1", f"the fixture does not demonstrate the defect (wrong={wrong})")

# ------------------------------------------ an UNPOPULATED submodule directory
# `git worktree add` does not populate submodules, so this is the state of every
# fresh worktree -- and `git -C <empty dir> rev-parse HEAD` walks UP and returns
# the SUPERPROJECT's commit with exit 0. Reporting that as a divergence would fire
# the guard in every new worktree, which is how a guard gets ignored. Caught by
# running the tool for real rather than by reading it.
with tempfile.TemporaryDirectory() as tmp:
    sup = os.path.join(tmp, "super")
    os.makedirs(sup)
    _init(sup)
    with open(os.path.join(sup, "R"), "w") as fh:
        fh.write("x\n")
    git(sup, "add", "R")
    git(sup, "commit", "-qm", "init")
    # The directory exists and is EMPTY, exactly as in a fresh worktree.
    os.makedirs(os.path.join(sup, "tests/al-language"))
    sup_head = git(sup, "rev-parse", "HEAD")

    raw = git(os.path.join(sup, "tests/al-language"), "rev-parse", "HEAD")
    check("git really does answer the SUPERPROJECT commit from an empty submodule dir",
          raw == sup_head, f"{raw!r} vs {sup_head!r} - the trap no longer reproduces")
    check("an unpopulated submodule reads as absent, NOT as the superproject commit",
          cp.read_worktree_head(cp._default_runner, sup) is None,
          f"got {cp.read_worktree_head(cp._default_runner, sup)!r}")


# ------------------------------------------------------- not a repository at all
with tempfile.TemporaryDirectory() as tmp:
    # Exit 2 -- "could not read" -- and never 0 or 1, which are verdicts.
    empty = os.path.join(tmp, "plain")
    os.makedirs(empty)
    _init(empty)
    with open(os.path.join(empty, "f"), "w") as fh:
        fh.write("x\n")
    git(empty, "add", "f")
    git(empty, "commit", "-qm", "no submodule here")
    buf, old = io.StringIO(), sys.stderr
    sys.stderr = buf
    try:
        rc = cp.main(["--rev", "HEAD", "--root", empty])
    finally:
        sys.stderr = old
    check("a repository with no corpus submodule exits 2, not a verdict",
          rc == 2, f"rc={rc}")
    check("and says there is no pin to read",
          "no corpus pin" in buf.getvalue() or "gitlink" in buf.getvalue(),
          buf.getvalue())

print()
if FAILURES:
    print(f"{len(FAILURES)} FAILED: {', '.join(FAILURES)}")
    sys.exit(1)
print("all corpus-pin tests passed")
