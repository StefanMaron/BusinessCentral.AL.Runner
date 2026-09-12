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
import scratch_git_config  # noqa: E402
scratch_git_config.isolate()  # scratch commits must not reach the user's signer (#4001)
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


def has_ref(cwd: str, ref: str) -> bool:
    """True if `ref` resolves. `git rev-parse --verify` exits 1 when it does not,
    which is an answer rather than a failure, so this must not use git() above."""
    p = subprocess.run(["git", "-C", cwd, "rev-parse", "--verify", "--quiet", ref],
                       capture_output=True, text=True)
    return p.returncode == 0 and bool(p.stdout.strip())


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
    # A path that does not exist is untracked, so nothing vouches for it -- and a
    # tool asked about a file that is not there has established nothing at all.
    r = asf.assess(os.path.join(work, "tools/brand-new.py"), remote_check=False)
    check("a file that does not exist locally is UNKNOWN and unvouched",
          r.state == "unknown" and r.refuse is True and r.provenance == "unvouched",
          f"{r.state}/{r.provenance} refuse={r.refuse}")

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
    check("...and says the freshness could not be checked there",
          any("cannot be checked here" in n for n in r.notes), r.notes)
    check("...and is classified as detached, the one unknown with a provenance story",
          r.state == "unknown" and r.provenance == "detached", f"{r.state}/{r.provenance}")

    # --- an unknown with NO provenance story must REFUSE (#3296) ----------------
    # The sharper case the issue does not cover: a REAL repository, the tools
    # tracked in it, content of any age, and no refs/remotes/origin/main. Nothing
    # about that says the running copy is current -- unlike the temp-directory
    # case above, where the extraction itself is the provenance. Answering here is
    # the guard reaching a verdict with the safety check skipped.
    # No local ref AND no remote to ask: nothing anywhere can vouch for this, so
    # there is no answer to give. Note the repository has no `origin` at all --
    # that is what makes it unvouched, NOT the missing local ref on its own (see
    # the shallow-CI case below, which has no local ref and is answered).
    noremote = os.path.join(tmp, "noremote")
    os.makedirs(os.path.join(noremote, "tools"), exist_ok=True)
    subprocess.run(["git", "init", "-b", "main", noremote], capture_output=True, check=True)
    git(noremote, "config", "user.email", "t@t")
    git(noremote, "config", "user.name", "t")
    write(noremote, "tools/ci-wait.py", "# arbitrarily old\n")
    git(noremote, "add", "-A")
    git(noremote, "commit", "-m", "tools, of unknown age")
    r = asf.assess(os.path.join(noremote, "tools/ci-wait.py"), remote_check=False)
    check("no origin/main locally AND no remote to ask REFUSES",
          r.state == "unknown" and r.refuse is True, f"{r.state} refuse={r.refuse} {r.notes}")
    check("...and is classified as unvouched, not detached",
          r.provenance == "unvouched", f"{r.provenance}")
    check("...and names the missing ref",
          any("refs/remotes/origin/main" in n for n in r.notes), r.notes)
    check("...and says the remote was asked too, not just the local ref",
          any("could not be reached" in n for n in r.notes), r.notes)
    check("...and does not claim to be answering anyway",
          not any("answering anyway" in n.lower() for n in r.notes), r.notes)

    # An UNTRACKED file inside a real repository: same absence of a story. It is
    # not the extract-to-/tmp recipe (that lands outside a repository), and being
    # untracked is precisely what makes its age unknowable from git.
    write(work, "tools/untracked-tool.py", "# who knows how old\n")
    r = asf.assess(os.path.join(work, "tools/untracked-tool.py"), remote_check=False)
    check("an UNTRACKED file inside a repository REFUSES",
          r.state == "unknown" and r.refuse is True, f"{r.state} refuse={r.refuse} {r.notes}")
    check("...and is classified as unvouched",
          r.provenance == "unvouched", f"{r.provenance}")
    os.remove(os.path.join(work, "tools/untracked-tool.py"))

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

    # --- the one branch that JUSTIFIES spending an ls-remote -------------------
    # refs/remotes/origin/main is shared by every worktree and is refreshed every
    # few tens of minutes, so the local check catches days-scale drift on its own.
    # The remote call earns its keep only here: the shared ref is itself behind,
    # and the file moved inside that window. This is the only path on which the
    # remote check turns "current" into a refusal, so it is the only one that
    # decides whether the call is worth making at all.
    base_before = git(work, "rev-parse", "refs/remotes/origin/main")
    land_on_remote(tmp, remote, "tools/ci-wait.py", "# v4 -- landed minutes ago\n")
    # Deliberately NOT fetched: local origin/main still points at v3.
    r = asf.assess(os.path.join(work, "tools/ci-wait.py"), remote_check=True)
    check("a local origin/main that is itself behind is fetched and re-judged",
          r.base_confirmed == "refreshed", f"{r.base_confirmed} {r.notes}")
    check("...and the answer flips from current to STALE",
          r.state == "stale" and r.refuse is True, f"{r.state} {r.refuse}")
    check("...and the ref actually moved",
          git(work, "rev-parse", "refs/remotes/origin/main") != base_before)
    check("...and the note names both ends of the fetch",
          any(base_before[:8] in n for n in r.notes), r.notes)

    # Same situation, but the fetch fails. Knowing we are behind and being unable
    # to close the gap is a LOUD NOTE on the older ref's answer, never a refusal:
    # a failed fetch is a network fact, not evidence about this checkout.
    git(work, "merge", "--ff-only", "origin/main")
    land_on_remote(tmp, remote, "tools/ci-wait.py", "# v5\n")

    def fetch_fails(args, timeout=None):
        if "fetch" in args:
            return 128, "", "fatal: unable to access remote"
        return asf._default_runner(args, timeout)

    r = asf.assess(os.path.join(work, "tools/ci-wait.py"), remote_check=True,
                   runner=asf.make_runner(fetch_fails))
    check("a failed fetch off a known-behind ref does NOT refuse", r.refuse is False,
          f"{r.state} {r.notes}")
    check("...and records that the base is behind and unfetchable",
          r.base_confirmed == "behind-unfetchable", r.base_confirmed)
    check("...and says the check ran against the OLDER ref",
          any("OLDER ref" in n for n in r.notes), r.notes)

    # --- a shallow CI checkout: no local origin/main, but a reachable remote ---
    # THE REGRESSION THIS SECTION EXISTS FOR. GitHub Actions checks out with
    # fetch-depth 1 and a refspec covering only the branch under test, so
    # refs/remotes/origin/main does not exist on any CI run. The first version of
    # this fix refused there -- every run, including the three checks below --
    # because it read "no local ref" as "nothing can vouch for this". The remote
    # answers fine; it just has to be asked.
    #
    # Built as a REAL shallow clone rather than a mocked one, because the whole
    # question is what git does in that shape.
    ci = os.path.join(tmp, "ci-shallow")
    subprocess.run(["git", "clone", "--depth", "1", "-b", "main", "file://" + remote, ci],
                   capture_output=True, check=True)
    check("the CI-shaped checkout really is shallow",
          git(ci, "rev-parse", "--is-shallow-repository") == "true")
    subprocess.run(["git", "-C", ci, "config", "remote.origin.fetch",
                    "+refs/heads/main:refs/remotes/origin/main-only"],
                   capture_output=True, check=True)
    subprocess.run(["git", "-C", ci, "update-ref", "-d", "refs/remotes/origin/main"],
                   capture_output=True)
    check("...and really has no refs/remotes/origin/main",
          not has_ref(ci, "refs/remotes/origin/main"),
          git(ci, "for-each-ref", "refs/remotes"))

    r = asf.assess(os.path.join(ci, "tools/ci-wait.py"), remote_check=False)
    check("a shallow CI checkout with no local origin/main is NOT refused",
          r.refuse is False, f"{r.state}/{r.provenance} {r.notes}")
    check("...and says the base was fetched directly from the remote",
          r.base_confirmed == "fetched-direct", f"{r.base_confirmed} {r.notes}")
    check("...and says why, naming the shallow CI checkout",
          any("shallow" in n.lower() for n in r.notes), r.notes)

    # The fallback must not silently reorganise the caller's repository: it is a
    # read. `git fetch <remote> <branch>` with no refspec writes FETCH_HEAD only.
    check("...and creates no refs/remotes/origin/main behind the caller's back",
          not has_ref(ci, "refs/remotes/origin/main"),
          git(ci, "for-each-ref", "refs/remotes"))

    # It must still be able to say STALE there -- an answer that can only ever be
    # "fine" is not a guard. origin/main moves the file; the shallow checkout has
    # not incorporated it.
    land_on_remote(tmp, remote, "tools/ci-wait.py", "# vNEXT -- landed after the CI clone\n")
    r = asf.assess(os.path.join(ci, "tools/ci-wait.py"), remote_check=False)
    check("a shallow CI checkout still detects a file main has moved past",
          r.state == "stale" and r.refuse is True, f"{r.state} refuse={r.refuse} {r.notes}")

    # --- the shape that ACTUALLY runs in CI: shallow AND on a divergent branch -
    # The case above clones `main` itself, so the file matches and the `identical`
    # route answers. A real PR run is a shallow clone of a BRANCH THAT EDITS the
    # guarded tool -- the file differs AND there is no merge base, which is the
    # `unvouched` route. That refused every CI run on the first version of this
    # fix, and the synthetic test above did not catch it because its shallow
    # clone had nothing to diverge from. Reproduced here for real.
    branchy = os.path.join(tmp, "ci-branch")
    subprocess.run(["git", "clone", "--depth", "1", "-b", "main",
                    "file://" + remote, branchy], capture_output=True, check=True)
    git(branchy, "config", "user.email", "t@t")
    git(branchy, "config", "user.name", "t")
    subprocess.run(["git", "-C", branchy, "update-ref", "-d",
                    "refs/remotes/origin/main"], capture_output=True)
    git(branchy, "checkout", "-b", "agent/x/issue-9")
    write(branchy, "tools/ci-wait.py", "# my in-flight fix to the tool\n")
    git(branchy, "add", "-A")
    git(branchy, "commit", "-m", "edit the guarded tool, as this very PR does")
    check("the CI-branch checkout is shallow, divergent, and has no origin/main",
          git(branchy, "rev-parse", "--is-shallow-repository") == "true"
          and not has_ref(branchy, "refs/remotes/origin/main"))

    _head_before = git(branchy, "rev-parse", "HEAD")
    _refs_before = git(branchy, "for-each-ref", "refs/heads")
    r = asf.assess(os.path.join(branchy, "tools/ci-wait.py"), remote_check=False)
    check("a shallow checkout of a branch EDITING the tool is not refused",
          r.refuse is False, f"{r.state}/{r.provenance} {r.notes}")
    check("...and is judged CURRENT -- a branch that edits the tool, not a stale one",
          r.state == "current", f"{r.state} {r.notes}")
    check("...and says the base was fetched directly, no local ref needed",
          r.base_confirmed == "fetched-direct", f"{r.base_confirmed} {r.notes}")

    # Whatever route it took, it is a READ: HEAD and every ref untouched. The
    # deepen step adds history objects only, and this holds whether or not it ran.
    check("...and moves HEAD nowhere",
          git(branchy, "rev-parse", "HEAD") == _head_before)
    check("...and creates or moves no branch ref",
          git(branchy, "for-each-ref", "refs/heads") == _refs_before)
    # THE BUG THIS PINS: a plain `git fetch <remote> <branch>` applies the
    # configured refspec and helpfully RECREATES refs/remotes/origin/main. A
    # later call then reads that resurrected ref as a local one and trusts it --
    # and it is already behind, so a genuinely stale file read as current.
    # `--refmap=` is what stops it.
    check("...and does NOT resurrect refs/remotes/origin/main",
          not has_ref(branchy, "refs/remotes/origin/main"),
          git(branchy, "for-each-ref", "refs/remotes"))

    # And it must still say STALE in that shape, or the fallback has traded a
    # refusal for a rubber stamp.
    land_on_remote(tmp, remote, "tools/ci-wait.py", "# vLATER -- published after the branch\n")
    r = asf.assess(os.path.join(branchy, "tools/ci-wait.py"), remote_check=False)
    check("a shallow divergent checkout still detects a genuinely STALE tool",
          r.state == "stale" and r.refuse is True, f"{r.state} refuse={r.refuse} {r.notes}")

    # --- no merge base: the blob itself is the provenance, or there is none ----
    # A shallow clone has no merge base with origin/main, so the branch-point
    # comparison is unavailable. But origin/main IS resolvable here, so the file
    # can be compared to it directly -- and a byte-identical file is vouched for
    # by that identity, whatever its history. A file that DIFFERS has nothing:
    # it may be a local edit or may be staleness, and the tool cannot tell.
    shallow = os.path.join(tmp, "shallow")
    subprocess.run(["git", "clone", "--depth", "1", "file://" + remote, shallow],
                   capture_output=True, check=True)
    git(shallow, "config", "user.email", "t@t")
    git(shallow, "config", "user.name", "t")

    def no_merge_base(args, timeout=None):
        if "merge-base" in args:
            return 128, "", "fatal: no merge base"
        return asf._default_runner(args, timeout)

    same = os.path.join(shallow, "tools/ci-wait.py")
    r = asf.assess(same, remote_check=False, runner=asf.make_runner(no_merge_base))
    check("no merge base, but the file MATCHES origin/main's blob: not refused",
          r.refuse is False, f"{r.state}/{r.provenance} {r.notes}")
    check("...and is vouched for by the blob identity",
          r.provenance == "identical", f"{r.provenance}")

    write(shallow, "tools/ci-wait.py", "# something else entirely\n")
    r = asf.assess(same, remote_check=False, runner=asf.make_runner(no_merge_base))
    check("no merge base and the file DIFFERS from origin/main: REFUSES",
          r.state == "unknown" and r.refuse is True,
          f"{r.state} refuse={r.refuse} {r.notes}")
    check("...and is classified as unvouched",
          r.provenance == "unvouched", f"{r.provenance}")

    # --- `remote` is a remote NAME, not a URL ---------------------------------
    # A URL resolves no refs/remotes/<name>/main, so it would answer "unknown" --
    # a caller error dressed up as an environment fact. Refuse the argument
    # instead; nothing in this repository passes anything but the default.
    for bad in ("https://github.com/o/r.git", "git@github.com:o/r.git", "a/b"):
        try:
            asf.assess(os.path.join(work, "tools/ci-wait.py"), remote=bad,
                       remote_check=False)
            check(f"a URL-shaped remote ({bad[:20]}) is refused", False, "no ValueError")
        except ValueError as exc:
            check(f"a URL-shaped remote ({bad[:20]}) is refused",
                  "remote name" in str(exc), str(exc))
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

# ---------------------------------------------------------------------------
# ci-wait.py end to end on the two unknowns, which must be handled DIFFERENTLY.
# The temp-directory recipe in .claude/rules/ci-verdicts.md must keep working --
# that recipe exists because the guard cannot help a copy older than itself --
# while a repository that vouches for nothing must not reach a verdict (#3296).

print("\nci-wait.py separates a vouched-for unknown from an unvouched one")


def run_ci_wait(path: str, argv: list[str]) -> tuple[int, str, list[str]]:
    """Import ci-wait.py from `path`, run main() with argv, spy on every gh call."""
    spec = importlib.util.spec_from_file_location("ci_wait_case_" + str(len(FAILURES)) + path[-14:].replace("/", "_").replace(".", "_"), path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    asked: list[str] = []

    def gh_spy(args, attempts=4):
        asked.append(" ".join(args))
        return 0, json.dumps([])

    mod.gh = gh_spy
    old = sys.argv
    sys.argv = ["ci-wait.py", *argv]
    buf = io.StringIO()
    try:
        with contextlib.redirect_stdout(buf), contextlib.redirect_stderr(buf):
            code = mod.main()
    finally:
        sys.argv = old
    return code, buf.getvalue(), asked


tmp = tempfile.mkdtemp()
try:
    # (a) UNVOUCHED: a real repository, tools tracked, no origin/main. Nothing
    # here has checked the running code, so there must be no verdict.
    repo = os.path.join(tmp, "novouch")
    os.makedirs(os.path.join(repo, "tools"), exist_ok=True)
    subprocess.run(["git", "init", "-b", "main", repo], capture_output=True, check=True)
    git(repo, "config", "user.email", "t@t")
    git(repo, "config", "user.name", "t")
    for name in ("ci-wait.py", "agent_self_freshness.py"):
        shutil.copy(os.path.join(HERE, name), os.path.join(repo, "tools", name))
    git(repo, "add", "-A")
    git(repo, "commit", "-m", "tools of unknown age")

    code, text, asked = run_ci_wait(os.path.join(repo, "tools/ci-wait.py"),
                                    ["2971", "--timeout", "1", "--interval", "0", "--no-log"])
    check("ci-wait.py in a repo with no origin/main exits 3", code == 3,
          f"code={code} {text[:400]}")
    check("...and asks GitHub NOTHING", asked == [], asked)
    check("...and says it is not a verdict", "not a verdict" in text.lower(), text[:400])
    # The remedy for "nothing vouches for this" is not the remedy for "stale", so
    # the refusal must not report one as the other.
    check("...and does NOT call an unvouched copy stale",
          "NOTHING VOUCHES FOR" in text and "STALE ci-wait.py" not in text, text[:600])

    # (b) DETACHED: the documented extract-to-a-temp-directory recipe. Both files
    # come straight out of origin/main by hand, so their provenance IS the
    # guarantee -- this must still answer, or the remedy refuses itself.
    loose = os.path.join(tmp, "loose")
    os.makedirs(loose, exist_ok=True)
    for name in ("ci-wait.py", "agent_self_freshness.py"):
        shutil.copy(os.path.join(HERE, name), os.path.join(loose, name))
    code, text, asked = run_ci_wait(os.path.join(loose, "ci-wait.py"),
                                    ["2971", "--timeout", "1", "--interval", "0", "--no-log"])
    check("the extract-to-/tmp recipe still reaches GitHub", asked != [],
          f"code={code} {text[:300]}")
    check("...and does not refuse for freshness", code != 3 or "STALE" not in text,
          f"code={code} {text[:300]}")
    check("...and says out loud that provenance is the caller's",
          "provenance" in text.lower(), text[:600])
finally:
    shutil.rmtree(tmp, ignore_errors=True)

# ---------------------------------------------------------------------------
# --timeout 0 must not produce a verdict-shaped non-verdict (#3351).

print("\nci-wait.py --timeout 0 is a single pass, not an instant non-answer")

tmp = tempfile.mkdtemp()
try:
    loose = os.path.join(tmp, "loose")
    os.makedirs(loose, exist_ok=True)
    for name in ("ci-wait.py", "agent_self_freshness.py"):
        shutil.copy(os.path.join(HERE, name), os.path.join(loose, name))

    code, text, asked = run_ci_wait(os.path.join(loose, "ci-wait.py"),
                                    ["2971", "--timeout", "0", "--interval", "0", "--no-log"])
    # The defect: the poll loop never ran, so no GitHub call was made and the
    # unconditional `return 2` fired with an empty reason in its parentheses.
    check("--timeout 0 actually LOOKS at the PR", asked != [],
          f"code={code} asked={asked} {text[:300]}")
    check("--timeout 0 never claims to have waited",
          "STILL RUNNING after 0s ()" not in text, text[:400])
    check("--timeout 0 does not advertise polling it will not do",
          "up to 0s, polling internally" not in text, text[:400])

    # A still-running answer must always carry a reason. An empty pair of
    # parentheses is the tell that the tool declined to look, and it is the only
    # thing distinguishing this from a genuine "not reported yet".
    check("no still-running line is ever emitted with an empty reason",
          "()" not in text, text[:400])
finally:
    shutil.rmtree(tmp, ignore_errors=True)

# ---------------------------------------------------------------------------
# The same shape one tool over. pr-body.py is the only sanctioned way to edit a
# PR body, every one of its guards exists because an unguarded edit destroyed
# one, and 40 of the 59 worktrees carrying it had a version that was not
# origin/main's. Running a copy that predates a guard is running without it.

print("\npr-body.py refuses a stale copy of itself")

tmp = tempfile.mkdtemp()
try:
    remote, work = make_repo(tmp)
    for name in ("pr-body.py", "agent_self_freshness.py"):
        shutil.copy(os.path.join(HERE, name), os.path.join(work, "tools", name))
    git(work, "add", "-A")
    git(work, "commit", "-m", "the real tool")
    git(work, "push", "origin", "main")
    land_on_remote(tmp, remote, "tools/pr-body.py", "# a newer pr-body\n")
    git(work, "fetch", "origin", "main")

    stale = os.path.join(work, "tools/pr-body.py")
    spec = importlib.util.spec_from_file_location("pr_body_stale", stale)
    pb = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(pb)

    said: list[str] = []
    rc = pb.freshness_refusal(printer=said.append)
    check("a stale pr-body.py refuses with EXIT_PRECONDITION",
          rc == pb.EXIT_PRECONDITION, f"{rc}")
    check("...and says nothing was read or written",
          any("Nothing was read or written" in m for m in said), said)
    check("...and prints the runnable remedy",
          any("git show origin/main:" in m for m in said), said)

    # A current copy must get out of the way, or the guard is just an outage.
    git(work, "merge", "--ff-only", "origin/main")
    shutil.copy(os.path.join(HERE, "pr-body.py"), stale)
    git(work, "add", "-A")
    git(work, "commit", "-m", "restore")
    spec = importlib.util.spec_from_file_location("pr_body_fresh", stale)
    pb2 = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(pb2)
    said2: list[str] = []
    check("a current pr-body.py is NOT refused",
          pb2.freshness_refusal(printer=said2.append) is None, said2)
finally:
    shutil.rmtree(tmp, ignore_errors=True)

print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
