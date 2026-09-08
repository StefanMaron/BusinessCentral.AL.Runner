#!/usr/bin/env python3
"""What is the corpus pin, and is the shared submodule checkout lying about it?

Issue #3404. There are two plausible ways to ask "which corpus commit are we on",
they disagree, and the wrong one looks entirely correct:

    git ls-tree origin/main tests/al-language     # THE PIN -- what CI replays
    git -C tests/al-language rev-parse HEAD       # whatever a process left behind

`tests/al-language/` is a submodule working directory, and a submodule working
directory is shared by every worktree of the repository -- exactly like
`refs/stash` (`no-git-stash-with-worktrees.md`). Any process that checks out a
different corpus commit inside it leaves it there for every other worktree. It
has no owner and nothing resets it.

WHY A TOOL AND NOT ANOTHER PARAGRAPH
------------------------------------
The rule was already written down in `.claude/rules/al-language-submodule.md`,
and on 2026-09-07 it was violated three times in one session by three different
actors -- once producing a third value belonging to neither end. Prose has had
its turn. What was missing is a single obvious call, so that "read the pin" stops
being a choice between two commands that both run, both exit 0, and both print a
real SHA.

Measured in the repository root while this was written, all three live at once:

    origin/main pin        af01bbbc      what CI actually replays
    index (staged) pin     9ee6bbcd      this checkout's own gitlink
    shared working dir     17b015ef      left by some earlier process

WHY IT DOES NOT ANNOUNCE ITSELF
-------------------------------
  * `git status` corroborates the wrong read: a stale checkout shows
    `M tests/al-language`, which reads as an ordinary dirty submodule.
  * `git -C tests/al-language log` works fine and prints a plausible history,
    because it IS a real repository at a real commit -- just not the pinned one.
  * The commit list is genuine. Nothing is malformed. It answers a question
    nobody asked.

The failure is a confident OVERESTIMATE of remaining work, which is the worse
direction: it justifies a large measurement run and invites bisecting a range
whose predecessors are already pinned.

WHAT THIS REPORTS
-----------------
All three readings, always, labelled -- never one number that the caller has to
trust. The divergence between them is the finding, so collapsing them to a single
answer would discard it.

EXIT CODES
----------
  0  the readings agree, or they differ only in ways that are not a hazard (see
     --explain). The pin is on stdout in --quiet form.
  1  the shared submodule working directory is NOT at the pin. Anything measured
     from it -- a gap, a commit range, a diff -- is about the wrong commit.
  2  the pin could not be read (not a git repository, no gitlink, no origin/main).
     NEVER a verdict about the pin.

Run:
  tools/corpus-pin.py                  # report all three readings
  tools/corpus-pin.py --quiet          # just the pin, for $(...) in a script
  tools/corpus-pin.py --gap            # ...and how far it is behind corpus master
"""
from __future__ import annotations

import argparse
import os
import subprocess
import sys

SUBMODULE_PATH = "tests/al-language"
CORPUS_REMOTE = "https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests"

# The corpus default branch is `master`, not `main` (`al-language-submodule.md`).
# Naming the wrong one is its own documented time sink, so it is a constant here
# rather than something a caller passes.
CORPUS_DEFAULT_BRANCH = "master"

EXIT_MEANING = {
    0: "the shared submodule checkout agrees with the pin (or is absent)",
    1: "the shared submodule checkout is NOT at the pin - measurements from it are wrong",
    2: "the pin could not be read - never a verdict about the pin",
}


class PinError(Exception):
    """The pin could not be read. Never a verdict about the pin."""


def _default_runner(args: list[str], cwd: str | None = None):
    return subprocess.run(args, cwd=cwd, capture_output=True, text=True)


def git(runner, root: str | None, *args: str) -> tuple[int, str]:
    """(exit code, stripped stdout). Never raises on a non-zero git."""
    cmd = ["git"]
    if root:
        cmd += ["-C", root]
    p = runner(cmd + list(args))
    return p.returncode, (p.stdout or "").strip()


def parse_gitlink(ls_tree_line: str) -> str | None:
    """The submodule SHA out of a `git ls-tree` line, or None.

    A submodule entry is mode 160000, object type `commit`. Checking BOTH is what
    keeps this from returning a blob or tree SHA when the path stops being a
    submodule -- a structural change to the repository, which this tool has no
    basis for a verdict on, so it must read as "no pin" rather than as a pin.
    """
    parts = ls_tree_line.split()
    if len(parts) < 3:
        return None
    mode, kind, sha = parts[0], parts[1], parts[2]
    if mode != "160000" or kind != "commit":
        return None
    return sha or None


def read_pin(runner, root: str, rev: str) -> str | None:
    """The corpus pin recorded in `rev`'s TREE. Independent of what is checked out."""
    rc, out = git(runner, root, "ls-tree", rev, SUBMODULE_PATH)
    if rc != 0 or not out:
        return None
    return parse_gitlink(out.splitlines()[0])


def read_index_pin(runner, root: str) -> str | None:
    """The gitlink staged in THIS checkout's index.

    Distinct from both other readings, and the one that is easiest to forget
    exists. A worktree mid-bump legitimately differs from origin/main here; that
    is a pin bump in progress, not a hazard.
    """
    rc, out = git(runner, root, "ls-files", "-s", SUBMODULE_PATH)
    if rc != 0 or not out:
        return None
    parts = out.split()
    # `ls-files -s` is: mode sha stage<TAB>path
    if len(parts) < 2 or parts[0] != "160000":
        return None
    return parts[1] or None


def read_worktree_head(runner, root: str) -> str | None:
    """Whatever the last process left checked out in the SHARED submodule directory.

    Not the pin. Reported so a caller can SEE the divergence, never so it can be
    used as the pin.

    An UNPOPULATED submodule directory must read as None, not as a commit. `git
    worktree add` does not populate submodules, so the directory exists and is
    empty in every fresh worktree -- and `git -C <empty dir> rev-parse HEAD` then
    walks UP to the superproject and cheerfully returns the SUPERPROJECT's commit,
    exit 0. Measured: a new worktree at 4e4abbcb reported `4e4abbcb` as the
    "corpus checkout", which is not a corpus commit at all. Reporting that as a
    divergence would fire this guard in every fresh worktree, and a guard that
    cries wolf by default is one nobody reads. `--show-toplevel` is what tells the
    two apart: for a real submodule it is the submodule; for an empty directory it
    is the superproject.
    """
    sub = os.path.join(root, SUBMODULE_PATH)
    if not os.path.isdir(sub):
        return None
    rc, top = git(runner, sub, "rev-parse", "--show-toplevel")
    if rc != 0 or not top:
        return None
    if os.path.realpath(top) != os.path.realpath(sub):
        return None
    rc, out = git(runner, sub, "rev-parse", "HEAD")
    if rc != 0 or not out:
        return None
    return out or None


def short(sha: str | None) -> str:
    return (sha or "")[:8] or "<none>"


class Readings:
    """The three readings, kept apart on purpose."""

    def __init__(self, pin: str | None, index: str | None, worktree: str | None,
                 pin_rev: str):
        self.pin = pin
        self.index = index
        self.worktree = worktree
        self.pin_rev = pin_rev

    @property
    def worktree_diverged(self) -> bool:
        """The hazard: the shared directory is present and NOT at the pin.

        Compared against the INDEX pin when there is one, because a branch that is
        mid-bump has legitimately moved its own gitlink and its submodule checkout
        together -- flagging that would fire on correct work and train the reader
        to ignore this. Absent an index pin, origin/main's is the reference.
        """
        if self.worktree is None:
            return False
        reference = self.index or self.pin
        if reference is None:
            return False
        return self.worktree != reference

    @property
    def index_ahead_of_main(self) -> bool:
        """This checkout stages a different pin than origin/main - a bump in progress."""
        return (self.index is not None and self.pin is not None
                and self.index != self.pin)


def gather(runner, root: str, pin_rev: str) -> Readings:
    pin = read_pin(runner, root, pin_rev)
    index = read_index_pin(runner, root)
    worktree = read_worktree_head(runner, root)
    if pin is None and index is None:
        raise PinError(
            f"no {SUBMODULE_PATH} gitlink in {pin_rev}'s tree or in the index of {root}. "
            f"Either this is not the AL Runner repository, or {pin_rev} does not exist "
            f"here -- run `git fetch origin main` first. There is no corpus pin to read.")
    return Readings(pin, index, worktree, pin_rev)


def render(r: Readings) -> list[str]:
    label_w = max(24, len(f"pin on {r.pin_rev}"))
    L = [f"corpus pin ({SUBMODULE_PATH})", ""]
    L.append(f"  {f'pin on {r.pin_rev}':<{label_w}}  {short(r.pin)}   "
             f"<- THE PIN: what CI replays")
    L.append(f"  {'pin staged in this index':<{label_w}}  {short(r.index)}   "
             f"{'(a bump in progress)' if r.index_ahead_of_main else ''}".rstrip())
    if r.worktree is None:
        L.append(f"  {'shared working directory':<{label_w}}  <absent>   "
                 f"(submodule not initialised here)")
    else:
        flag = "   <- NOT THE PIN" if r.worktree_diverged else ""
        L.append(f"  {'shared working directory':<{label_w}}  {short(r.worktree)}{flag}")
    L.append("")
    if not r.worktree_diverged:
        L.append("The shared submodule checkout agrees with the pin. Anything measured from "
                 "it is about the pinned commit.")
        return L
    reference = "the pin staged in this index" if r.index else f"the pin on {r.pin_rev}"
    L.append(f"HAZARD: {SUBMODULE_PATH} is checked out at {short(r.worktree)}, which is not "
             f"{reference} ({short(r.index or r.pin)}).")
    L.append("")
    L.append("A submodule working directory is shared by EVERY worktree of this repository, "
             "like refs/stash. Some other process left this one here; it has no owner and "
             "nothing resets it. Any commit range, gap count or diff you measure from it is "
             "about the wrong commit -- and it will look right, because `git status` reads it "
             "as an ordinary dirty submodule and `git log` inside it prints a real history.")
    L.append("")
    L.append("Read the pin from the tree instead:")
    L.append(f"    PIN=$(tools/corpus-pin.py --quiet)")
    L.append(f"    # or: git ls-tree {r.pin_rev} {SUBMODULE_PATH} | awk '{{print $3}}'")
    L.append("")
    L.append("To MEASURE against the corpus, give your worktree its own clone rather than "
             "checking out inside the shared one -- a checkout there is the very act that "
             "poisons it for everyone else:")
    L.append(f"    git clone {CORPUS_REMOTE} /tmp/corpus && \\")
    L.append(f"      git -C /tmp/corpus rev-list --count \"$PIN..origin/{CORPUS_DEFAULT_BRANCH}\"")
    return L


def measure_gap(runner, root: str, pin: str) -> tuple[int | None, str]:
    """Commits between the pin and the corpus default branch, or (None, why).

    Uses the shared submodule directory as a git OBJECT STORE only -- `rev-list`
    between two explicit revisions never consults HEAD, so a poisoned checkout
    cannot skew this. It can still lack the objects, which is a "could not
    measure", not a gap of zero.
    """
    sub = os.path.join(root, SUBMODULE_PATH)
    if not os.path.isdir(sub):
        return None, f"{SUBMODULE_PATH} is not initialised here"
    rc, _ = git(runner, sub, "cat-file", "-e", f"{pin}^{{commit}}")
    if rc != 0:
        return None, (f"the pin {short(pin)} is not an object in {SUBMODULE_PATH} "
                      f"(run `git -C {SUBMODULE_PATH} fetch`)")
    ref = f"origin/{CORPUS_DEFAULT_BRANCH}"
    rc, _ = git(runner, sub, "rev-parse", "--verify", "--quiet", ref)
    if rc != 0:
        return None, f"no {ref} in {SUBMODULE_PATH} (run `git -C {SUBMODULE_PATH} fetch`)"
    rc, out = git(runner, sub, "rev-list", "--count", f"{pin}..{ref}")
    if rc != 0 or not out.isdigit():
        return None, f"could not count {pin}..{ref}"
    return int(out), ""


def repo_root(runner, start: str) -> str | None:
    rc, out = git(runner, start, "rev-parse", "--show-toplevel")
    return out if rc == 0 and out else None


def main(argv: list[str] | None = None, runner=None) -> int:
    runner = runner or _default_runner
    epilog = "exit codes:\n" + "\n".join(f"  {k}  {v}" for k, v in sorted(EXIT_MEANING.items()))
    ap = argparse.ArgumentParser(
        description=__doc__.split("\n\n")[0],
        epilog=epilog, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--quiet", action="store_true",
                    help="print only the pin SHA, for capture in a script. Still exits 1 "
                         "when the shared checkout has diverged, so a caller that ignores "
                         "the exit code still gets the RIGHT pin rather than a wrong one")
    ap.add_argument("--gap", action="store_true",
                    help=f"also report how many commits the pin is behind the corpus "
                         f"{CORPUS_DEFAULT_BRANCH}")
    ap.add_argument("--rev", default="origin/main",
                    help="the superproject revision whose tree carries the pin "
                         "(default: origin/main)")
    ap.add_argument("--root", default=None,
                    help="repository root (default: discovered from this script)")
    args = ap.parse_args(argv)

    start = args.root or os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    root = repo_root(runner, start) if not args.root else args.root
    if not root:
        print("corpus-pin: not inside a git repository", file=sys.stderr)
        return 2

    try:
        r = gather(runner, root, args.rev)
    except PinError as e:
        print(f"corpus-pin: {e}", file=sys.stderr)
        return 2

    # The pin a caller should USE is the one this checkout stages when it has one
    # -- a worktree mid-bump is working against its own pin, not origin/main's --
    # falling back to origin/main's tree otherwise.
    effective = r.index or r.pin

    if args.quiet:
        print(effective)
    else:
        for line in render(r):
            print(line)

    if args.gap and effective:
        gap, why = measure_gap(runner, root, effective)
        if not args.quiet:
            print()
        if gap is None:
            print(f"gap: could not measure - {why}", file=sys.stderr)
        else:
            print(f"gap: {gap} commit(s) behind origin/{CORPUS_DEFAULT_BRANCH}"
                  if not args.quiet else str(gap))

    return 1 if r.worktree_diverged else 0


if __name__ == "__main__":
    sys.exit(main())
