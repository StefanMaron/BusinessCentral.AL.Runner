#!/usr/bin/env python3
"""Edit a pull request body behind preconditions that can actually fail.

FIRST, THOUGH: an append is almost always the wrong move
--------------------------------------------------------
If what you want is to add a note, a status update, a "rebased onto main" or a
reply to a reviewer -- POST A COMMENT. `gh pr comment <N> --body ...` is additive,
it cannot destroy anything, and it is what a reviewer reads in order. This tool
exists for genuine BODY corrections: a sentence in the body that no longer matches
the diff (a rebase moved the head and the body still says a file is untouched), a
missing closing-reference trailer, a wrong file path in the summary. #2790's body
was being edited ONLY to add a note, and that is what destroyed it.

Why this exists
---------------
A scripted edit of PR #2790's body did this:

    b = subprocess.run(["gh","pr","view",PR,"--json","body","--jq",".body"],...).stdout
    orig = b
    b = b.replace(anchor1, new1).replace(anchor2, new2)
    b += "\n\nNote: ...\n"
    print('changed' if b != orig else 'NO ANCHOR MATCHED')
    # ... then uploaded b

The fetch returned an EMPTY STRING -- the network was timing out. The replacements
matched nothing. The append ran against "". 711 bytes were uploaded over a ~4 KB
body, removing the trailer that named issue #2783, so the issue did not auto-close
on merge and had to be closed by hand. That is the mirror bug
`.claude/rules/branch-and-pr.md` records from #2046, #1642 and #1640.

The guard `print('changed' if b != orig else ...)` COULD NOT FAIL: appending always
changes the string, so it reported success on a total loss. A check that cannot fail
is worse than no check, because it converts "nobody looked" into "something looked
and was happy".

So every guard here is one that can fail, and the failure mode of the whole tool is
REFUSING TO WRITE. When in doubt it exits non-zero and explains.

The guards
----------
  self-current        this file is not behind `origin/main` on itself. An agent
                      runs the copy in its own worktree, and 40 of the 59
                      worktrees carrying this file on the development box had a
                      version that was not origin/main's (#3020). A copy that
                      predates a guard is a copy running without that guard, so
                      it refuses instead. A branch that legitimately EDITS this
                      file is not stale and is not refused.
  fetch-parsed        the body is read as JSON (`gh pr view --json body`, no --jq),
                      so an empty body is distinguishable from a failed fetch. A
                      response that does not parse, or has no `body` key, is a
                      FETCH FAILURE, never an empty body.
  fetch-nonempty      a body that came back empty is refused outright.
  fetch-plausible     ...and one shorter than --min-bytes (default 200) is refused
                      too: a truncated read is the same accident as an empty one.
  fetch-agrees        the body is read TWICE and the two reads must agree, so a
                      partial response cannot become the baseline for an edit.
  anchor-found        every --replace anchor must be found, the expected number of
                      times. A miss is an error, never a silent no-op.
  closes-survive      every closing-reference target declared in the ORIGINAL body
                      must still be declared in the new one (this is the #2790
                      damage, stated directly). --closes N additionally requires a
                      target the original may not have had yet.
  no-stray-closing    no closing keyword next to any OTHER issue number, matching
                      `pr-gate.yml`'s reject-bad-closing-references job. Failing
                      here is cheaper than failing there.
  no-large-shrink     a shrink beyond the threshold is refused; the message names
                      the threshold and how to override it.
  must-contain        --must-contain / --must-not-contain, for a claim you want to
                      keep asserting (see --check).
  verified-after      after the upload the body is RE-READ and compared to what was
                      intended. The write's exit code is not evidence: on the same
                      night a `gh pr merge` reported `dial tcp ... i/o timeout` on a
                      call that had actually succeeded, and the retry answered
                      "already merged".

Closing references fire from COMMIT MESSAGES too
------------------------------------------------
This repository squash-merges with squash_merge_commit_message=COMMIT_MESSAGES, so
the branch's commit messages become the merge commit's body and GitHub's parser
fires on them as well as on the PR body. It does not understand negation: PR #2486's
commit message said "It does not close #2479" and the merge closed #2479 anyway.
Editing commit messages is out of scope here (that needs a reword and a force-push);
this tool only guards the body, and `pr-gate.yml` guards both.

Usage
-----
    # assert a body still agrees with itself, e.g. after a rebase. No write.
    tools/pr-body.py 2790 --check --must-contain "Closes #2783" \\
        --must-not-contain "test-count-baseline.json is untouched"

    # correct a claim that no longer matches the diff
    tools/pr-body.py 2790 --dry-run \\
        --replace "baseline is untouched" "baseline is updated in this PR"
    tools/pr-body.py 2790 \\
        --replace "baseline is untouched" "baseline is updated in this PR"

    # an anchor that legitimately appears more than once
    tools/pr-body.py 2790 --replace-count 3 "impl-7" "stma-auto-1"

Exit codes
----------
    0  the intended body is in place: edited and verified by re-reading, or
       --check / --dry-run passed every assertion
    1  NOTHING TO DO -- the body already matches the intent. No write attempted.
    2  PRECONDITION FAILED -- an assertion said no. NOTHING WAS WRITTEN.
    3  UPLOAD FAILED -- the write did not land, confirmed by re-reading that the
       body is still the original. Nothing was lost; retry.
    4  VERIFICATION AFTER UPLOAD FAILED -- the body on GitHub is neither the
       original nor what was intended. ACT ON THIS: something is in an unexpected
       state. The diff is printed.
    5  COULD NOT READ A TRUSTWORTHY BODY (auth, network, empty/short/disagreeing
       reads). NOTHING WAS WRITTEN. This is the guard that #2790 needed.
"""
from __future__ import annotations

import argparse
import difflib
import json
import os
import re
import subprocess
import sys
import tempfile
import time
from typing import Callable

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    import agent_self_freshness as _freshness
except Exception:  # pragma: no cover - a copy detached from its sibling module
    _freshness = None

REPO = "StefanMaron/BusinessCentral.AL.Runner"

# Same list ci-wait.py retries on; the network here times out often enough that a
# single failed call is not evidence of anything.
TRANSIENT = ("i/o timeout", "connection reset", "502 Bad", "dial tcp",
             "could not connect", "TLS handshake", "timeout awaiting")

EXIT_OK = 0
EXIT_NOTHING_TO_DO = 1
EXIT_PRECONDITION = 2
EXIT_UPLOAD_FAILED = 3
EXIT_VERIFY_FAILED = 4
EXIT_FETCH_FAILED = 5


# --------------------------------------------------------------------------
# Closing references. Ported from .github/scripts/check_closing_reference.sh so
# this fails locally with the same verdict pr-gate.yml reaches server-side.
# tools/test_pr_body.py runs a parity check against that script when bash and a
# PCRE-capable grep are available.
# --------------------------------------------------------------------------

KEYWORDS = "close|closes|closed|fix|fixes|fixed|resolve|resolves|resolved"
# "#N" (the "#" is REQUIRED -- "fixes 3 bugs" is prose, and treating a bare number
# as a reference is a false positive that shipped once already), "owner/repo#N",
# or a full issue/PR URL. Deliberately NOT "GH-N": that only becomes live with a
# configured autolink, and this repo has none.
REF_HASH = r"(?:[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)?#[0-9]+"
REF_URL = r"https?://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/(?:issues|pull)/[0-9]+"
REF = f"(?:{REF_HASH}|{REF_URL})"

# Separator between the keyword and the reference. This was "[ \t]+", which
# REQUIRES whitespace and so could not match a colon -- while GitHub's parser
# honors "closes: #N". That accidentally closed #2942 when PR #2951 merged
# (#3094). Kept deliberately wider than any documented GitHub syntax, because
# the two failure directions do not cost the same: a false positive costs one
# reword, a false negative silently closes somebody else's issue.
#
# It must stay in step with SEP in .github/scripts/check_closing_reference.sh --
# that script is the server-side gate, this is only the local preflight, and
# tools/test_pr_body.py asserts parity between the two on a case list that now
# includes the colon forms.
SEP = r"[ \t]*[,;:]?[ \t]*"

CANONICAL_LINE_RE = re.compile(rf"^[ \t]*(?:{KEYWORDS}){SEP}{REF}[ \t]*\.?[ \t]*$", re.I)
STRAY_RE = re.compile(rf"\b(?:{KEYWORDS}){SEP}{REF}", re.I)


def _ref_number(fragment: str) -> int | None:
    """The issue number in a matched fragment: the LAST run of digits.

    True whether the reference was "#123", "owner/repo#123" or a URL ending
    ".../issues/123" -- any earlier digits belong to an owner or repo name.
    """
    nums = re.findall(r"[0-9]+", fragment)
    return int(nums[-1]) if nums else None


def declared_targets(body: str) -> list[int]:
    """Issue numbers declared by a CANONICAL trailer line ("Closes #123" alone).

    A standalone line is a declaration a reviewer can see; the same text inside a
    sentence is a stray (it still closes on merge, which is the bug).
    """
    out: list[int] = []
    for line in body.split("\n"):
        if CANONICAL_LINE_RE.match(line):
            # {SEP}, not a hardcoded "[ \t]+". This is a THIRD copy of the
            # keyword/reference shape; the shell script has the same one, and
            # widening the two named constants but not this extraction made
            # CANONICAL_LINE_RE match a line that then yielded no number, so
            # "Closes: #123" declared nothing at all (#3094).
            m = STRAY_RE.search(line) or re.search(rf"(?:{KEYWORDS}){SEP}{REF}", line, re.I)
            n = _ref_number(m.group(0)) if m else None
            if n is not None and n not in out:
                out.append(n)
    return out


def stray_closing_reference(body: str, declared: list[int]) -> tuple[int, str] | None:
    """First closing keyword + reference that is NOT one of `declared`.

    Returns (issue number, the offending line) or None.
    """
    for line in body.split("\n"):
        if not line.strip():
            continue
        if CANONICAL_LINE_RE.match(line):
            continue
        for m in STRAY_RE.finditer(line):
            n = _ref_number(m.group(0))
            if n is not None and n not in declared:
                return n, line.strip()
    return None


# --------------------------------------------------------------------------
# Normalisation. GitHub stores bodies with CRLF and strips trailing newlines, so
# a body uploaded as "a\nb\n" reads back as "a\r\nb". Comparing raw would make
# every verification fail and every anchor with a newline in it miss.
# --------------------------------------------------------------------------

def norm(text: str) -> str:
    return text.replace("\r\n", "\n").replace("\r", "\n").rstrip("\n")


# --------------------------------------------------------------------------
# gh plumbing (injectable, so the tests never touch the network)
# --------------------------------------------------------------------------

def gh(args: list[str], attempts: int = 4, sleep: Callable[[float], None] | None = None
       ) -> tuple[int, str]:
    """Run gh, retrying transient network failures. Returns (rc, stdout+stderr)."""
    # Resolved here, not as a default: a default argument binds time.sleep at
    # def time, which no test can then intercept -- and a test suite that really
    # sleeps is a test suite nobody runs.
    sleep = sleep or time.sleep
    last = ""
    for i in range(attempts):
        p = subprocess.run(["gh", *args], capture_output=True, text=True)
        out = (p.stdout or "") + (p.stderr or "")
        # mise prints a banner on stdout; drop it so JSON parses.
        out = "\n".join(l for l in out.split("\n") if not l.startswith("mise "))
        if p.returncode == 0 or not any(t.lower() in out.lower() for t in TRANSIENT):
            return p.returncode, out.strip()
        last = out
        sleep(3 * (i + 1))
    return 1, last.strip()


class FetchError(Exception):
    pass


def parse_body_json(rc: int, out: str) -> str:
    """The body out of `gh pr view --json body`, or raise.

    Deliberately NOT `--jq .body`: with --jq, a failed call and a genuinely empty
    body both produce an empty stdout, which is exactly the ambiguity that let
    #2790's edit run against "". The JSON envelope makes a successful read
    self-evident -- a dict with a "body" key -- and anything else an error.
    """
    if rc != 0:
        raise FetchError(f"gh exited {rc}: {out.strip()[:400]}")
    try:
        obj = json.loads(out)
    except Exception as e:
        raise FetchError(f"response was not JSON ({e}): {out.strip()[:400]!r}")
    if not isinstance(obj, dict) or "body" not in obj:
        raise FetchError(f"response has no 'body' key: {out.strip()[:400]!r}")
    body = obj["body"]
    if body is None:
        body = ""
    if not isinstance(body, str):
        raise FetchError(f"'body' is not a string: {type(body).__name__}")
    return body


def fetch_body(reader: Callable[[], tuple[int, str]], min_bytes: int,
               double_read: bool = True, attempts: int = 3,
               sleep: Callable[[float], None] | None = None) -> str:
    """A body we are willing to edit, or FetchError.

    Three refusals, all of which #2790's edit needed and none of which it had:
    an unparseable response, an empty body, a body shorter than `min_bytes`.
    Plus, by default, TWO reads that have to agree -- a truncated response is
    unlikely to truncate identically twice, and the cost is one extra API call
    against the cost of overwriting a body.
    """
    sleep = sleep or time.sleep
    last_err = ""
    for attempt in range(attempts):
        if attempt:
            sleep(2 * attempt)
        try:
            first = norm(parse_body_json(*reader()))
        except FetchError as e:
            last_err = str(e)
            continue
        if not first.strip():
            last_err = ("the fetched body is EMPTY. Refusing to use it as the baseline "
                        "for an edit -- this is exactly how PR #2790's body was lost.")
            continue
        if len(first) < min_bytes:
            last_err = (f"the fetched body is {len(first)} bytes, under the "
                        f"--min-bytes {min_bytes} plausibility floor. A truncated read "
                        f"is the same accident as an empty one. If the body really is "
                        f"this short, pass --min-bytes {max(1, len(first))}.")
            continue
        if not double_read:
            return first
        try:
            second = norm(parse_body_json(*reader()))
        except FetchError as e:
            last_err = f"second confirming read failed: {e}"
            continue
        if first != second:
            last_err = (f"two reads of the body disagree ({len(first)} vs {len(second)} "
                        f"bytes). One of them is truncated or the body is being edited "
                        f"concurrently; either way it is not a safe baseline.")
            continue
        return first
    raise FetchError(last_err or "no attempt produced a body")


# --------------------------------------------------------------------------
# Edits
# --------------------------------------------------------------------------

class PreconditionFailed(Exception):
    pass


class Result:
    def __init__(self, name: str, ok: bool, detail: str = ""):
        self.name, self.ok, self.detail = name, ok, detail

    def line(self) -> str:
        return f"  {'ok  ' if self.ok else 'FAIL'} {self.name}" + (f" -- {self.detail}" if self.detail else "")


class Edit:
    """One anchored replacement. `count` is the number of occurrences REQUIRED."""

    def __init__(self, old: str, new: str, count: int = 1):
        self.old, self.new, self.count = norm(old), norm(new), count


def apply_edits(body: str, edits: list[Edit]) -> tuple[str, list[Result]]:
    """Apply every edit, or raise PreconditionFailed naming the one that missed.

    The whole point: an anchor that is not found the expected number of times is
    an ERROR. `str.replace` on a missing anchor is a silent no-op, and a script
    built out of silent no-ops reports success on a body it never touched.
    """
    results: list[Result] = []
    out = body
    for e in edits:
        found = out.count(e.old)
        if found != e.count:
            results.append(Result(f"anchor {e.old[:60]!r}", False,
                                  f"found {found} time(s), expected {e.count}"))
            raise PreconditionFailed(
                f"anchor {e.old[:200]!r} was found {found} time(s) in the body, "
                f"expected exactly {e.count}. Nothing was written. "
                + ("The body may have changed since you composed this edit; re-read it "
                   "with --check and re-anchor." if found == 0 else
                   "Pass --replace-count <n> if the anchor legitimately repeats."))
        results.append(Result(f"anchor {e.old[:60]!r}", True, f"found {found}, replaced"))
        out = out.replace(e.old, e.new)
    return out, results


def check_body(orig: str, new: str, *, require_closes: list[int],
               must_contain: list[str], must_not_contain: list[str],
               max_shrink_bytes: int, max_shrink_frac: float,
               force_shrink: bool, allow_drop_closes: bool) -> list[Result]:
    """Every assertion about the RESULT. Pure; see tools/test_pr_body.py."""
    results: list[Result] = []
    failures: list[str] = []

    def add(name: str, ok: bool, detail: str, why: str = "") -> None:
        results.append(Result(name, ok, detail))
        if not ok:
            failures.append(why or f"{name}: {detail}")

    orig_targets = declared_targets(orig)
    new_targets = declared_targets(new)

    # --- the #2790 damage, stated directly -------------------------------
    lost = [n for n in orig_targets if n not in new_targets]
    if lost and not allow_drop_closes:
        add("closes-survive", False,
            f"the edit drops the closing reference(s) for {', '.join('#%d' % n for n in lost)}",
            f"This edit would remove the standalone closing-reference line(s) naming "
            f"{', '.join('#%d' % n for n in lost)} from the body. Without it the issue never "
            f"auto-closes on merge and is left labeled in-progress forever (#2046, #1642, "
            f"#1640, and #2790 for real on this exact tool's ancestor). If the removal is "
            f"deliberate, pass --allow-drop-closes.")
    elif lost:
        add("closes-survive", True, f"dropping {', '.join('#%d' % n for n in lost)} (--allow-drop-closes)")
    else:
        add("closes-survive", True,
            f"declared target(s): {', '.join('#%d' % n for n in new_targets) or 'none'}")

    for n in require_closes:
        add(f"requires closing reference #{n}", n in new_targets,
            "declared" if n in new_targets else "NOT declared in the new body",
            f"--closes {n} was required, but the new body has no standalone "
            f"'Closes #{n}' line. GitHub only auto-closes from a reference it can parse.")

    # --- the other direction: a keyword next to an issue we do not mean ---
    stray = stray_closing_reference(new, new_targets)
    if stray:
        n, line = stray
        add("no-stray-closing", False, f"#{n} in: {line[:100]}",
            f"The new body contains a closing keyword next to issue #{n}, written inline "
            f"rather than as its own trailer line, and #{n} is not a declared target. "
            f"GitHub's parser fires on that pattern anywhere in the merge message and does "
            f"not understand negation -- PR #2127's body said a sentence did NOT close an "
            f"issue and the merge closed it anyway. Refer to it without the keyword (e.g. "
            f"'see #{n}'), or declare it on its own line. pr-gate.yml's "
            f"reject-bad-closing-references job rejects this server-side too.\n"
            f"    offending line: {line[:200]}")
    else:
        add("no-stray-closing", True, "no closing keyword next to an undeclared issue")

    # --- shrink ----------------------------------------------------------
    shrink = len(orig) - len(new)
    allowed = max(max_shrink_bytes, int(len(orig) * max_shrink_frac))
    if shrink > allowed and not force_shrink:
        add("no-large-shrink", False, f"would remove {shrink} bytes (allowed {allowed})",
            f"This edit shrinks the body by {shrink} bytes ({len(orig)} -> {len(new)}), past "
            f"the threshold of {allowed} bytes (the larger of --max-shrink-bytes "
            f"{max_shrink_bytes} and --max-shrink-frac {max_shrink_frac:.0%} of the original). "
            f"PR #2790 lost ~3.3 KB this way. If the shrink is intended, pass --force-shrink.")
    elif shrink > allowed:
        add("no-large-shrink", True, f"removing {shrink} bytes (--force-shrink)")
    else:
        add("no-large-shrink", True,
            f"{len(orig)} -> {len(new)} bytes ({'-' if shrink > 0 else '+'}{abs(shrink)}), "
            f"within {allowed}")

    for t in must_contain:
        ok = norm(t) in new
        add(f"must contain {t[:60]!r}", ok, "present" if ok else "ABSENT",
            f"--must-contain {t[:200]!r} is not in the body.")
    for t in must_not_contain:
        ok = norm(t) not in new
        add(f"must not contain {t[:60]!r}", ok, "absent" if ok else "PRESENT",
            f"--must-not-contain {t[:200]!r} is in the body. If a rebase made a claim in "
            f"the body untrue, correct the claim rather than leaving it standing -- a "
            f"reviewer trusts that sentence in order to SKIP checking.")

    if failures:
        raise PreconditionFailed("\n\n".join(failures))
    return results


def diff(orig: str, new: str, pr: str) -> str:
    return "".join(difflib.unified_diff(
        orig.splitlines(keepends=True), new.splitlines(keepends=True),
        fromfile=f"PR #{pr} body (on GitHub)", tofile=f"PR #{pr} body (intended)",
        n=2))


# --------------------------------------------------------------------------
# Upload + verification
# --------------------------------------------------------------------------

class Outcome:
    def __init__(self, code: int, lines: list[str]):
        self.code, self.lines = code, lines


def upload_and_verify(orig: str, intended: str,
                      writer: Callable[[str], tuple[int, str]],
                      refetch: Callable[[], str]) -> Outcome:
    """Write, then decide from a RE-READ what actually happened.

    The write's exit code is not evidence. On the night this tool was written a
    `gh pr merge` reported `dial tcp ... i/o timeout` on a call that had already
    succeeded, and the retry said "already merged". So a failing rc with a body
    that reads back as intended is a SUCCESS, and a passing rc is still verified.
    """
    rc, out = writer(intended)
    lines: list[str] = []
    if rc != 0:
        lines.append(f"  the write reported failure (rc={rc}): {out.strip()[:300]}")
        lines.append("  re-reading anyway -- a failed rc here does not mean the write "
                     "did not land.")
    try:
        actual = refetch()
    except FetchError as e:
        lines.append(f"  VERIFICATION COULD NOT RUN: {e}")
        lines.append("  The body is in an UNKNOWN state. Re-read it by hand before "
                     "editing again: gh pr view <N> --json body")
        return Outcome(EXIT_VERIFY_FAILED, lines)

    if actual == intended:
        lines.append("  verified by re-reading: the body on GitHub is what was intended"
                     + (" (despite the write's rc)" if rc != 0 else ""))
        return Outcome(EXIT_OK, lines)
    if actual == orig:
        lines.append("  verified by re-reading: the body is UNCHANGED -- the write did "
                     "not land. Nothing was lost; retry.")
        return Outcome(EXIT_UPLOAD_FAILED, lines)
    lines.append("  VERIFICATION FAILED: the body on GitHub is neither the original nor "
                 "what was intended.")
    lines.append(f"  intended {len(intended)} bytes, found {len(actual)} bytes.")
    lines.append("  diff (intended -> what is actually there):")
    lines.append("".join(difflib.unified_diff(
        intended.splitlines(keepends=True), actual.splitlines(keepends=True),
        fromfile="intended", tofile="actually on GitHub", n=2)))
    return Outcome(EXIT_VERIFY_FAILED, lines)


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------

def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="tools/pr-body.py",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        description=__doc__)
    p.add_argument("pr", help="pull request number")
    p.add_argument("--repo", default=REPO)

    p.add_argument("--replace", nargs=2, action="append", metavar=("OLD", "NEW"),
                   default=[], help="replace OLD with NEW; OLD must occur EXACTLY ONCE")
    p.add_argument("--replace-count", nargs=3, action="append",
                   metavar=("N", "OLD", "NEW"), default=[],
                   help="replace OLD with NEW; OLD must occur exactly N times")
    p.add_argument("--append", metavar="TEXT",
                   help="append TEXT to the body. Usually the WRONG move -- post a "
                        "comment instead (gh pr comment); see the header of this file.")
    p.add_argument("--append-file", metavar="FILE", help="append FILE's contents (see --append)")
    p.add_argument("--body-file", metavar="FILE",
                   help="replace the whole body with FILE's contents (still guarded)")

    p.add_argument("--check", action="store_true",
                   help="assert only, never write. Use after a rebase to re-verify that "
                        "the body still agrees with the diff.")
    p.add_argument("--dry-run", action="store_true",
                   help="print the diff and every assertion's result, write nothing")

    p.add_argument("--must-contain", action="append", default=[], metavar="TEXT")
    p.add_argument("--must-not-contain", action="append", default=[], metavar="TEXT")
    p.add_argument("--closes", action="append", type=int, default=[], metavar="N",
                   help="require a standalone closing-reference line naming issue N")
    p.add_argument("--allow-drop-closes", action="store_true",
                   help="permit removing a closing reference the body already declares")

    p.add_argument("--min-bytes", type=int, default=200,
                   help="refuse a fetched body shorter than this (default: 200)")
    p.add_argument("--max-shrink-bytes", type=int, default=200,
                   help="absolute shrink allowance (default: 200)")
    p.add_argument("--max-shrink-frac", type=float, default=0.10,
                   help="fractional shrink allowance (default: 0.10)")
    p.add_argument("--force-shrink", action="store_true",
                   help="permit a shrink past the threshold")
    p.add_argument("--single-read", action="store_true",
                   help="skip the confirming second read of the body (not recommended)")
    return p


def collect_edits(args) -> list[Edit]:
    edits = [Edit(old, new) for old, new in args.replace]
    for n, old, new in args.replace_count:
        try:
            count = int(n)
        except ValueError:
            raise SystemExit(f"--replace-count: N must be an integer, got {n!r}")
        if count < 1:
            raise SystemExit("--replace-count: N must be >= 1")
        edits.append(Edit(old, new, count))
    return edits


def build_new_body(orig: str, args, edits: list[Edit]) -> tuple[str, list[Result]]:
    if args.body_file:
        with open(args.body_file) as f:
            return norm(f.read()), [Result("whole-body replacement", True, args.body_file)]
    new, results = apply_edits(orig, edits)
    tail = ""
    if args.append:
        tail += args.append
    if args.append_file:
        with open(args.append_file) as f:
            tail += f.read()
    if tail:
        new = norm(new + "\n\n" + norm(tail))
        results.append(Result("append", True, f"{len(norm(tail))} bytes appended"))
    return new, results


def freshness_refusal(printer=None) -> int | None:
    """EXIT_PRECONDITION if this copy of the tool is behind origin/main, else None.

    Same shape as #3020's defect in ci-wait.py, one tool over: an agent runs the
    copy in its own worktree, and a worktree is created once and never
    fast-forwarded. Measured 2026-09-06, 40 of the 59 worktrees carrying this
    file had a version that was not origin/main's. Every guard in this module was
    added because an unguarded edit destroyed a body, so running a copy that
    predates a guard is running without that guard -- and this tool's whole
    failure mode is REFUSING TO WRITE, which is what a stale copy should do too.
    """
    printer = printer or (lambda m: print(m, file=sys.stderr))
    if _freshness is None:
        printer("note: could not establish whether this copy of pr-body.py is current "
                "-- tools/agent_self_freshness.py could not be imported. Proceeding; "
                "nothing has checked that this copy carries the latest guards.")
        return None
    refused = False
    confirm = True
    for target in (os.path.abspath(__file__), os.path.abspath(_freshness.__file__)):
        fresh = _freshness.assess(target, remote_check=confirm)
        confirm = False  # one ls-remote, not one per file
        for note in fresh.notes:
            printer(note)
        refused = refused or fresh.refuse
    if refused:
        printer("\nREFUSING TO WRITE -- this copy of pr-body.py is STALE. Its guards "
                "are older than origin/main's, and a guard you do not have cannot "
                "refuse anything. Nothing was read or written.")
        return EXIT_PRECONDITION
    return None


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    stale = freshness_refusal()
    if stale is not None:
        return stale
    edits = collect_edits(args)
    editing = bool(edits or args.append or args.append_file or args.body_file)

    if args.check and editing:
        print("--check asserts without editing; drop the edit flags, or drop --check.",
              file=sys.stderr)
        return EXIT_PRECONDITION
    if not args.check and not editing:
        print("nothing to do: pass an edit (--replace / --replace-count / --append / "
              "--body-file) or --check.", file=sys.stderr)
        return EXIT_PRECONDITION
    if args.body_file and (edits or args.append or args.append_file):
        # A whole-body replacement ignores anchors, so accepting both would
        # silently drop the anchored edits -- and an anchor that is silently
        # dropped is the failure this tool exists to prevent.
        print("--body-file replaces the whole body; it cannot be combined with "
              "--replace / --replace-count / --append.", file=sys.stderr)
        return EXIT_PRECONDITION
    if args.check and not (args.must_contain or args.must_not_contain or args.closes):
        # A --check that asserts nothing is a check that cannot fail -- the exact
        # shape of the guard that let #2790's body be destroyed. Refuse it.
        print("--check with no assertion cannot fail, which is the shape of guard this "
              "tool exists to replace. Pass at least one of --must-contain, "
              "--must-not-contain or --closes.", file=sys.stderr)
        return EXIT_PRECONDITION
    if args.append or args.append_file:
        print("NOTE: appending to a body is usually the wrong move. A note, a status "
              "update or a reply belongs in a COMMENT (gh pr comment "
              f"{args.pr} --body ...), which cannot destroy anything. PR #2790's body was "
              "being edited only to add a note, and the edit destroyed it.", file=sys.stderr)

    def reader() -> tuple[int, str]:
        return gh(["pr", "view", args.pr, "--repo", args.repo, "--json", "body"])

    try:
        orig = fetch_body(reader, args.min_bytes, double_read=not args.single_read)
    except FetchError as e:
        print(f"REFUSING TO WRITE -- could not read a trustworthy body for PR "
              f"#{args.pr}:\n  {e}", file=sys.stderr)
        return EXIT_FETCH_FAILED

    print(f"PR #{args.pr}: read {len(orig)} bytes"
          f"{'' if args.single_read else ' (two agreeing reads)'}")

    edit_results: list[Result] = []
    try:
        if editing:
            new, edit_results = build_new_body(orig, args, edits)
        else:
            new = orig
        check_results = check_body(
            orig, new,
            require_closes=args.closes,
            must_contain=args.must_contain,
            must_not_contain=args.must_not_contain,
            max_shrink_bytes=args.max_shrink_bytes,
            max_shrink_frac=args.max_shrink_frac,
            force_shrink=args.force_shrink,
            allow_drop_closes=args.allow_drop_closes)
    except PreconditionFailed as e:
        print("assertions:", file=sys.stderr)
        for r in edit_results:
            print(r.line(), file=sys.stderr)
        print(f"\nPRECONDITION FAILED -- NOTHING WAS WRITTEN.\n\n{e}", file=sys.stderr)
        return EXIT_PRECONDITION

    print("assertions:")
    for r in edit_results + check_results:
        print(r.line())

    if not editing:
        print("\n--check: every assertion passed. Nothing was written.")
        return EXIT_OK
    if new == orig:
        print("\nNOTHING TO DO: the body already matches the intent; no write attempted.")
        return EXIT_NOTHING_TO_DO

    d = diff(orig, new, args.pr)
    if args.dry_run:
        print("\n--dry-run, nothing written. Intended change:\n")
        print(d)
        return EXIT_OK

    print("\nwriting:\n")
    print(d)

    def writer(text: str) -> tuple[int, str]:
        fd, path = tempfile.mkstemp(prefix="pr-body-", suffix=".md")
        try:
            with os.fdopen(fd, "w") as f:
                f.write(text + "\n")
            return gh(["pr", "edit", args.pr, "--repo", args.repo, "--body-file", path])
        finally:
            os.unlink(path)

    def refetch() -> str:
        # min_bytes deliberately 1 here: the question is "what is there now",
        # and refusing to look because the answer is short would leave the
        # unexpected state unreported. The comparison below judges it.
        return fetch_body(reader, 1, double_read=not args.single_read)

    outcome = upload_and_verify(orig, new, writer, refetch)
    for l in outcome.lines:
        print(l, file=sys.stderr if outcome.code else sys.stdout)
    return outcome.code


if __name__ == "__main__":
    sys.exit(main())
