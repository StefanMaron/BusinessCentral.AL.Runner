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

The two unknowns (#3296)
------------------------
"Could not establish" is not one situation, and resolving all of it toward an
answer is how a guard reaches a verdict with its own safety check skipped. What
separates them is whether anything OUTSIDE git vouches for the running code:

  detached   the file is not in a git repository. The caller extracted it and
             put it there -- that is the remedy this module prints, and a remedy
             that refuses itself is not a remedy. Provenance is the caller's act.
  identical  no merge base (shallow clone), but the file is byte-identical to
             origin/main's blob. Identity is provenance; the history is beside
             the point once the bytes match.
  unvouched  everything else. Untracked inside a repository; differing from
             origin/main with no merge base to judge by; or no origin/main
             ANYWHERE -- not locally and not from the remote when asked. Nothing
             here says the copy is current, and its age is unknowable from git.

A missing LOCAL origin/main ref is deliberately NOT in that list, because it is
not the same fact as "origin/main is out of reach". GitHub Actions checks out
with fetch-depth 1 and a refspec covering only the branch under test, so
refs/remotes/origin/main does not exist on any CI run -- while the remote answers
fine. Treating the two as one refused every CI run (caught in review on the first
version of this fix). The remote is asked before anything is refused, via
FETCH_HEAD, which creates and moves no ref.

Only "unvouched" refuses. That keeps the temp-directory recipe working, which
matters because it is the ONLY route open to a copy older than this check.
"""
from __future__ import annotations

import os
import re
import subprocess
from dataclasses import dataclass, field

_SHA_LINE = re.compile(r"^([0-9a-f]{40})\s+(\S+)$")
# A remote NAME. git allows a lot here, but nothing that looks like a URL or a
# path, which is the confusion worth catching (see assess).
_REMOTE_NAME = re.compile(r"^[A-Za-z0-9._-]+$")


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
      "unknown"  could not be established. Whether that refuses depends on
                 `provenance` below -- see the module docstring.

    provenance: only meaningful when state is "unknown". Which of the two
    unknowns this is: whether anything outside git vouches for the running code.
      "detached"   the file is not inside a git repository at all, so the caller
                   put it where it is. That is the documented extract-to-/tmp
                   recipe, whose provenance IS the guarantee. NOT refused.
      "identical"  no merge base (shallow clone), but the file is byte-identical
                   to origin/main's blob. That identity is the guarantee. NOT
                   refused.
      "unvouched"  nothing vouches for it: tracked in a repository with no
                   origin/main, untracked inside one, or differing from
                   origin/main with no merge base to judge by. REFUSED.
      "n/a"        state is not "unknown".

    base_confirmed:
      "confirmed"          the local origin/main ref matches the remote's main.
      "refreshed"          it did not, and a fetch brought it up to date.
      "behind-unfetchable" it did not, and the fetch failed.
      "unreachable"        the remote could not be reached at all.
      "no-ref"             the remote has no refs/heads/main.
      "fetched-direct"     there was no local refs/remotes/<remote>/<branch>, so the
                           comparison base came from the remote via FETCH_HEAD. It
                           needs no further confirmation: it IS the remote's answer.
      "skipped"            remote_check was off, or the answer was already stale.
    """

    state: str
    refuse: bool
    notes: list[str] = field(default_factory=list)
    base_confirmed: str = "skipped"
    provenance: str = "n/a"
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


def _fetch_head(runner, root: str, remote: str, branch: str, *,
                timeout: int | None = 30) -> tuple[str | None, str]:
    """`origin/main`'s commit via FETCH_HEAD, without creating or moving any ref.

    For a checkout that has no refs/remotes/<remote>/<branch> -- the shape
    actions/checkout produces, and the shape a never-fetched clone has. Returns
    (sha, why): sha is None when the remote would not answer, and `why` then says
    what happened, because "could not reach the remote" and "this copy is stale"
    are different facts and the caller has to be able to say which it hit.

    `git fetch <remote> <branch>` with no refspec updates FETCH_HEAD only. That
    matters: this module is a read, and a guard that quietly reorganises the
    caller's refs to answer a question is doing something the caller did not ask
    for. Deepening a shallow clone was the alternative and is rejected for the
    same reason -- it rewrites the object store to answer a question that
    FETCH_HEAD already answers.
    """
    # `--refmap=` with an explicit source ref suppresses the configured refspec,
    # so NOTHING under refs/remotes/ is created or moved -- only FETCH_HEAD.
    # Without it git applies remote.origin.fetch anyway and helpfully recreates
    # refs/remotes/<remote>/<branch>, which a later call then reads as a local
    # ref and trusts. Measured: that made a genuinely stale file read as current
    # on the second call, because the ref it resurrected was already behind.
    rc, _, err = _git(runner, root, "fetch", "--quiet", "--refmap=",
                      remote, f"refs/heads/{branch}", timeout=timeout)
    if rc != 0:
        return None, (err or "fetch failed")[:120]
    sha = _rev(runner, root, "FETCH_HEAD")
    if not sha:
        return None, "fetch succeeded but FETCH_HEAD could not be resolved"
    return sha, ""


def _is_shallow(runner, root: str) -> bool:
    """True for a shallow clone -- the only shape deepening can help."""
    rc, out, _ = _git(runner, root, "rev-parse", "--is-shallow-repository")
    return rc == 0 and out.strip() == "true"


# Deepening steps. Bounded on purpose: a branch point is normally a few tens of
# commits back, and an unbounded --unshallow on a large repository is a very
# different operation from the ~8s measured here. If none of these reaches a
# merge base, the honest answer is that this checkout cannot establish it.
_DEEPEN_STEPS = (100, 500)


def _deepen(runner, root: str, remote: str, branch: str, *,
            timeout: int | None = 30) -> bool:
    """Pull in history so a shallow clone's grafted boundary stops lying.

    Unconditional, unlike _deepen_for_merge_base below: a shallow clone always
    HAS a merge base -- git reports the graft point, which for a freshly fetched
    tip is that tip itself -- so "stop as soon as one resolves" would stop
    immediately, on the wrong answer. Measured: before deepening, mb ==
    FETCH_HEAD and a stale file reads as current; after, the comparison is
    truthful in both directions.
    """
    rc, _, _ = _git(runner, root, "fetch", "--quiet", "--refmap=",
                    f"--deepen={_DEEPEN_STEPS[0]}", remote, f"refs/heads/{branch}",
                    timeout=timeout)
    return rc == 0


def _deepen_for_merge_base(runner, root: str, remote: str, branch: str,
                           base: str, *, timeout: int | None = 30) -> str | None:
    """Deepen a shallow clone until a merge base with `base` resolves; else None.

    Adds history objects only -- measured on a real depth-1 CI clone: HEAD
    unchanged, every ref unchanged, working tree clean. Nothing the repository
    POINTS AT is altered, which is the property that makes this acceptable in a
    read-only guard where creating or moving a ref would not be.

    That property depends entirely on `--refmap=`, which is why every fetch in
    this module carries it. Without it git applies the configured
    remote.origin.fetch refspec and RECREATES refs/remotes/<remote>/<branch> as
    a side effect. Measured consequence: a second assess() call then found that
    resurrected ref, took the local-ref path instead of this one, and trusted a
    ref that was already behind -- reporting a genuinely stale file as current.
    A guard that silently rewrites the state it is about to read is worse than
    one that refuses.
    """
    for depth in _DEEPEN_STEPS:
        rc, _, _ = _git(runner, root, "fetch", "--quiet", "--refmap=",
                        f"--deepen={depth}", remote, f"refs/heads/{branch}",
                        timeout=timeout)
        if rc != 0:
            return None
        rc, mb, _ = _git(runner, root, "merge-base", "HEAD", base)
        if rc == 0 and mb:
            return mb.strip()
    return None


def _detached(path: str, directory: str) -> Freshness:
    """Outside a repository: the caller's own extraction is the provenance.

    This is the one unknown that answers. See the module docstring; the note is
    loud because the guarantee rests entirely on the caller having followed the
    documented recipe, and nobody but the caller can check that.
    """
    return Freshness("unknown", False, [
        f"note: {os.path.basename(path)} is not inside a git repository "
        f"({directory}), so its freshness cannot be checked here. Answering on "
        "the caller's PROVENANCE: this is the documented recipe of extracting "
        "origin/main's copy to a temp directory, and that extraction is the "
        "guarantee. If you did NOT just extract it from origin/main, this answer "
        "is not vouched for by anything."], provenance="detached")


def _unvouched(note: str, path: str, relpath: str | None = None) -> Freshness:
    """Nothing vouches for this copy, so there is no answer to give (#3296).

    Refusing rather than noting is the whole point: a note next to a verdict is
    read as a caveat on a result, and the result is what the caller acts on.
    """
    return Freshness("unknown", True,
                     [note, _remedy(relpath or f"tools/{os.path.basename(path)}")],
                     provenance="unvouched")


def assess(path: str, *, remote_check: bool = True, remote: str = "origin",
           branch: str = "main", runner=None, timeout: int = 20) -> Freshness:
    """Whether the copy of `path` on disk is behind origin/<branch> on that file.

    `remote` is a configured remote NAME, because the comparison is made against
    `refs/remotes/<remote>/<branch>`. A URL would resolve no such ref and answer
    "unknown", which dresses a caller's mistake up as a fact about the
    environment -- so it is refused outright instead.
    """
    if not _REMOTE_NAME.match(remote):
        raise ValueError(
            f"remote must be a configured remote name, not {remote!r}: this "
            "compares against refs/remotes/<remote>/<branch>, and a URL resolves "
            "no such ref.")
    runner = runner or _default_runner
    ref = f"refs/remotes/{remote}/{branch}"

    path = os.path.abspath(path)
    directory = os.path.dirname(path)
    rc, root, err = _git(runner, None, "-C", directory, "rev-parse", "--show-toplevel")
    if rc != 0 or not root:
        # "Not a repository" and "git is not installed" used to share this branch
        # and its answer. They are opposite facts: the first is the documented
        # extract-to-/tmp recipe, whose provenance is the caller's own act; the
        # second means the check could not run at all, which vouches for nothing.
        # git says "not a git repository" on stderr; _default_runner puts the
        # OSError text there when the binary is missing.
        if "not a git repository" in err.lower():
            return _detached(path, directory)
        return _unvouched(
            f"note: REFUSING to vouch for {os.path.basename(path)} -- git could not "
            f"be run to check it ({err[:120]}). Nothing here has established that "
            "this copy carries the latest fixes.", path)

    rc, relpath, _ = _git(runner, root, "ls-files", "--full-name", "--", path)
    relpath = relpath.splitlines()[0].strip() if relpath else ""
    if rc != 0 or not relpath:
        # Inside a repository but untracked. Not the temp-directory recipe (that
        # lands OUTSIDE a repository), and being untracked is exactly what makes
        # the file's age unknowable from git.
        return _unvouched(
            f"note: REFUSING to vouch for {os.path.basename(path)} -- it is not a "
            f"tracked file in {root}, so git can say nothing about its age. If you "
            "meant the extract-to-a-temp-directory recipe, extract to a directory "
            "that is not inside a repository.", path)

    notes: list[str] = []
    base = _rev(runner, root, ref)
    if not base:
        # No LOCAL origin/main ref. That is not the same fact as "origin/main is
        # out of reach", and conflating them refuses every CI run: GitHub Actions
        # checks out with fetch-depth 1 and a refspec narrowed to the PR branch,
        # so refs/remotes/origin/main does not exist -- while the remote itself
        # answers perfectly well.
        #
        # So ASK THE REMOTE before giving up. `git fetch <remote> <branch>` with
        # no refspec writes FETCH_HEAD and creates or moves no branch ref, so
        # this stays a read: the caller's repository is not reorganised by a
        # tool whose whole job is to answer a question about it.
        # Deepen FIRST when the clone is shallow. A shallow clone's grafted
        # boundary makes git report the freshly-fetched tip as its own merge
        # base -- measured: mb == FETCH_HEAD, so mb_blob == base_blob and a
        # genuinely stale file reads as current. Deepening dissolves the graft
        # and the comparison becomes truthful again (measured both ways).
        # Without this the fallback would trade a refusal for a rubber stamp,
        # which is worse than refusing.
        if _is_shallow(runner, root):
            _deepen(runner, root, remote, branch, timeout=timeout)
        base, why = _fetch_head(runner, root, remote, branch, timeout=timeout)
        if not base:
            # Genuinely nothing to compare against: no local ref AND the remote
            # would not answer. This is the case #3296 is about -- a long-lived
            # clone that never fetched -- and it is now reached only when the
            # remote has also been asked and declined.
            return _unvouched(
                f"note: REFUSING to vouch for {relpath} -- this repository has no {ref}, "
                f"and {remote}/{branch} could not be reached to compare against either "
                f"({why}). Nothing has established that this copy carries the latest "
                "fixes.", path, relpath=relpath)
        notes.append(
            f"note: this repository has no {ref}, so {relpath} was compared against "
            f"{remote}/{branch} fetched directly ({base[:8]}). Normal for a shallow "
            "CI checkout, whose refspec covers only the branch under test.")

    # True when `base` came from the remote just now rather than from a local
    # ref. The confirmation block below exists to close the window in which a
    # SHARED local ref has drifted from the remote; a base fetched seconds ago
    # has no such window, and re-deriving it from `ref` would read None in a CI
    # checkout and report a healthy run as "behind-unfetchable".
    base_from_remote = not _rev(runner, root, ref)

    result = _evaluate(runner, root, relpath, base, notes, remote=remote,
                       branch=branch, timeout=timeout)
    if base_from_remote:
        result.base_confirmed = "fetched-direct"

    # The remote confirmation runs only when the local answer was NOT already
    # stale: a refusal should be instant and should not depend on the network.
    if result.state != "stale" and remote_check and not base_from_remote:
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
                result = _evaluate(runner, root, relpath, new_base, notes,
                                   remote=remote, branch=branch, timeout=timeout)
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


def _evaluate(runner, root: str, relpath: str, base: str, notes: list[str],
              *, remote: str = "origin", branch: str = "main",
              timeout: int | None = 30) -> Freshness:
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
        # No merge base (shallow clone, unrelated history), so the branch-point
        # comparison is unavailable. origin/main IS resolvable here, though, so
        # the file can be compared to it directly -- and that splits the case in
        # two rather than leaving it one blanket unknown (#3296).
        if local_blob and local_blob == base_blob:
            # Byte-identical to origin/main's copy. The history is beside the
            # point once the bytes match: this IS the published version.
            notes.append(
                f"note: no merge base with origin/main for {relpath} (shallow "
                "clone?), but the file is byte-identical to origin/main's copy, "
                "which is what the check would have established anyway. Answering.")
            return Freshness("unknown", False, base_confirmed="skipped",
                             local_blob=local_blob, base_blob=base_blob,
                             provenance="identical")
        # The file DIFFERS and there is no merge base, so the question is
        # genuinely open: a branch that edits the tool and a stale copy look
        # identical from here. Before refusing, try to make it answerable --
        # a shallow clone has no merge base because it has no history, and
        # history is fetchable.
        #
        # Deepening is a bigger step than the FETCH_HEAD read above, so it is
        # last, bounded, and only on this branch -- never when the blobs already
        # match. Measured on a real depth-1 CI clone of this repository: ~8s,
        # HEAD unchanged, every ref unchanged, working tree clean. It only adds
        # history objects: the repository learns more and points at nothing new.
        # That is why it is acceptable where creating or moving a ref would not
        # be -- the objection to mutation is about what a repository POINTS AT,
        # which this does not touch.
        if _is_shallow(runner, root):
            mb = _deepen_for_merge_base(runner, root, remote, branch, base,
                                        timeout=timeout)
            if mb:
                notes.append(
                    f"note: no merge base with origin/main for {relpath} in a shallow "
                    f"checkout, so history was deepened until one resolved ({mb[:8]}). "
                    "No ref was created or moved.")
                return _judge_against_merge_base(runner, root, relpath, mb, base_blob,
                                                 local_blob, notes)

        notes.append(
            f"note: REFUSING to vouch for {relpath} -- no merge base with "
            "origin/main (shallow clone?), and the file differs from origin/main's "
            "copy. That may be a local edit or may be staleness, and without a "
            "merge base there is nothing here that can tell them apart.")
        notes.append(_remedy(relpath))
        return Freshness("unknown", True, base_confirmed="skipped",
                         local_blob=local_blob, base_blob=base_blob,
                         provenance="unvouched")

    return _judge_against_merge_base(runner, root, relpath, mb, base_blob,
                                     local_blob, notes)


def _judge_against_merge_base(runner, root: str, relpath: str, mb: str,
                              base_blob: str, local_blob: str | None,
                              notes: list[str]) -> Freshness:
    """The staleness verdict itself, given a resolved merge base.

    Shared by the ordinary path and the deepened-shallow-clone path so the two
    cannot drift: a second copy of this comparison is a second place for the
    definition of "stale" to be wrong.
    """
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
