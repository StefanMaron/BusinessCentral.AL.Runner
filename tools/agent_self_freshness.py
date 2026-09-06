#!/usr/bin/env python3
"""Refuse to answer out of a checkout that is behind `origin/main` on this file.

Why this exists
---------------
`tools/ci-wait.py` decides whether a PR is green, and the unattended merge bar
reads that decision. An agent invokes it by relative path, so it runs whichever
copy is on disk in that agent's worktree -- and a worktree is created once, at
the start of a task, and never fast-forwarded again. The tool being correct on
`origin/main` says nothing about the copy that actually ran.

Measured on this box on 2026-09-06, across the 109 worktrees of this repository:

    tools/ci-wait.py       99 present, 4 distinct versions, 71 NOT origin/main's
    tools/pr-body.py       59 present, 2 distinct versions, 40 NOT origin/main's
    tools/preflight.py     59 present, 4 distinct versions, 40 NOT origin/main's
    tools/context-pack.py 105 present, 1 distinct version,   0 NOT origin/main's

That last row is the point. The spread is not "old worktrees are old" -- a tool
nobody has changed lately is identical everywhere. It is the tools under ACTIVE
REPAIR that are running old, which is exactly backwards: the copy most likely to
carry a bug somebody already fixed is the one most likely to be on disk.

And it is not theoretical. Replaying the recorded PR #2971 rollup -- "Tests
updated" reported success, the matrix job not created yet -- through each of the
four on-disk versions of ci-wait.py:

    210986c3b 2026-09-02   exit 0   "GREEN -- all 1 required checks passed."
    6fa0fc2a9 2026-09-05   exit 0   "GREEN -- all 1 required checks passed."
    2f84c4de1 2026-09-06   exit 2   still running, NOT a verdict
    0cf933f08 2026-09-06   exit 2   still running, NOT a verdict   <- origin/main

59 of the 99 worktrees carrying the tool would have printed that false GREEN.
The oldest version answers a second recorded shape (#3010, a superseded
cancelled run) with a false FAILURE. Three of the four versions on disk disagree
with origin/main on at least one recorded input, in both directions.

What makes a copy stale
-----------------------
NOT "the working file differs from origin/main's". That test would fire on every
branch legitimately fixing the tool, which is why the issue expected a freshness
check to need an escape hatch for that case.

The right question is whether `origin/main` has moved this file since the
checkout branched:

    mb = git merge-base HEAD origin/main
    stale  <=>  origin/main:<file>  !=  mb:<file>

A branch that edits the tool is current until main also edits it -- at which
point it genuinely must absorb that change before its answer can be trusted.
A checkout that has not touched the tool but branched before a fix landed is
stale, which is the measured case. No flag, no escape hatch, no judgement.

`refs/remotes/origin/main` is a REPOSITORY-level ref, shared by every worktree of
this repository, so the comparison costs no network: any agent's `git fetch`
refreshes it for all 109. Its reflog on this box shows it moving every ten to
forty minutes. `remote_check=True` closes that residual window with one
`git ls-remote` (measured 0.5s), and closing it is the ONLY thing the network is
used for -- an unreachable remote costs a loud note, never a refusal, because a
transient network failure is not evidence of a stale checkout.

`git ls-remote` without `--exit-code` exits 0 and prints nothing when the ref
does not exist, which is indistinguishable from a failed connection. With it, a
missing ref exits 2 (measured, git 2.55). `parse_ls_remote` below therefore
treats an unparseable exit 0 as UNREACHABLE and only a literal exit 2 as
"no such ref" -- and requires a 40-hex sha rather than mere non-emptiness,
because `mise` prints a banner on stdout that a non-emptiness test would swallow.

What it cannot do
-----------------
It cannot help a copy that predates it. The ~71 stale ci-wait.py copies already
on disk have no guard in them and never will; they stop mattering as those
worktrees are recycled. Nothing that lives inside the file can fix a copy of the
file that is older than the fix -- only the invocation rule in
`.claude/rules/ci-verdicts.md` covers those.

A copy moved outside a git repository is UNKNOWN, and answers with a loud note
rather than refusing. That is deliberate: the remedy this module prints is to run
`origin/main`'s copy out of a temp directory, and a remedy that refuses itself is
not a remedy.
"""
from __future__ import annotations

import os
import re
import subprocess
from dataclasses import dataclass, field

_SHA_LINE = re.compile(r"^([0-9a-f]{40})\s+(\S+)$")


@dataclass
class Freshness:
    """What could be established about the running copy, and whether to stop.

    state:
      "current"  origin/main has not moved this file since this checkout's merge
                 base with it. The working file may still differ (a branch fixing
                 the tool) -- that is noted, not refused.
      "stale"    origin/main carries a version of this file that this checkout
                 has not incorporated. REFUSE: the running code is known to be
                 behind a published change to itself.
      "new"      the file does not exist on origin/main at all. Not refused.
      "unknown"  could not be established -- outside a git repository, no
                 origin/main ref, git missing, a shallow clone with no merge
                 base. Not refused; said out loud.

    base_confirmed:
      "confirmed"          the local origin/main ref matches the remote's main.
      "refreshed"          it did not, and a fetch brought it up to date.
      "behind-unfetchable" it did not, and the fetch failed.
      "unreachable"        the remote could not be reached at all.
      "no-ref"             the remote has no refs/heads/main.
      "skipped"            remote_check was off, or the answer was already stale.
    """

    state: str
    refuse: bool
    notes: list[str] = field(default_factory=list)
    base_confirmed: str = "skipped"
    local_blob: str | None = None
    base_blob: str | None = None


def make_runner(fn):
    """Wrap a (args, timeout) -> (rc, stdout, stderr) callable as a runner."""
    return fn


def _default_runner(args: list[str], timeout: int | None = None):
    try:
        p = subprocess.run(args, capture_output=True, text=True, timeout=timeout)
    except (OSError, subprocess.TimeoutExpired) as exc:
        return 128, "", str(exc)
    return p.returncode, p.stdout or "", p.stderr or ""


def parse_ls_remote(rc: int, out: str) -> tuple[str | None, str]:
    """(sha, state) out of `git ls-remote --exit-code <remote> <ref>`.

    state is "ok", "no-ref" (exit 2 -- the ref genuinely does not exist), or
    "unreachable" (anything else, INCLUDING exit 0 with nothing parseable in it,
    which is what a failed connection looks like without --exit-code).

    Never accepts a capture for being merely non-empty: `mise` prints a banner on
    stdout, and a non-emptiness test has already broken a health check, a
    PR-existence check and a version capture in this repository.
    """
    for line in (out or "").splitlines():
        m = _SHA_LINE.match(line.strip())
        if m:
            return m.group(1), "ok"
    if rc == 2:
        return None, "no-ref"
    return None, "unreachable"


def _git(runner, root: str | None, *args: str, timeout: int | None = 30):
    argv = ["git"] + (["-C", root] if root else []) + list(args)
    rc, out, err = runner(argv, timeout)
    return rc, out.strip(), err.strip()


def _rev(runner, root, spec):
    rc, out, _ = _git(runner, root, "rev-parse", "--verify", "--quiet", spec)
    return out if rc == 0 and out else None


def _remedy(relpath: str) -> str:
    return ("remedy -- run origin/main's copy without touching this branch:\n"
            "    git fetch origin main\n"
            f"    git show origin/main:{relpath} > /tmp/{os.path.basename(relpath)} "
            f"&& python3 /tmp/{os.path.basename(relpath)} <args>\n"
            "  or work from a checkout that is up to date with origin/main.")


def assess(path: str, *, remote_check: bool = True, remote: str = "origin",
           branch: str = "main", runner=None, timeout: int = 20) -> Freshness:
    """Whether the copy of `path` on disk is behind origin/<branch> on that file."""
    runner = runner or _default_runner
    ref = f"refs/remotes/{remote}/{branch}"

    path = os.path.abspath(path)
    directory = os.path.dirname(path)
    rc, root, _ = _git(runner, None, "-C", directory, "rev-parse", "--show-toplevel")
    if rc != 0 or not root:
        return Freshness("unknown", False, [
            f"note: could not establish whether {os.path.basename(path)} is current -- "
            f"{directory} is not inside a git repository (or git is unavailable). "
            "Answering anyway; nothing here has checked that this copy of the tool "
            "carries the latest fixes."])

    rc, relpath, _ = _git(runner, root, "ls-files", "--full-name", "--", path)
    relpath = relpath.splitlines()[0].strip() if relpath else ""
    if rc != 0 or not relpath:
        return Freshness("unknown", False, [
            f"note: could not establish whether {os.path.basename(path)} is current -- "
            f"it is not a tracked file in {root}. Answering anyway."])

    base = _rev(runner, root, ref)
    if not base:
        return Freshness("unknown", False, [
            f"note: could not establish whether {relpath} is current -- this "
            f"repository has no {ref}. Answering anyway."])

    notes: list[str] = []
    result = _evaluate(runner, root, relpath, base, notes)

    # The remote confirmation runs only when the local answer was NOT already
    # stale: a refusal should be instant and should not depend on the network.
    if result.state != "stale" and remote_check:
        rc, out, _ = _git(runner, root, "ls-remote", "--exit-code", remote,
                          f"refs/heads/{branch}", timeout=timeout)
        tip, tip_state = parse_ls_remote(rc, out)
        if tip_state == "ok" and tip == base:
            result.base_confirmed = "confirmed"
        elif tip_state == "ok":
            frc, _, ferr = _git(runner, root, "fetch", "--quiet", remote, branch,
                                timeout=timeout)
            new_base = _rev(runner, root, ref) if frc == 0 else None
            if frc == 0 and new_base:
                notes.append(f"note: {ref} was behind {remote}/{branch} and has been "
                             f"fetched ({base[:8]} -> {new_base[:8]}).")
                result = _evaluate(runner, root, relpath, new_base, notes)
                result.base_confirmed = "refreshed"
            else:
                result.base_confirmed = "behind-unfetchable"
                notes.append(
                    f"note: could not confirm this copy of {relpath} is current -- "
                    f"{ref} is behind {remote}/{branch} ({base[:8]} vs {tip[:8]}) and "
                    f"the fetch failed ({ferr[:120]}). The check below ran against the "
                    "OLDER ref, so it can miss anything published since.")
        elif tip_state == "no-ref":
            result.base_confirmed = "no-ref"
            notes.append(f"note: could not confirm this copy of {relpath} is current -- "
                         f"{remote} reports no refs/heads/{branch}. Checked against the "
                         "local ref only.")
        else:
            result.base_confirmed = "unreachable"
            notes.append(f"note: could not confirm this copy of {relpath} is current -- "
                         f"`git ls-remote {remote}` did not answer. This is a NETWORK "
                         "failure, not evidence of a stale checkout, so the local check "
                         f"against {ref} stands on its own.")

    result.notes = notes
    return result


def _evaluate(runner, root: str, relpath: str, base: str, notes: list[str]) -> Freshness:
    """The staleness question itself, against one resolved origin/main commit."""
    base_blob = _rev(runner, root, f"{base}:{relpath}")
    local_blob = None
    rc, out, _ = _git(runner, root, "hash-object", os.path.join(root, relpath))
    if rc == 0 and out:
        local_blob = out.split()[0]

    if base_blob is None:
        notes.append(f"note: {relpath} does not exist on origin/main; nothing to be "
                     "behind. Answering.")
        return Freshness("new", False, base_confirmed="skipped",
                         local_blob=local_blob, base_blob=None)

    rc, mb, _ = _git(runner, root, "merge-base", "HEAD", base)
    if rc != 0 or not mb:
        # No merge base (shallow clone, unrelated history): fall back to comparing
        # the working file itself. Weaker -- it cannot tell a legitimate local edit
        # from staleness -- so it only NOTES, it does not refuse.
        if local_blob and local_blob != base_blob:
            notes.append(
                f"note: could not establish whether {relpath} is current -- no merge "
                f"base with origin/main (shallow clone?). The file differs from "
                "origin/main's copy, which may be a local edit or may be staleness. "
                "Answering anyway.")
        return Freshness("unknown", False, base_confirmed="skipped",
                         local_blob=local_blob, base_blob=base_blob)

    mb_blob = _rev(runner, root, f"{mb}:{relpath}")

    if mb_blob != base_blob:
        notes.append(
            f"note: {relpath} is STALE. origin/main carries a version of this file "
            f"(blob {base_blob[:12]}) that this checkout has not incorporated: at its "
            f"merge base with origin/main ({mb[:8]}) the file is "
            f"{(mb_blob or 'absent')[:12]}. This copy predates a published change to "
            "itself, so its answer cannot be trusted.")
        notes.append(_remedy(relpath))
        return Freshness("stale", True, base_confirmed="skipped",
                         local_blob=local_blob, base_blob=base_blob)

    if local_blob and local_blob != base_blob:
        notes.append(
            f"note: the running {relpath} differs from origin/main's copy, but "
            "origin/main has not moved this file since this checkout branched -- a "
            "branch that edits the tool, not a stale one. Answering.")

    return Freshness("current", False, base_confirmed="skipped",
                     local_blob=local_blob, base_blob=base_blob)
