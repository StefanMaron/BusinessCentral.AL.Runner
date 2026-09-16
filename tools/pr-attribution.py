#!/usr/bin/env python3
"""Will this pull request need a human approval before it can merge? One question, one answer.

    tools/pr-attribution.py 3927            # before arming
    tools/pr-attribution.py 4231 4232 4233  # a whole arming batch, one call
    tools/pr-attribution.py 3927 --json

Why this exists
---------------
The `main` ruleset sets `require_extra_approval_for_unattributed_changes`, so a
pull request carrying a commit authored by another real GitHub account is
`BLOCKED` until a human approves it -- with **every required check green** and
`mergeable: MERGEABLE`.

It is not a check. `tools/ci-wait.py` reads check runs and workflow runs and
correctly reports GREEN; `git merge-tree` is clean; the corpus gate passes; the
closing references are right. Every condition in the arming list holds and the
merge still fails. Measured on #3927 (#3942), which was the fix for a red
`main` -- so the block held a repository-wide red behind an approval nobody
knew was needed, for hours, because nothing reads it until the merge attempt.

This is that read, run before arming instead of after.

What it does NOT do
-------------------
It reports. **Never self-approve to clear this, and never reach for `--admin`.**
The ruleset exists to put a human in front of a change the pushing identity did
not author; an agent approving its own pull request defeats the only thing it
does. This is branch protection working.

Exit codes
----------
    0  no commit needs an approval -- this is not what blocks the merge
    1  at least one pull request carries a commit by another real account
    3  at least one author list COULD NOT BE READ, and none is flagged

Exit 3 is deliberately not folded into 0. "I could not read the authors" is not
"safe to arm" (`guards-need-a-third-state.md`): an unmeasured thing reported as
its success state is the defect, and here it would send a coordinator to arm a
pull request that cannot merge.

The discriminator, and why the obvious one is wrong
---------------------------------------------------
**Not** "a login other than the pushing identity". This loop's own commits carry
`{login:"", name:"Test"}` and `{login:"claude"}`, so that rule fires on every
pull request it writes -- and a check that fires on everything is worse than no
check, because the one that genuinely needs an approval is then indistinguishable
from the noise.

The measurement that settles it is a contrast, not an instance (#3942): #3943
carried both of those and was CLEAN; #3927 carried both **plus**
`{login:"SShadowS", name:"Torben Leth"}` and was BLOCKED. One variable.

Which read to hand it
---------------------
`gh pr view <N> --json commits` reports `authors[].login`. The REST
`/pulls/<N>/commits` listing -- and the GitHub MCP `pull_request_read` with
`method: get_commits` -- reports `commit.author = {name, email, date}` and **no
login at all**. Those are different shapes from different APIs, and a
classifier handed the second cannot tell Torben Leth from an author GitHub
could not resolve. So an entry with no `login` key is the third state, never a
pass: `.get("login", "")` on that shape calls every real account benign and
reports the whole set as safe to arm.

Sister reading: `ci-verdicts.md` §0's exit-4 row, which owns
BLOCKED-with-everything-green and names this as its third cause; the arming
list in the `orchestrating-a-session` skill, which is where this runs.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from dataclasses import dataclass, field

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    import agent_stdio as _stdio
except Exception:  # pragma: no cover - a copy detached from its sibling module
    _stdio = None
if _stdio is not None:
    _stdio.enable_utf8_stdio()

REPO = "StefanMaron/BusinessCentral.AL.Runner"

# The three verdicts. Distinct values, because collapsing UNREADABLE into either
# neighbour is the whole defect: into OK it declares a pull request armable
# having failed to measure it, into APPROVAL_REQUIRED it sends a coordinator to
# ask the owner about a pull request that may need nothing.
OK = "ok"
APPROVAL_REQUIRED = "approval-required"
UNREADABLE = "unreadable"

# Measured benign, not reasoned: #3943 was CLEAN carrying exactly these two.
# An empty login is a commit GitHub could not resolve to any account, which the
# ruleset treats as attributed to the pusher. Adding an account here on the
# strength of "it looks like a bot" would be the single-instance inference this
# tool exists because of -- a second agent-running account (#3275) is still
# another account to this ruleset.
LOOP_LOGINS = frozenset({"", "claude"})

_MISSING = object()

_APPROVAL_REASON = (
    "a commit is attributed to another real GitHub account, so the main "
    "ruleset's require_extra_approval_for_unattributed_changes will refuse the "
    "merge until a human approves it. Report it and ask the owner; never "
    "self-approve, and never reach for --admin.")
_SHAPE_REASON = (
    "the commit-author list could not be read as {login, name} entries. An "
    "entry with no `login` key cannot be told from a real account, so this is "
    "not an all-clear. Read it with `gh pr view <N> --repo <repo> --json "
    "commits --jq '[.commits[].authors[]|{login,name}]'`; the REST "
    "/pulls/<N>/commits shape and the GitHub MCP get_commits method report "
    "{name, email, date} and carry no login.")
_EMPTY_REASON = (
    "the commit-author list came back empty. Every pull request has at least "
    "one commit with at least one author, so zero entries means the read "
    "returned a shape this tool does not understand -- not that nothing is "
    "attributed elsewhere.")
_OK_REASON = (
    "no commit is attributed to another real GitHub account; the "
    "unattributed-changes rule is not what blocks this merge.")


@dataclass
class Result:
    verdict: str
    flagged: list[dict] = field(default_factory=list)
    unreadable: list[dict] = field(default_factory=list)
    reason: str = ""


@dataclass
class Row:
    number: int
    result: Result


def _login_of(entry):
    """The comparable login, or `_MISSING` when this entry cannot supply one.

    The distinction this draws is the whole third state. A `login` key present
    and empty or null is a MEASURED answer -- GitHub resolved the commit to no
    account -- and is benign. A `login` key ABSENT means the API being read does
    not report logins, so nothing about this entry has been measured.
    `.get("login", "")` collapses the two and calls a real account benign.
    """
    if not isinstance(entry, dict) or "login" not in entry:
        return _MISSING
    raw = entry["login"]
    if raw is None:
        return ""
    if not isinstance(raw, str):
        return _MISSING
    return raw.strip().lower()


def classify(authors, *, viewer: str | None = None) -> Result:
    """Does this author list mean an approval will be required?

    `viewer` is the authenticated account, whose own commits are attributed by
    definition and so never unattributed. Passing None widens the flagged set
    rather than narrowing it, which is the safe direction: a missed block costs
    a surprise at merge time, an extra report costs a look.
    """
    if authors is None:
        return Result(UNREADABLE, reason=_SHAPE_REASON)
    if not isinstance(authors, list) or not authors:
        return Result(UNREADABLE, reason=_EMPTY_REASON)

    benign = set(LOOP_LOGINS)
    if viewer and viewer.strip():
        benign.add(viewer.strip().lower())

    flagged: list[dict] = []
    unreadable: list[dict] = []
    for entry in authors:
        login = _login_of(entry)
        if login is _MISSING:
            unreadable.append(entry if isinstance(entry, dict)
                              else {"raw": repr(entry)})
        elif login not in benign:
            flagged.append({"login": entry.get("login") or "",
                            "name": entry.get("name") or ""})

    # A login that WAS read and is foreign settles the question whatever an
    # unreadable sibling says, and the answer is not the success state either
    # way -- so this precedence never turns an unknown into an all-clear.
    if flagged:
        return Result(APPROVAL_REQUIRED, flagged=flagged, unreadable=unreadable,
                      reason=_APPROVAL_REASON)
    if unreadable:
        return Result(UNREADABLE, unreadable=unreadable, reason=_SHAPE_REASON)
    return Result(OK, reason=_OK_REASON)


def _gh(args: list[str]) -> tuple[int, str]:
    """`gh` with the mise banner filtered off stdout (CLAUDE.md)."""
    try:
        p = subprocess.run(["gh", *args], capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=90)
    except (OSError, subprocess.SubprocessError) as exc:
        return 127, str(exc)
    body = "\n".join(l for l in (p.stdout or "").split("\n")
                     if not l.startswith("mise "))
    return p.returncode, body


def fetch_authors(number: int) -> list[dict] | None:
    """Every commit author on a pull request as {login, name}, or None.

    None rather than [] on a failed read: an empty list is what a shape this
    tool did not understand also produces, and classify() refuses both.
    """
    rc, out = _gh(["pr", "view", str(number), "--repo", REPO, "--json", "commits",
                   "--jq", "[.commits[].authors[] | {login, name}]"])
    if rc != 0:
        return None
    try:
        parsed = json.loads(out)
    except json.JSONDecodeError:
        return None
    return parsed if isinstance(parsed, list) else None


def viewer_login() -> str | None:
    """The authenticated account, or None.

    None widens the flagged set rather than narrowing it -- the pushing
    identity's own commits get reported -- so a failed read costs a look and
    never a missed block. The report says which identity it compared against.
    """
    rc, out = _gh(["api", "user", "--jq", ".login"])
    login = (out or "").strip()
    return login if rc == 0 and login else None


def run(numbers, fetch=None, viewer: str | None = None) -> tuple[int, list[Row]]:
    """Classify each pull request. Returns (exit code, rows worth reporting)."""
    fetch = fetch or fetch_authors
    rows: list[Row] = []
    for number in numbers:
        try:
            authors = fetch(number)
        except Exception as exc:
            # One unreadable pull request must not abort the sweep, or it hides
            # every other answer behind it.
            rows.append(Row(number, Result(
                UNREADABLE,
                reason=f"could not read the commit authors: {exc}")))
            continue
        result = classify(authors, viewer=viewer)
        if result.verdict == OK:
            continue
        rows.append(Row(number, result))
    if any(r.result.verdict == APPROVAL_REQUIRED for r in rows):
        return 1, rows
    if rows:
        return 3, rows
    return 0, rows


def report(rows: list[Row]) -> str:
    """The human report. Empty for a clean sweep -- never an all-clear string.

    An empty string lets a caller print nothing on the quiet path, which is what
    keeps a tool run before every arming from becoming noise.
    """
    if not rows:
        return ""
    out: list[str] = []
    flagged = [r for r in rows if r.result.verdict == APPROVAL_REQUIRED]
    unreadable = [r for r in rows if r.result.verdict == UNREADABLE]
    if flagged:
        out.append("APPROVAL REQUIRED -- these will be BLOCKED with every check "
                   "green. Do NOT arm; report to the owner:")
        for r in flagged:
            who = ", ".join(
                f"{a.get('login') or '(no account)'} <{a.get('name') or '?'}>"
                for a in r.result.flagged)
            out.append(f"  #{r.number}  commit author(s): {who}")
        out.append("  (never self-approve, and never reach for --admin -- the "
                   "rule exists to put a human in front of exactly this)")
    if unreadable:
        if flagged:
            out.append("")
        out.append("UNREADABLE -- the commit authors were not established. NOT "
                   "an all-clear; ask again:")
        for r in unreadable:
            out.append(f"  #{r.number}  {r.result.reason}")
    return "\n".join(out)


def main(argv=None) -> int:
    p = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("pr", nargs="+", type=int, help="pull request number(s)")
    p.add_argument("--json", action="store_true", dest="as_json",
                   help="print rows as JSON")
    args = p.parse_args(argv)

    viewer = viewer_login()
    rc, rows = run(args.pr, viewer=viewer)

    if args.as_json:
        print(json.dumps(
            {"viewer": viewer, "exit": rc,
             "rows": [{"number": r.number, "verdict": r.result.verdict,
                       "flagged": r.result.flagged, "reason": r.result.reason}
                      for r in rows]}, indent=2))
        return rc

    against = viewer or ("an UNREAD pushing identity -- its own commits are "
                         "reported too")
    print(f"{len(args.pr)} pull request(s) checked against {against}: "
          f"{len(args.pr) - len(rows)} clear, {len(rows)} needing attention")
    text = report(rows)
    if text:
        print()
        print(text)
    return rc


if __name__ == "__main__":
    sys.exit(main())
