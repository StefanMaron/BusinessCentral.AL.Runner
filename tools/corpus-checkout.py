#!/usr/bin/env python3
"""Put the corpus in `tests/al-language/`, and say which commit that is (#3737).

The corpus is no longer a pinned gitlink. A run RESOLVES it -- at the head of
`master`, or at the head of a corpus pull request -- and the SHA it resolved is
the evidence for what that run measured. This is the local half of that; CI's
half is `.github/actions/checkout-corpus`, and both print the same one line:

    corpus: 1a2b3c4d5e6f... (master)

WHY THE SHA IS PRINTED AND NOT JUST RECORDED
--------------------------------------------
With a pin, "which corpus did this measure" was answerable from the tree
afterwards. With a moving corpus it is answerable only from the run, so a run
that does not name its corpus has produced a verdict nobody can attribute. The
ref alone is not enough either: `master` moves, and a corpus pull request's
branch head moves while the pull request keeps its number.

WHAT REPLACED THE SHARED SUBMODULE CHECKOUT
-------------------------------------------
`tests/al-language/` used to be a submodule working directory, which git shares
across every worktree of this repository -- like `refs/stash`
(`no-git-stash-with-worktrees.md`). A checkout inside it by one worktree moved it
for all of them, and nothing reset it (#3404). It is now an ordinary, ignored
directory: each worktree gets its own clone, so there is nothing left to share
and nothing left to poison.

That leaves one migration state this refuses rather than repairs: a checkout made
before the pin was dropped still has a `.git` FILE pointing into the superproject's
`.git/modules/`, which is exactly the shared thing. Deleting it here would delete
it for every other worktree using that gitdir, including live ones -- so this says
what to run and stops (`guards-need-a-third-state.md`).

EXIT CODES
----------
  0  the corpus is checked out and its SHA printed
  3  could not resolve it -- a fetch that failed, a ref that does not exist, or
     the pre-#3737 submodule checkout above. NEVER a silent fall back to master:
     "you asked for a corpus pull request and got master" is the one wrong answer
     that looks exactly like a right one.

Run:
  tools/corpus-checkout.py                        # master
  tools/corpus-checkout.py --corpus-pr 293        # that corpus pull request's head
  tools/corpus-checkout.py --ref refs/tags/x      # any ref the corpus remote has
  tools/corpus-checkout.py --print                # no network: what is checked out now
  tools/corpus-checkout.py --quiet                # just the SHA, for $(...)
"""
from __future__ import annotations

import argparse
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    import agent_stdio as _stdio
except Exception:  # pragma: no cover - a copy detached from its sibling module
    _stdio = None
if _stdio is not None:
    _stdio.enable_utf8_stdio()

CORPUS_PATH = "tests/al-language"
CORPUS_REMOTE = "https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests.git"

# The corpus default branch is `master`, not `main` (`al-language-submodule.md`).
# Naming the wrong one is its own documented time sink, so it is a constant here
# rather than something a caller passes.
CORPUS_DEFAULT_BRANCH = "master"

EXIT_MEANING = {
    0: "the corpus is checked out at the printed SHA",
    3: "could not resolve the corpus - never a silent fall back to master",
}


class CorpusError(Exception):
    """Could not resolve the corpus. Carries the remedy, not just the failure."""


def _default_runner(args: list[str], cwd: str | None = None):
    # UTF-8, never the locale codec (#3434): git writes commit subjects as UTF-8.
    return subprocess.run(args, cwd=cwd, capture_output=True, text=True,
                          encoding="utf-8", errors="replace")


def git(runner, cwd: str | None, *args: str) -> tuple[int, str, str]:
    """(exit code, stdout, stderr), all stripped. Never raises on a non-zero git."""
    cmd = ["git"]
    if cwd:
        cmd += ["-C", cwd]
    p = runner(cmd + list(args))
    return p.returncode, (p.stdout or "").strip(), (p.stderr or "").strip()


def ref_for(corpus_pr: int | None, ref: str | None) -> str:
    """The remote ref to fetch. Exactly one of the two inputs, or neither."""
    if corpus_pr is not None and ref:
        raise CorpusError("--corpus-pr and --ref both name a corpus to fetch, and a "
                          "run measures exactly one. Pass one of them.")
    if corpus_pr is not None:
        # `refs/pull/<N>/head` is the pull request's own branch head, not the merge
        # commit GitHub computes: the merge ref would measure the corpus PR merged
        # into whatever master is at that instant, which is a commit nobody has
        # reviewed and which changes under the run.
        return f"refs/pull/{corpus_pr}/head"
    return ref or CORPUS_DEFAULT_BRANCH


def checkout_state(path: str) -> str:
    """`clone` / `fetch` / `submodule-leftover` / `not-a-repo`, for `path`."""
    if not os.path.isdir(path):
        return "clone"
    dot_git = os.path.join(path, ".git")
    if os.path.isdir(dot_git):
        return "fetch"
    if os.path.isfile(dot_git):
        return "submodule-leftover"
    if os.listdir(path):
        return "not-a-repo"
    return "clone"


def resolve(runner, root: str, ref: str, path: str = CORPUS_PATH) -> str:
    """Fetch `ref` into `<root>/<path>` and check it out. Returns the full SHA."""
    full = os.path.join(root, path)
    state = checkout_state(full)

    if state == "submodule-leftover":
        raise CorpusError(
            f"{path}/.git is a FILE, so this directory is still the pre-#3737 submodule "
            f"checkout, whose git directory lives in the superproject and is SHARED by "
            f"every worktree of this repository. Removing it here would remove it for all "
            f"of them, including live ones, so this refuses rather than doing it for you:\n"
            f"    rm -rf {path} && tools/corpus-checkout.py\n"
            f"Nothing is lost -- the corpus is read-only here and re-cloned from the remote.")

    if state == "not-a-repo":
        raise CorpusError(
            f"{path} exists, is not empty, and is not a git repository. Refusing to fetch "
            f"into it: whatever is in there is not the corpus, and this cannot tell a "
            f"half-finished clone from something you put there on purpose.\n"
            f"    rm -rf {path} && tools/corpus-checkout.py")

    if state == "clone":
        os.makedirs(full, exist_ok=True)
        rc, _, err = git(runner, full, "init", "--quiet")
        if rc != 0:
            raise CorpusError(f"could not `git init` {path}: {err}")
        rc, _, err = git(runner, full, "remote", "add", "origin", CORPUS_REMOTE)
        if rc != 0:
            raise CorpusError(f"could not add the corpus remote to {path}: {err}")

    # --depth 1: nothing here needs corpus history any more. The guard that did --
    # check_corpus_pin_forward.sh, which compared two pins -- went with the pin, and
    # a shallow clone was the state in which its merge-base lied (#3288).
    rc, _, err = git(runner, full, "fetch", "--depth", "1", "--force", "origin", ref)
    if rc != 0:
        raise CorpusError(
            f"could not fetch `{ref}` from the corpus remote: {err or 'git said nothing'}\n"
            f"A corpus pull request that was never pushed, a closed one whose branch was "
            f"deleted, and an unreachable network all land here. None of them is a reason "
            f"to measure master instead -- that would report a green run for a corpus "
            f"nobody asked for.")

    rc, _, err = git(runner, full, "-c", "advice.detachedHead=false",
                     "checkout", "--detach", "--force", "FETCH_HEAD")
    if rc != 0:
        raise CorpusError(f"fetched `{ref}` but could not check it out: {err}")

    rc, sha, err = git(runner, full, "rev-parse", "HEAD")
    if rc != 0 or len(sha) != 40:
        raise CorpusError(f"checked `{ref}` out but could not read its SHA: {err or sha}")
    return sha


def read_current(runner, root: str, path: str = CORPUS_PATH) -> str:
    """The SHA already checked out, without touching the network."""
    full = os.path.join(root, path)
    state = checkout_state(full)
    if state != "fetch":
        raise CorpusError(
            f"{path} is not a corpus clone ({state}). Run `tools/corpus-checkout.py` "
            f"to make one; there is no pin to fall back on since #3737.")
    rc, sha, err = git(runner, full, "rev-parse", "HEAD")
    if rc != 0 or len(sha) != 40:
        raise CorpusError(f"could not read {path}'s HEAD: {err or sha}")
    return sha


def repo_root(runner, start: str) -> str | None:
    rc, out, _ = git(runner, start, "rev-parse", "--show-toplevel")
    return out if rc == 0 and out else None


def main(argv: list[str] | None = None, runner=None) -> int:
    runner = runner or _default_runner
    epilog = "exit codes:\n" + "\n".join(f"  {k}  {v}" for k, v in sorted(EXIT_MEANING.items()))
    ap = argparse.ArgumentParser(
        description=__doc__.split("\n\n")[0],
        epilog=epilog, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--corpus-pr", type=int, default=None,
                    help="resolve the head of this corpus pull request instead of "
                         f"{CORPUS_DEFAULT_BRANCH}")
    ap.add_argument("--ref", default=None,
                    help=f"resolve an arbitrary corpus ref (default: {CORPUS_DEFAULT_BRANCH})")
    ap.add_argument("--print", dest="print_only", action="store_true",
                    help="print what is checked out now; touches no network")
    ap.add_argument("--quiet", action="store_true",
                    help="print only the SHA, for capture in a script")
    ap.add_argument("--path", default=CORPUS_PATH,
                    help=f"where the corpus goes (default: {CORPUS_PATH})")
    ap.add_argument("--root", default=None,
                    help="repository root (default: discovered from this script)")
    args = ap.parse_args(argv)

    start = args.root or os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    root = args.root or repo_root(runner, start)
    if not root:
        print("corpus-checkout: not inside a git repository", file=sys.stderr)
        return 3

    try:
        if args.print_only:
            sha, ref = read_current(runner, root, args.path), "already checked out"
        else:
            ref = ref_for(args.corpus_pr, args.ref)
            sha = resolve(runner, root, ref, args.path)
    except CorpusError as e:
        print(f"corpus-checkout: {e}", file=sys.stderr)
        return 3

    print(sha if args.quiet else f"corpus: {sha} ({ref})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
