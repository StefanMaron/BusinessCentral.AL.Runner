#!/usr/bin/env python3
"""Read a pull request's newest REVIEW VERDICT and say whether it still applies.

Claim: a review verdict is only actionable if a machine can find it and tell
whether it belongs to the code that is about to merge. Review here happens in
comments, in at least five header styles, so nothing could extract a verdict and
a FIX-FIRST could be armed for auto-merge by mistake
(https://fbakkensen.github.io/al-runner-retro/#e-11 has the counts).

So `.claude/agents/reviewer.md` now requires one fixed last line per review
comment, and this tool is the reader of it. The grammar lives here in `GRAMMAR`
and is pinned against reviewer.md by `tools/test_pr_verdict.py`, so the prose
and the parser cannot drift apart.

    tools/pr-verdict.py <PR>            # read the newest verdict, judge it
    tools/pr-verdict.py --stamp <PR>    # print the line's tail, to paste
    tools/pr-verdict.py --stamp <PR> --head <sha> [--patch <id>]
                                        # ...in a session with no `gh`

Reading a verdict needs `gh`: it is a GitHub query with no local equivalent, so
in an MCP-only session (`github-access.md`) that half is unavailable and the
tool says so instead of guessing. Stamping does not -- pass the head SHA you
read through `mcp__github__pull_request_read` and the fingerprint is computed
from your own checkout with `git`.

Exit codes
----------
    0  the newest verdict is MERGE, on THIS head and THIS patch-id -- and this
       is the only path that returns 0
    1  the newest verdict is FIX-FIRST or HOLD
    2  a MERGE verdict exists but the head or the patch-id has moved since; the
       output says which
    3  no verdict comment at all, a malformed verdict line, the PR or the API
       could not be read, or this copy is behind origin/main

Two traps, both load-bearing:

* The patch field is a fingerprint of `git diff <base>...<head>` with hunk
  headers and blob hashes removed and everything else kept byte for byte, so it
  survives a clean rebase (the head SHA moves, the fingerprint does not) and is
  **whitespace-sensitive on purpose** -- `git patch-id` normalises whitespace
  away and answers the same id for a line moved into or out of a Python
  conditional, which is a behaviour change. Anything that moves the fingerprint
  costs a full review, which is the safe direction. See `canonical_diff`.
* A verdict must be the LAST non-empty line of its comment, so an agent
  signature belongs above it. A trailing "thanks" makes the comment carry no
  verdict at all, which is exit 3 -- never a silent green.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    import agent_stdio as _stdio
except Exception:  # pragma: no cover - a copy detached from its sibling module
    _stdio = None
    print("note: tools/agent_stdio.py could not be imported alongside this copy of "
          "pr-verdict.py -- the verdict line carries em dashes, which a cp1252 "
          "console cannot print. Extract that file too.", file=sys.stderr)
if _stdio is not None:
    _stdio.enable_utf8_stdio()
try:
    import agent_self_freshness as _freshness
except Exception:  # pragma: no cover - a copy detached from its sibling module
    _freshness = None

REPO = "StefanMaron/BusinessCentral.AL.Runner"

# The documented line, verbatim in .claude/agents/reviewer.md. Pinned by
# tools/test_pr_verdict.py so a reviewer cannot be told one shape while the
# parser reads another.
GRAMMAR = ("Verdict: MERGE|FIX-FIRST|HOLD (<reason, only for FIX-FIRST/HOLD>) "
           "— head <full 40-char sha> — patch <diff fingerprint, 12 hex> "
           "— kind: full|arm-check")

# Who may hand down a verdict. GitHub reports the commenter's relationship to
# the repository on every comment, and this is a PUBLIC repository -- the head
# SHA and the patch-id are both public, so anyone can compose a well-formed
# MERGE line for the current code. Write access is the boundary; the login is
# not, because the maintainer's verdicts must count too.
TRUSTED_ASSOCIATIONS = ("OWNER", "MEMBER", "COLLABORATOR")

VERDICT_RE = re.compile(
    r"^Verdict: (?P<decision>MERGE|FIX-FIRST|HOLD)"
    r"(?: \((?P<reason>.+?)\))?"
    r" — head (?P<head>[0-9a-f]{40})"
    r" — patch (?P<patch>[0-9a-f]{12})"
    r" — kind: (?P<kind>full|arm-check)$")


class Verdict:
    __slots__ = ("decision", "reason", "head", "patch", "kind", "line")

    def __init__(self, decision, reason, head, patch, kind, line):
        self.decision = decision
        self.reason = reason
        self.head = head
        self.patch = patch
        self.kind = kind
        self.line = line

    def __repr__(self) -> str:
        return (f"Verdict({self.decision}, reason={self.reason!r}, "
                f"head={self.head[:8]}, patch={self.patch}, kind={self.kind})")


class Result:
    """One judgement, with the two "did it move" facts a caller branches on."""

    def __init__(self, exit_code: int, lines: list[str], verdict: Verdict | None = None,
                 head: str | None = None, patch: str | None = None,
                 head_ok: bool = False, patch_ok: bool = False,
                 stderr_lines: list[str] | None = None,
                 head_moved_during_check: bool = False):
        # True only when the caller named the head it had already checked and
        # the PR has moved since: a different fact from "the verdict is stale",
        # and the caller must abandon its pass rather than restate it.
        self.head_moved_during_check = head_moved_during_check
        self.exit_code = exit_code
        self.lines = lines
        # Notes rather than verdict: printed to stderr so a caller capturing
        # stdout gets the judgement and nothing else.
        self.stderr_lines = stderr_lines or []
        self.verdict = verdict
        self.head = head
        self.patch = patch
        self.head_ok = head_ok
        self.patch_ok = patch_ok


def stamp(head: str, patch: str, kind: str = "full") -> str:
    """The tail of a verdict line: what `--stamp` prints and a reviewer pastes."""
    return f"— head {head} — patch {patch} — kind: {kind}"


def parse_verdict(body: str) -> tuple[Verdict | None, str | None]:
    """(verdict, malformed-note) from a comment body's LAST non-empty line.

    Malformed and absent are reported separately because they need different
    remedies -- the same split `.github/scripts/check_corpus_linkage.sh` makes
    for a `Corpus-PR:` line that carries no URL (#3330).
    """
    lines = [l.rstrip() for l in (body or "").splitlines()]
    lines = [l for l in lines if l.strip()]
    if not lines:
        return None, None
    last = lines[-1].strip()
    m = VERDICT_RE.match(last)
    if not m:
        # "Verdict:" ANYWHERE in the line, not just at its start: a bolded
        # marker (`**Verdict: MERGE**`) is the shape a reviewer writes by
        # accident, and reporting it as absent sends them looking for a comment
        # they already posted.
        if "Verdict:" in last:
            return None, f"malformed verdict line: {last!r}"
        # ...and anywhere in the COMMENT, not just in its last line. A verdict
        # followed by a signature is the single most likely way to write one
        # wrong, and treating that comment as carrying no verdict at all leaves
        # an OLDER verdict standing as the newest actionable one -- a newer HOLD
        # then reads as the earlier MERGE.
        for l in lines[:-1]:
            if VERDICT_RE.match(l.strip()) or "Verdict:" in l:
                return None, ("malformed verdict comment: a verdict line is present "
                              f"but {last!r} follows it -- the verdict must be the "
                              "LAST non-empty line, with the signature above it")
        return None, None
    decision, reason = m.group("decision"), m.group("reason")
    if decision == "MERGE" and reason is not None:
        return None, ("malformed verdict line: MERGE carries no reason, so "
                      f"{reason!r} makes this line unreadable as a verdict")
    if decision != "MERGE" and not reason:
        return None, (f"malformed verdict line: {decision} must carry a reason "
                      "in parentheses")
    return Verdict(decision, reason, m.group("head"), m.group("patch"),
                   m.group("kind"), last), None


def newest_verdict(comments: list[dict]) -> tuple[Verdict | None, str | None]:
    """The newest parseable verdict, and any malformed line NEWER than it.

    The pair matters: a malformed line above a readable verdict means the newest
    thing the reviewer wrote is unreadable, so the older verdict is not the
    current answer. `evaluate` refuses on that pair rather than judging on the
    older one -- otherwise a FIX-FIRST that forgot its reason would leave an
    earlier MERGE standing, which is the exact mis-arming this grammar exists to
    stop (https://fbakkensen.github.io/al-runner-retro/#e-11).
    """
    # An edited comment carries its new text under its ORIGINAL created_at, so
    # ordering on that alone reads a corrected verdict as older than the one it
    # corrects. Newest of the two timestamps is what "the reviewer's latest
    # word" means.
    ordered = sorted(comments or [],
                     key=lambda c: (max(str(c.get("created_at") or ""),
                                        str(c.get("updated_at") or "")),
                                    c.get("id") or 0),
                     reverse=True)
    malformed = None
    for c in ordered:
        v, bad = parse_verdict(c.get("body") or "")
        if v is not None:
            return v, malformed
        if bad and malformed is None:
            malformed = bad
    return None, malformed


def filter_trusted(comments: list[dict]) -> tuple[list[dict], list[str]]:
    """(comments that may carry a verdict, notes about the ones dropped).

    A comment with no `author_association` at all is dropped too: the field is
    on every GitHub comment payload, so its absence means the payload did not
    come from GitHub, and defaulting an unknown author to trusted is the whole
    hole this closes.
    """
    kept, dropped = [], {}
    for c in comments or []:
        assoc = str(c.get("author_association") or "NONE").upper()
        if assoc in TRUSTED_ASSOCIATIONS:
            kept.append(c)
        else:
            login = (c.get("user") or {}).get("login") or "?"
            dropped[f"{login} ({assoc})"] = dropped.get(f"{login} ({assoc})", 0) + 1
    notes = [f"note: ignoring {n} comment(s) from {who} -- a verdict counts only "
             f"from {', '.join(TRUSTED_ASSOCIATIONS)}"
             for who, n in sorted(dropped.items())]
    return kept, notes


def _default_runner(argv: list[str], input: str | None = None) -> tuple[int, str]:
    p = subprocess.run(argv, capture_output=True, text=True, encoding="utf-8",
                       errors="replace", input=input)
    out = (p.stdout or "") + (p.stderr or "")
    # mise prints a banner on stdout, which otherwise lands inside the JSON.
    out = "\n".join(l for l in out.split("\n") if not l.startswith("mise "))
    return p.returncode, out.strip()


def _default_brunner(argv: list[str], input: bytes | None = None) -> tuple[int, bytes]:
    """Bytes in, bytes out: a diff is piped to patch-id without being re-encoded."""
    p = subprocess.run(argv, capture_output=True, input=input)
    return p.returncode, p.stdout or b""


def load_json(text: str):
    """A gh --paginate body: one array, or several concatenated ones."""
    try:
        return json.loads(text)
    except ValueError:
        pass
    out, decoder, idx = [], json.JSONDecoder(), 0
    while idx < len(text):
        while idx < len(text) and text[idx] in " \t\r\n":
            idx += 1
        if idx >= len(text):
            break
        obj, idx = decoder.raw_decode(text, idx)
        out.extend(obj if isinstance(obj, list) else [obj])
    return out


_HUNK = re.compile(rb"^@@ -\d+(?:,\d+)? \+\d+(?:,\d+)? @@.*$", re.MULTILINE)
_INDEX = re.compile(rb"^index [0-9a-f]+\.\.[0-9a-f]+.*$", re.MULTILINE)


def canonical_diff(diff: bytes) -> bytes:
    """A diff reduced to what a rebase does NOT change, byte-exact otherwise.

    Two properties are needed at once and `git patch-id` gives only the first:

    * **rebase-invariant** -- a clean rebase shifts line numbers and blob hashes
      without changing the change, so hunk headers and `index` lines are dropped.
    * **whitespace-sensitive** -- `git patch-id` normalises whitespace away, so
      it answers the same id for two diffs that differ only in indentation. In
      Python that is a behaviour change (a line moved into or out of a
      conditional), and a fingerprint that cannot see it would license an
      arm-check on a diff that is not the reviewed one. Content lines are
      therefore kept byte for byte.
    """
    out = _HUNK.sub(b"@@", diff)
    return _INDEX.sub(b"index", out)


def compute_patch_fingerprint(base_ref: str, head: str, *, brunner=_default_brunner,
                              repo_dir: str | None = None) -> tuple[str | None, str]:
    """(first 12 hex of sha256(canonical diff), note). None when it cannot be had.

    An empty diff is never a value: `git diff` printing nothing means the range
    did not resolve as often as it means an empty change, and a caller that
    treats "" as a fingerprint would compare it equal to another "".
    """
    pre = ["git"] + (["-C", repo_dir] if repo_dir else [])
    rc, diff = brunner(pre + ["diff", f"{base_ref}...{head}"])
    if rc != 0:
        return None, f"git diff {base_ref}...{head} failed (rc={rc})"
    if not (diff or b"").strip():
        return None, ("git diff produced nothing -- the diff was empty or the "
                      "range did not resolve")
    return hashlib.sha256(canonical_diff(diff)).hexdigest()[:12], ""


def _remote_for(repo: str, runner) -> str | None:
    """The configured remote whose URL is `repo` (this box has three)."""
    rc, out = runner(["git", "remote", "-v"])
    if rc != 0:
        return None
    want = repo.lower()
    for line in out.splitlines():
        parts = line.split()
        if len(parts) >= 2 and want in parts[1].lower().removesuffix(".git"):
            return parts[0]
    return None


def patch_id_for_pr(repo: str, pr: str, head: str, base: str,
                    runner, brunner=_default_brunner) -> tuple[str | None, str]:
    """Fetch what the range needs, then hash it. Network, so it is injectable."""
    remote = _remote_for(repo, runner)
    if not remote:
        return None, f"no configured git remote points at {repo}"
    # A fetch that failed leaves whatever refs were already on disk, and the
    # comparison then silently answers about older code. Read both return codes.
    for spec in (f"refs/pull/{pr}/head", base):
        rc, out = runner(["git", "fetch", "--quiet", remote, spec])
        if rc != 0:
            return None, (f"git fetch {remote} {spec} failed (rc={rc}) -- refusing to "
                          f"compare against cached refs: {out.splitlines()[0][:120] if out else ''}")
    base_ref = f"{remote}/{base}"
    rc, _ = runner(["git", "rev-parse", "--verify", "--quiet", base_ref + "^{commit}"])
    if rc != 0:
        return None, f"{base_ref} does not resolve here"
    rc, _ = runner(["git", "rev-parse", "--verify", "--quiet", head + "^{commit}"])
    if rc != 0:
        return None, f"the head commit {head[:8]} is not in this checkout"
    return compute_patch_fingerprint(base_ref, head, brunner=brunner)


def read_pr(repo: str, pr: str, runner) -> dict | None:
    rc, out = runner(["gh", "pr", "view", pr, "--repo", repo, "--json",
                      "headRefOid,baseRefName,headRefName,body"])
    if rc != 0 or not out:
        return None
    try:
        return json.loads(out)
    except ValueError:
        return None


def read_comments(repo: str, pr: str, runner) -> list | None:
    # Issue comments only: pull-request REVIEW comments are inline code notes,
    # and review objects are 1 of 796 here, so a verdict is always an issue
    # comment on the PR.
    rc, out = runner(["gh", "api", f"repos/{repo}/issues/{pr}/comments",
                      "--paginate"])  # the payload carries author_association
    if rc != 0:
        return None
    try:
        return load_json(out or "[]")
    except ValueError:
        return None


def evaluate(repo: str, pr: str, *, runner=None, patch_id_of=None,
             expect_head: str | None = None) -> Result:
    """The judgement, with no printing and no argparse, so tests can drive it."""
    runner = runner or _default_runner
    patch_id_of = patch_id_of or (
        lambda repo, pr, head, base, runner: patch_id_for_pr(repo, pr, head, base, runner))
    lines: list[str] = []
    try:
        pr_json = read_pr(repo, pr, runner)
        if not pr_json or not pr_json.get("headRefOid"):
            lines.append(f"could not read PR #{pr} on {repo} -- no verdict.")
            return Result(3, lines)
        head = pr_json["headRefOid"]
        base = pr_json.get("baseRefName") or "main"
        if expect_head and head != expect_head:
            # The caller checked other things against expect_head. Answering
            # about a different commit would let a multi-check pass mix two
            # heads and stamp the newer one as reviewed.
            lines.append(f"the head of PR #{pr} moved during this check: "
                         f"{expect_head[:12]} -> {head[:12]}. Nothing is judged; "
                         "start the pass again against the new head.")
            return Result(2, lines, head=head, head_moved_during_check=True)

        comments = read_comments(repo, pr, runner)
        if comments is None:
            lines.append(f"could not read the comments on PR #{pr} -- no verdict.")
            return Result(3, lines)

        comments, untrusted = filter_trusted(comments)
        verdict, malformed = newest_verdict(comments)
        if verdict is not None and malformed:
            lines.append(malformed)
            lines.append("That line is NEWER than the newest readable verdict "
                         f"({verdict.decision} on {verdict.head[:12]}), so the readable "
                         "one is not the current answer -- refusing to judge on it.")
            lines.append("  " + GRAMMAR)
            return Result(3, lines, stderr_lines=untrusted)
        if verdict is None:
            if malformed:
                lines.append(malformed)
                lines.append("A verdict line exists but does not match the grammar, so "
                             "it is NOT a verdict. The grammar is:")
            else:
                lines.append(f"PR #{pr} carries no verdict comment "
                             f"({len(comments)} comment(s) read). The grammar is:")
            lines.append("  " + GRAMMAR)
            return Result(3, lines, stderr_lines=untrusted)

        patch, note = patch_id_of(repo, pr, head, base, runner)
        if not patch:
            lines.append(f"could not compute the patch-id for PR #{pr}: {note}")
            lines.append("Without it a MERGE verdict cannot be tied to a diff -- no verdict.")
            return Result(3, lines, verdict=verdict, head=head,
                          stderr_lines=untrusted)

        head_ok = verdict.head == head
        patch_ok = verdict.patch == patch
        lines.append(f"newest verdict on PR #{pr}: {verdict.decision}"
                     + (f" ({verdict.reason})" if verdict.reason else "")
                     + f", kind {verdict.kind}")
        lines.append(f"  reviewed head  {verdict.head[:12]}   current head  {head[:12]}"
                     + ("" if head_ok else "   MOVED"))
        lines.append(f"  reviewed patch {verdict.patch}       current patch {patch}"
                     + ("" if patch_ok else "   MOVED"))

        if verdict.decision != "MERGE":
            lines.append(f"{verdict.decision} — do not arm auto-merge.")
            return Result(1, lines, verdict, head, patch, head_ok, patch_ok, untrusted)
        if head_ok and patch_ok:
            lines.append("MERGE on the current head and the current patch.")
            return Result(0, lines, verdict, head, patch, True, True, untrusted)
        moved = ",".join(x for x, ok in (("head", head_ok), ("patch", patch_ok)) if not ok)
        lines.append(f"MOVED: {moved}")
        lines.append("A MERGE verdict exists, but it was given on other code. "
                     + ("The patch-id is unchanged, so this is a rebase: an arm-check "
                        "pass is enough (tools/arm-check.py)."
                        if patch_ok else
                        "The diff itself changed, so a full review is owed."))
        return Result(2, lines, verdict, head, patch, head_ok, patch_ok, untrusted)
    except Exception as exc:  # a broken environment is never a verdict
        lines.append(f"could not judge PR #{pr}: {exc.__class__.__name__}: {exc}")
        return Result(3, lines)


def freshness_gate(paths: list[str], *, remote_check: bool = True) -> tuple[bool, list[str]]:
    """(refused, output). Same shape as ci-wait.py's: a stale tool is exit 3.

    Both this file and the guard module are assessed, because a stale guard is
    a stale answer one level down.
    """
    out: list[str] = []
    if _freshness is None:
        out.append("REFUSING to judge: tools/agent_self_freshness.py could not be "
                   "imported alongside this copy, so nothing established whether "
                   "this tool carries the latest fixes. This is NOT a verdict.")
        return True, out
    refused, why, confirm = False, "", remote_check
    for target in paths + [os.path.abspath(_freshness.__file__)]:
        fresh = _freshness.assess(target, remote_check=confirm)
        confirm = False  # one ls-remote for the whole set
        out.extend(fresh.notes)
        if fresh.refuse and not why:
            why = ("a STALE " if fresh.state == "stale" else "a NOT-VOUCHED-FOR ") + \
                os.path.basename(target)
        refused = refused or fresh.refuse
    if refused:
        out.append(f"REFUSING to judge from {why}. This is NOT a verdict.")
    return refused, out


def main(argv: list[str] | None = None, *, runner=None, patch_id_of=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("pr")
    ap.add_argument("--repo", default=REPO)
    ap.add_argument("--stamp", action="store_true",
                    help="print the tail of the verdict line for this PR's current "
                         "head and patch-id, for the reviewer to paste")
    ap.add_argument("--kind", choices=("full", "arm-check"), default="full",
                    help="which pass produced the verdict being stamped (default: full)")
    ap.add_argument("--head", metavar="SHA",
                    help="with --stamp: the head SHA to stamp, instead of asking gh "
                         "(for a session with no gh -- read it through "
                         "mcp__github__pull_request_read)")
    ap.add_argument("--patch", metavar="ID",
                    help="with --stamp: the 12-hex fingerprint to stamp, instead of "
                         "computing it from this checkout")
    ap.add_argument("--base", default="main", metavar="BRANCH",
                    help="with --stamp --head: the base branch the fingerprint is "
                         "taken against (default: main)")
    ap.add_argument("--no-freshness-fetch", action="store_true",
                    help="skip the ls-remote that confirms the local origin/main ref")
    args = ap.parse_args(argv)
    if (args.head or args.patch) and not args.stamp:
        ap.error("--head/--patch are inputs to --stamp; reading a verdict takes "
                 "neither, because it reports the PR's CURRENT head and fingerprint")
    if args.patch and not re.fullmatch(r"[0-9a-f]{12}", args.patch):
        ap.error("--patch must be 12 lowercase hex characters, as printed by --stamp")
    if args.head and not re.fullmatch(r"[0-9a-f]{40}", args.head):
        ap.error("--head must be a full 40-character SHA, not an abbreviation")

    # --stamp's stdout is pasted into a comment, and gets captured with $(...).
    # Anything else it prints becomes part of the verdict line, so under --stamp
    # every note goes to stderr and stdout carries exactly one line.
    notes_to = sys.stderr if args.stamp else sys.stdout
    refused, notes = freshness_gate([os.path.abspath(__file__)],
                                    remote_check=not args.no_freshness_fetch)
    for line in notes:
        print(line, file=notes_to)
    if refused:
        return 3

    runner = runner or _default_runner
    if args.stamp:
        if args.head:
            # No gh at all on this path: the caller read the head elsewhere.
            head, base = args.head, args.base
        else:
            pr_json = read_pr(args.repo, args.pr, runner)
            if not pr_json or not pr_json.get("headRefOid"):
                print(f"could not read PR #{args.pr} on {args.repo}", file=sys.stderr)
                return 3
            head = pr_json["headRefOid"]
            base = pr_json.get("baseRefName") or "main"
        if args.patch:
            patch, note = args.patch, ""
        elif args.head:
            # Local git only -- no refs/pull fetch, because that needs gh's repo
            # too; the caller's checkout must already carry the commit.
            patch, note = compute_patch_fingerprint(f"origin/{base}", head)
        else:
            patch, note = (patch_id_of(args.repo, args.pr, head, base, runner)
                           if patch_id_of else
                           patch_id_for_pr(args.repo, args.pr, head, base, runner))
        if not patch:
            print(f"could not compute the patch-id: {note}", file=sys.stderr)
            return 3
        print(stamp(head, patch, args.kind))
        return 0

    res = evaluate(args.repo, args.pr, runner=runner, patch_id_of=patch_id_of)
    for line in res.stderr_lines:
        print(line, file=sys.stderr)
    for line in res.lines:
        print(line)
    return res.exit_code


if __name__ == "__main__":
    sys.exit(main())
