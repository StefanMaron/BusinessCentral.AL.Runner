#!/usr/bin/env python3
"""Unit tests for tools/corpus-checkout.py (#3737).

The behaviour worth pinning is what happens when the corpus CANNOT be resolved.
Every one of those paths is a candidate to quietly become "well, master then",
and master is also the right answer for the ordinary case -- so a wrong answer
here is indistinguishable from a right one by inspection, and only a test that
asserts the refusal can tell them apart (`guards-need-a-third-state.md`).

The happy paths run against a REAL local git repository standing in for the
corpus remote, so the fetch/checkout sequence is exercised rather than described.
The failure paths use a recording runner, because "the network is unreachable"
and "the corpus pull request was never pushed" are not states you can construct
on demand, and they are exactly the ones that must not resolve master.

Run: python3 tools/test_corpus_checkout.py
"""
from __future__ import annotations

import importlib.util
import io
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("corpus_checkout",
                                               os.path.join(HERE, "corpus-checkout.py"))
cc = importlib.util.module_from_spec(_spec)
sys.modules["corpus_checkout"] = cc
_spec.loader.exec_module(cc)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


def git(cwd: str, *args: str) -> str:
    return subprocess.run(["git", "-C", cwd] + list(args), check=True,
                          capture_output=True, text=True, encoding="utf-8").stdout


# ------------------------------------------------------------------ ref_for
# One input, one ref. The default is master, and it is a constant rather than
# something a caller passes, because naming `main` for this repository is its own
# documented time sink (`al-language-submodule.md`).

check("no arguments resolve master", cc.ref_for(None, None) == "master")
check("a corpus PR resolves its branch HEAD, not the merge ref",
      cc.ref_for(293, None) == "refs/pull/293/head")
check("an explicit ref is passed through", cc.ref_for(None, "refs/tags/v1") == "refs/tags/v1")

try:
    cc.ref_for(293, "master")
    check("naming two corpora refuses", False, "no CorpusError raised")
except cc.CorpusError as e:
    check("naming two corpora refuses", "exactly one" in str(e), str(e))


# ------------------------------------------------------------ checkout_state
with tempfile.TemporaryDirectory() as tmp:
    check("an absent directory is a clone",
          cc.checkout_state(os.path.join(tmp, "nope")) == "clone")

    empty = os.path.join(tmp, "empty")
    os.makedirs(empty)
    check("an empty directory is a clone", cc.checkout_state(empty) == "clone")

    real = os.path.join(tmp, "real")
    os.makedirs(real)
    subprocess.run(["git", "init", "--quiet", real], check=True, capture_output=True)
    check("a real clone is a fetch", cc.checkout_state(real) == "fetch")

    # The migration state. A `.git` FILE means the pre-#3737 submodule checkout,
    # whose git directory is shared by every worktree of this repository -- the
    # one thing this tool must never delete on someone's behalf.
    leftover = os.path.join(tmp, "leftover")
    os.makedirs(leftover)
    with open(os.path.join(leftover, ".git"), "w", encoding="utf-8") as fh:
        fh.write("gitdir: ../../.git/modules/tests/al-language\n")
    check("a .git FILE is the submodule leftover",
          cc.checkout_state(leftover) == "submodule-leftover")

    junk = os.path.join(tmp, "junk")
    os.makedirs(junk)
    with open(os.path.join(junk, "hello.txt"), "w", encoding="utf-8") as fh:
        fh.write("not a corpus\n")
    check("a non-empty non-repository is not a clone target",
          cc.checkout_state(junk) == "not-a-repo")


# ------------------------------------------------------------ resolve: happy
# A real fetch against a real local repository standing in for the corpus remote.

with tempfile.TemporaryDirectory() as tmp:
    remote = os.path.join(tmp, "corpus-remote")
    os.makedirs(remote)
    subprocess.run(["git", "init", "--quiet", "-b", "master", remote],
                   check=True, capture_output=True)
    git(remote, "config", "user.email", "t@example.com")
    git(remote, "config", "user.name", "t")
    with open(os.path.join(remote, "README.md"), "w", encoding="utf-8") as fh:
        fh.write("corpus\n")
    git(remote, "add", "README.md")
    git(remote, "commit", "-qm", "first")
    first = git(remote, "rev-parse", "HEAD").strip()

    # A second commit on a side ref, standing in for refs/pull/<N>/head.
    git(remote, "checkout", "-q", "-b", "side")
    with open(os.path.join(remote, "NEW.md"), "w", encoding="utf-8") as fh:
        fh.write("a corpus pull request\n")
    git(remote, "add", "NEW.md")
    git(remote, "commit", "-qm", "second")
    side = git(remote, "rev-parse", "HEAD").strip()
    git(remote, "checkout", "-q", "master")

    root = os.path.join(tmp, "runner")
    os.makedirs(root)

    # The module's remote constant is the real corpus; point it at the local
    # stand-in for the duration, which is the only way to exercise the real
    # fetch/checkout sequence without a network.
    real_remote, cc.CORPUS_REMOTE = cc.CORPUS_REMOTE, remote
    try:
        sha = cc.resolve(cc._default_runner, root, "master")
        check("a first resolve clones and returns master's full SHA",
              sha == first, f"{sha} != {first}")
        check("and the corpus files are on disk",
              os.path.isfile(os.path.join(root, "tests", "al-language", "README.md")))

        # Second call over the same directory: the fetch path, not the clone path.
        sha2 = cc.resolve(cc._default_runner, root, "side")
        check("a second resolve moves the same clone to another ref",
              sha2 == side, f"{sha2} != {side}")
        check("and the ref's own files arrive with it",
              os.path.isfile(os.path.join(root, "tests", "al-language", "NEW.md")))

        check("--print reads back what is checked out, with no network",
              cc.read_current(cc._default_runner, root) == side)

        # Back to master, and the side ref's file must be GONE. A checkout that
        # left it behind would run tests the resolved corpus does not declare --
        # the count would be right and the tree would not.
        cc.resolve(cc._default_runner, root, "master")
        check("moving back to master removes the other ref's files",
              not os.path.isfile(os.path.join(root, "tests", "al-language", "NEW.md")))

        # The refusal that matters most: a ref the remote does not have.
        try:
            cc.resolve(cc._default_runner, root, "refs/pull/999999/head")
            check("a ref the remote does not have refuses", False, "no CorpusError raised")
        except cc.CorpusError as e:
            check("a ref the remote does not have refuses",
                  "could not fetch" in str(e), str(e))
            check("and the refusal says master is not the fallback",
                  "reason to measure master instead" in str(e), str(e))

        # ...and the working tree is still whatever it was, not silently master-
        # from-somewhere-else. (It IS master here, but because the failed fetch
        # left it alone, not because anything fell back to it.)
        check("a failed fetch leaves the previous checkout in place",
              cc.read_current(cc._default_runner, root) == first)
    finally:
        cc.CORPUS_REMOTE = real_remote


# -------------------------------------------------- resolve: the leftover state
with tempfile.TemporaryDirectory() as tmp:
    root = os.path.join(tmp, "runner")
    path = os.path.join(root, "tests", "al-language")
    os.makedirs(path)
    with open(os.path.join(path, ".git"), "w", encoding="utf-8") as fh:
        fh.write("gitdir: ../../.git/modules/tests/al-language\n")

    calls: list[list[str]] = []

    def recording(args, cwd=None):
        calls.append(list(args))
        return subprocess.CompletedProcess(args, 0, "", "")

    try:
        cc.resolve(recording, root, "master")
        check("the pre-#3737 submodule checkout refuses", False, "no CorpusError raised")
    except cc.CorpusError as e:
        check("the pre-#3737 submodule checkout refuses", True)
        check("and names the remedy rather than running it",
              "rm -rf tests/al-language" in str(e), str(e))
        check("and runs no git at all in that directory",
              calls == [], f"ran {calls}")
    # The point of the refusal: it must not delete the SHARED gitdir pointer.
    check("the leftover .git file is left alone",
          os.path.isfile(os.path.join(path, ".git")))


# ------------------------------------------------------------------ main()
# The exit code is the contract a workflow step reads. Anything that could not
# resolve is 3 -- never 0 with master quietly substituted.

with tempfile.TemporaryDirectory() as tmp:
    root = os.path.join(tmp, "runner")
    os.makedirs(root)

    err, sys.stderr = io.StringIO(), io.StringIO()
    out, sys.stdout = io.StringIO(), io.StringIO()
    real_out, real_err = sys.stdout, sys.stderr
    try:
        rc = cc.main(["--print", "--root", root])
        printed_err = real_err.getvalue()
    finally:
        sys.stdout, sys.stderr = sys.__stdout__, sys.__stderr__
    check("--print with no corpus clone exits 3, not 0", rc == 3, f"rc={rc}")
    check("and says there is no pin to fall back on",
          "no pin to fall back on" in printed_err, printed_err)

    real_out, real_err = io.StringIO(), io.StringIO()
    sys.stdout, sys.stderr = real_out, real_err
    try:
        rc = cc.main(["--corpus-pr", "293", "--ref", "master", "--root", root])
        printed_err = real_err.getvalue()
    finally:
        sys.stdout, sys.stderr = sys.__stdout__, sys.__stderr__
    check("naming two corpora exits 3 from main()", rc == 3, f"rc={rc}")
    check("and says a run measures exactly one",
          "exactly one" in printed_err, printed_err)

print()
if FAILURES:
    print(f"{len(FAILURES)} FAILED: {', '.join(FAILURES)}")
    sys.exit(1)
print("all corpus-checkout tests passed")
