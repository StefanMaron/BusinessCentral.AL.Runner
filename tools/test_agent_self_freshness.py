#!/usr/bin/env python3
"""Unit tests for tools/agent_self_freshness.py, and for ci-wait.py refusing on a
stale copy of itself.

The staleness cases build a REAL throwaway git repository -- a bare "remote", a
work checkout, and a commit landing on the remote's main after the checkout
branched -- because the whole question is what git says about a checkout's
relationship to `origin/main`, and a mocked answer to that would prove nothing.
`tools/test_preflight.py` already builds throwaway repositories for the same
reason.

The `git ls-remote` cases are driven through an injected runner instead, because
the one that matters -- exit 0 with no parseable line, which is what a failed
connection looks like -- cannot be produced on demand from a working network.

Run: python3 tools/test_agent_self_freshness.py
"""
from __future__ import annotations

import importlib.util
import io
import json
import contextlib
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import agent_self_freshness as asf  # noqa: E402

_spec = importlib.util.spec_from_file_location("ci_wait", os.path.join(HERE, "ci-wait.py"))
cw = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(cw)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


# ---------------------------------------------------------------------------
# A throwaway repository: `remote` is bare, `work` is a checkout of it.
# `tools/ci-wait.py` lands in v1, then v2 lands on the remote's main only.

def git(cwd, *args):
    p = subprocess.run(["git", "-C", cwd, *args], capture_output=True, text=True)
    if p.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} -> {p.returncode}: {p.stderr}")
    return p.stdout.strip()


def make_repo(tmp: str) -> tuple[str, str]:
    remote = os.path.join(tmp, "remote.git")
    work = os.path.join(tmp, "work")
    subprocess.run(["git", "init", "--bare", "-b", "main", remote],
                   capture_output=True, check=True)
    subprocess.run(["git", "clone", remote, work], capture_output=True, check=True)
    git(work, "config", "user.email", "t@t")
    git(work, "config", "user.name", "t")
    os.makedirs(os.path.join(work, "tools"), exist_ok=True)
    write(work, "tools/ci-wait.py", "# v1\n")
    git(work, "add", "-A")
    git(work, "commit", "-m", "v1")
    git(work, "push", "origin", "main")
    return remote, work


def write(root: str, rel: str, text: str) -> None:
    path = os.path.join(root, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as fh:
        fh.write(text)


def land_on_remote(tmp: str, remote: str, rel: str, text: str) -> None:
    """Publish a new version of `rel` on the remote's main, behind the work tree's back."""
    other = tempfile.mkdtemp(dir=tmp)
    subprocess.run(["git", "clone", remote, other], capture_output=True, check=True)
    git(other, "config", "user.email", "t@t")
    git(other, "config", "user.name", "t")
    write(other, rel, text)
    git(other, "add", "-A")
    git(other, "commit", "-m", "newer")
    git(other, "push", "origin", "main")


print("agent_self_freshness")

tmp = tempfile.mkdtemp()
try:
    # --- RED: the checkout is behind origin/main on this very file --------------
    remote, work = make_repo(tmp)
    land_on_remote(tmp, remote, "tools/ci-wait.py", "# v2 -- the fix\n")
    git(work, "fetch", "origin", "main")     # origin/main now v2; HEAD still v1

    r = asf.assess(os.path.join(work, "tools/ci-wait.py"), remote_check=False)
    check("a checkout behind origin/main ON THIS FILE is STALE", r.state == "stale",
          f"{r.state} {r.notes}")
    check("...and refuse is set", r.refuse is True, f"{r.refuse}")
    check("...and the note names origin/main's version",
          any("origin/main" in n for n in r.notes), r.notes)
    check("...and the note names a remedy that does not touch the branch",
          any("git show origin/main:" in n for n in r.notes), r.notes)

    # A stale copy that has ALSO been edited locally is still stale: editing an
    # old file does not incorporate what landed on main since.
    write(work, "tools/ci-wait.py", "# v1 plus a local tweak\n")
    r = asf.assess(os.path.join(work, "tools/ci-wait.py"), remote_check=False)
    check("a stale copy that is also locally EDITED is still STALE", r.state == "stale",
          f"{r.state} {r.notes}")

    # --- GREEN: fast-forward and it is current ---------------------------------
    git(work, "checkout", "--", "tools/ci-wait.py")
    git(work, "merge", "--ff-only", "origin/main")
    r = asf.assess(os.path.join(work, "tools/ci-wait.py"), remote_check=False)
    check("a fast-forwarded checkout is CURRENT", r.state == "current",
          f"{r.state} {r.notes}")
    check("...and does not refuse", r.refuse is False, f"{r.refuse}")

    # --- a branch that LEGITIMATELY modifies the tool is not stale --------------
    # This is the escape the issue said a freshness check would need. It needs no
    # flag: what makes a copy stale is origin/main having moved the file since the
    # branch point, not the working file differing from origin/main's.
    git(work, "checkout", "-b", "agent/x/issue-1")
    write(work, "tools/ci-wait.py", "# v2 plus my fix\n")
    git(work, "add", "-A")
    git(work, "commit", "-m", "my fix to the tool")
    r = asf.assess(os.path.join(work, "tools/ci-wait.py"), remote_check=False)
    check("a branch that MODIFIES the tool is not stale", r.state == "current",
          f"{r.state} {r.notes}")
    check("...and says the working copy differs from origin/main",
          any("differs from origin/main" in n for n in r.notes), r.notes)

    # ...until origin/main moves the file underneath it, which it must then absorb.
    land_on_remote(tmp, remote, "tools/ci-wait.py", "# v3 -- landed while I worked\n")
    git(work, "fetch", "origin", "main")
    r = asf.assess(os.path.join(work, "tools/ci-wait.py"), remote_check=False)
    check("a modifying branch goes STALE once main moves the same file",
          r.state == "stale", f"{r.state} {r.notes}")

    # --- a file origin/main does not have at all --------------------------------
    r = asf.assess(os.path.join(work, "tools/brand-new.py"), remote_check=False)
    check("a file that does not exist locally is UNKNOWN, not stale",
          r.state == "unknown" and r.refuse is False, f"{r.state} {r.notes}")

    write(work, "tools/brand-new.py", "# new\n")
    git(work, "add", "-A")
    git(work, "commit", "-m", "add a new tool")
    r = asf.assess(os.path.join(work, "tools/brand-new.py"), remote_check=False)
    check("a file introduced by this branch is NEW, and does not refuse",
          r.state == "new" and r.refuse is False, f"{r.state} {r.notes}")

    # --- outside a git repository ----------------------------------------------
    # The remedy this tool prints runs origin/main's copy out of a temp directory,
    # so that copy must not refuse itself. It says so out loud instead.
    loose = os.path.join(tmp, "loose")
    os.makedirs(loose, exist_ok=True)
    write(loose, "ci-wait.py", "# v3\n")
    r = asf.assess(os.path.join(loose, "ci-wait.py"), remote_check=False)
    check("a copy outside any git repository is UNKNOWN and answers anyway",
          r.state == "unknown" and r.refuse is False, f"{r.state} {r.notes}")
    check("...and says why it could not be established",
          any("could not" in n.lower() for n in r.notes), r.notes)

    # --- the remote confirmation is never allowed to REFUSE ---------------------
    # `origin/main` is a repository-level ref shared by every worktree, and on this
    # box it is refreshed every few tens of minutes. Confirming it against the
    # remote closes that window; failing to confirm it must cost a NOTE, never a
    # verdict, or a network blip strands every agent.
    git(work, "checkout", "main")
    git(work, "merge", "--ff-only", "origin/main")

    def unreachable(args, timeout=None):
        # Only the remote call fails; every other git command runs for real, so
        # the local half of the check is genuinely exercised.
        if "ls-remote" in args:
            return 128, "", "fatal: could not read from remote repository"
        return asf._default_runner(args, timeout)

    r = asf.assess(os.path.join(work, "tools/ci-wait.py"), remote_check=True,
                   runner=asf.make_runner(unreachable))
    check("an unreachable remote does NOT refuse", r.refuse is False,
          f"{r.state} {r.notes}")
    check("...and says the base could not be confirmed",
          r.base_confirmed == "unreachable", r.base_confirmed)
    check("...and the note is loud about it",
          any("could not confirm" in n.lower() for n in r.notes), r.notes)
finally:
    shutil.rmtree(tmp, ignore_errors=True)

# ---------------------------------------------------------------------------
# `git ls-remote` with exit 0 and no parseable line is what a FAILED CONNECTION
# looks like -- not "the ref does not exist". `--exit-code` is what separates
# them: measured on git 2.55, a missing ref exits 2 and a present one exits 0
# with "<40-hex>\trefs/heads/main". Reading an empty exit 0 as "no such ref"
# would make the tool refuse for the wrong reason.

sha = "a" * 40
check("a well-formed ls-remote line parses to the sha",
      asf.parse_ls_remote(0, f"{sha}\trefs/heads/main\n") == (sha, "ok"))
check("exit 2 is 'the ref does not exist', not a network failure",
      asf.parse_ls_remote(2, "") == (None, "no-ref"))
check("exit 0 with NO output is a network failure, never 'no-ref'",
      asf.parse_ls_remote(0, "") == (None, "unreachable"))
check("exit 0 with unparseable output is a network failure",
      asf.parse_ls_remote(0, "warning: something\n") == (None, "unreachable"))
check("a non-zero exit that is not 2 is a network failure",
      asf.parse_ls_remote(128, "fatal: could not read") == (None, "unreachable"))
# mise prints a banner on stdout; a capture that is merely non-empty is not proof.
check("a mise banner ahead of the line does not break the parse",
      asf.parse_ls_remote(0, f"mise ~/.config/mise/config.toml tools: git\n{sha}\trefs/heads/main\n")
      == (sha, "ok"))
check("a mise banner ALONE is still unreachable, not a sha",
      asf.parse_ls_remote(0, "mise ~/.config/mise/config.toml tools: git\n")
      == (None, "unreachable"))
check("a short hex string is not accepted as a sha",
      asf.parse_ls_remote(0, "abc123\trefs/heads/main\n") == (None, "unreachable"))

# ---------------------------------------------------------------------------
# ci-wait.py must REFUSE, with exit 3, before it asks GitHub anything.

print("\nci-wait.py refuses a stale copy of itself")

tmp = tempfile.mkdtemp()
try:
    remote, work = make_repo(tmp)
    shutil.copy(os.path.join(HERE, "ci-wait.py"), os.path.join(work, "tools/ci-wait.py"))
    shutil.copy(os.path.join(HERE, "agent_self_freshness.py"),
                os.path.join(work, "tools/agent_self_freshness.py"))
    git(work, "add", "-A")
    git(work, "commit", "-m", "the real tool")
    git(work, "push", "origin", "main")
    land_on_remote(tmp, remote, "tools/ci-wait.py", "# a newer ci-wait\n")
    git(work, "fetch", "origin", "main")

    stale = os.path.join(work, "tools/ci-wait.py")
    spec = importlib.util.spec_from_file_location("ci_wait_stale", stale)
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)

    asked: list[str] = []

    def gh_spy(args, attempts=4):
        asked.append(" ".join(args))
        return 0, json.dumps([])

    m.gh = gh_spy
    old = sys.argv
    sys.argv = ["ci-wait.py", "2971", "--timeout", "1", "--interval", "0", "--no-log"]
    buf = io.StringIO()
    try:
        with contextlib.redirect_stdout(buf), contextlib.redirect_stderr(buf):
            code = m.main()
    finally:
        sys.argv = old
    text = buf.getvalue()

    check("a stale ci-wait.py exits 3 -- undetermined, not a verdict", code == 3,
          f"code={code} {text[:400]}")
    check("...and asks GitHub NOTHING", asked == [], asked)
    check("...and says it refused because it is not current",
          "STALE" in text and "not a verdict" in text.lower(), text[:400])
    check("...and prints the runnable remedy", "git show origin/main:" in text, text[:600])

    # Same repository, fast-forwarded: the guard must get out of the way.
    git(work, "merge", "--ff-only", "origin/main")
    shutil.copy(os.path.join(HERE, "ci-wait.py"), os.path.join(work, "tools/ci-wait.py"))
    git(work, "add", "-A")
    git(work, "commit", "-m", "restore")
    r = asf.assess(os.path.join(work, "tools/ci-wait.py"), remote_check=False)
    check("the same file on a fast-forwarded checkout is not refused",
          r.refuse is False, f"{r.state} {r.notes}")
finally:
    shutil.rmtree(tmp, ignore_errors=True)

print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
