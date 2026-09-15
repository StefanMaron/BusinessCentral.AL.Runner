#!/usr/bin/env python3
"""Unit tests for tools/armed-prs.py's classification and exit code.

The defect this tool exists for is a PR armed on a green verdict that goes red
afterwards and sits there (#4006): measured windows of 12 and 119 minutes on a
night with an attentive coordinator and nineteen PRs armed at once. So the
property under test is not "does it call gh" but **which of the five per-PR
verdicts each `ci-wait.py` exit code maps to, and what the run-level exit code
is for each mix of them**.

Two of those mappings carry the whole value of the tool and are the ones a
plausible-looking rewrite gets wrong:

  * exit 2 (checks still running) is the ORDINARY state of an armed PR and must
    be QUIET. A tool that reports it reports twenty-one PRs on a busy night and
    is then ignored, which is indistinguishable from not existing.
  * exit 3 (could not read the verdict) is NOT quiet. Folding an unreadable
    verdict into the green set is precisely the defect
    `guards-need-a-third-state.md` exists to prevent -- it asserts a PR is fine
    having failed to measure it.

`ci_wait_rc` is injected rather than mocked at the subprocess layer, because the
thing worth pinning is the mapping, not gh's argv.

Run: python3 tools/test_armed_prs.py
"""
from __future__ import annotations

import importlib.util
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location(
    "armed_prs", os.path.join(HERE, "armed-prs.py"))
ap = importlib.util.module_from_spec(_spec)
sys.modules["armed_prs"] = ap
_spec.loader.exec_module(ap)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


def pr(number: int, armed: bool = True, enabled_at: str | None = None) -> dict:
    """A `gh pr list --json number,autoMergeRequest,headRefOid` element."""
    return {
        "number": number,
        "headRefOid": f"{number:040x}",
        "autoMergeRequest": (
            {"enabledAt": enabled_at or "2026-09-13T04:00:00Z",
             "mergeMethod": "SQUASH"} if armed else None),
    }


# --------------------------------------------------------------------------
# armed(): which PRs are armed at all
# --------------------------------------------------------------------------
print("armed():")

check("armed selects autoMergeRequest != null",
      [p["number"] for p in ap.armed([pr(1), pr(2, armed=False), pr(3)])]
      == [1, 3])

check("armed on an empty list is empty", ap.armed([]) == [])

check("armed ignores a PR whose autoMergeRequest key is absent entirely",
      ap.armed([{"number": 9, "headRefOid": "a" * 40}]) == [])


# --------------------------------------------------------------------------
# classify(): ci-wait.py's exit code -> this tool's verdict
# --------------------------------------------------------------------------
print("classify():")

# The two quiet ones. Exit 2 being quiet is the whole reason this tool is
# usable on a night with 21 armed PRs.
check("rc 0 (green) is OK", ap.classify(0) == ap.OK)
check("rc 2 (still running) is OK -- the ordinary armed state",
      ap.classify(2) == ap.OK)

# The reported ones.
check("rc 1 (a required check failed) is FAILING", ap.classify(1) == ap.FAILING)
check("rc 4 (blocked, not failing) is FAILING", ap.classify(4) == ap.FAILING)

# The third state.
check("rc 3 (could not determine) is UNREADABLE",
      ap.classify(3) == ap.UNREADABLE)
check("an unmapped rc is UNREADABLE, never OK",
      ap.classify(99) == ap.UNREADABLE)
check("a negative rc (killed by a signal) is UNREADABLE, never OK",
      ap.classify(-9) == ap.UNREADABLE)

check("UNREADABLE is a distinct value from OK and FAILING",
      len({ap.OK, ap.FAILING, ap.UNREADABLE}) == 3)


# --------------------------------------------------------------------------
# sweep(): the run-level verdict over a set of armed PRs
# --------------------------------------------------------------------------
print("sweep():")


def sweep(rcs: dict[int, int], prs: list[dict] | None = None):
    """Run a sweep with ci-wait's exit code injected per PR number."""
    prs = prs if prs is not None else [pr(n) for n in sorted(rcs)]
    return ap.sweep(prs, ci_wait_rc=lambda n, **kw: rcs[n])


rc, rows = sweep({1: 0, 2: 2})
check("all green-or-running exits 0", rc == 0)
check("...and reports no rows", rows == [])

rc, rows = sweep({1: 0, 2: 1, 3: 2})
check("one failing among green exits 1", rc == 1)
check("...and reports exactly the failing PR",
      [(r.number, r.verdict) for r in rows] == [(2, ap.FAILING)])

rc, rows = sweep({1: 0, 2: 3})
check("one unreadable among green exits 3 -- NOT 0", rc == 3)
check("...and reports it as UNREADABLE, not as failing",
      [(r.number, r.verdict) for r in rows] == [(2, ap.UNREADABLE)])

# The precedence question: a run carrying both must not let the unreadable
# verdict hide the failing one, nor the reverse. Exit 1 is the actionable
# verdict, so it wins -- but BOTH rows are still reported, which is the half
# that matters. An exit code carrying only one of them is still a report that
# names both.
rc, rows = sweep({1: 1, 2: 3})
check("failing + unreadable exits 1 (the actionable verdict wins)", rc == 1)
check("...and BOTH are reported, so neither is lost to the exit code",
      sorted((r.number, r.verdict) for r in rows)
      == [(1, ap.FAILING), (2, ap.UNREADABLE)])

rc, rows = sweep({}, prs=[pr(1, armed=False)])
check("no armed PRs at all exits 0 -- a legitimate absence, not a refusal",
      rc == 0 and rows == [])

# ci-wait.py is asked ONLY about armed PRs. Asking about every open PR would
# spend 35 subprocesses to answer a question about 21 (measured on the live
# queue, 2026-09-13).
asked: list[int] = []
ap.sweep([pr(1), pr(2, armed=False), pr(3)],
         ci_wait_rc=lambda n, **kw: asked.append(n) or 0)
check("an unarmed PR is never handed to ci-wait.py", asked == [1, 3])


# --------------------------------------------------------------------------
# A crashing ci_wait_rc is UNREADABLE, not a crashed sweep
# --------------------------------------------------------------------------
print("robustness:")


def boom(n, **kw):
    if n == 2:
        raise OSError("gh not found")
    return 0


rc, rows = ap.sweep([pr(1), pr(2), pr(3)], ci_wait_rc=boom)
check("a raising ci_wait_rc yields UNREADABLE for that PR only",
      [(r.number, r.verdict) for r in rows] == [(2, ap.UNREADABLE)])
check("...and the sweep still exits 3 rather than dying", rc == 3)
check("...and the other PRs were still swept",
      rc == 3 and len(rows) == 1)


# --------------------------------------------------------------------------
# The report itself must name the distinction, not merely carry it
# --------------------------------------------------------------------------
print("report():")

_, rows = sweep({1: 1, 2: 3})
text = ap.report(rows)
check("the report names the failing PR number", "#1" in text)
check("the report names the unreadable PR number", "#2" in text)
check("the report distinguishes the two verdicts in its TEXT",
      "FAILING" in text and "UNREADABLE" in text)
check("the unreadable row says the verdict was not read, not that it is fine",
      "could not" in text.lower() or "unreadable" in text.lower())

check("an empty report is empty rather than a misleading all-clear string",
      ap.report([]) == "")


# --------------------------------------------------------------------------
# armed_for(): the age an armed PR has carried its arming, for the report
# --------------------------------------------------------------------------
print("armed_for():")

check("a 119-minute arming reports in hours-and-minutes",
      ap.armed_for("2026-09-13T02:22:00Z",
                   now="2026-09-13T04:21:00Z") == "1h59m")
check("a 12-minute arming reports in minutes",
      ap.armed_for("2026-09-13T04:14:00Z",
                   now="2026-09-13T04:26:00Z") == "12m")
check("an absent enabledAt is 'unknown', never 0m",
      ap.armed_for(None, now="2026-09-13T04:26:00Z") == "unknown")
check("an unparseable enabledAt is 'unknown', never a crash",
      ap.armed_for("not-a-date", now="2026-09-13T04:26:00Z") == "unknown")


# ===========================================================================
# Re-firing a stale corpus gate (#4206)
#
# The gate `A cited corpus PR must be able to merge` evaluates on
# push/edited/labeled, never when the corpus PR moves. Corpus-first merging
# (bc-behavior-tests-go-upstream.md step 5) guarantees the corpus PR merges
# AFTER the runner PR's last push, so the stored `failure` outlives its cause --
# and because a failing NON-required check makes mergeStateStatus UNSTABLE,
# `enablePullRequestAutoMerge` refuses. Measured three times in one session:
# #4135/#348, #4202/#368, and #4203/#371, where the gate ran at 20:04:28Z, the
# corpus PR merged at 20:21:08Z, and a body edit at 20:50:05Z produced a fresh
# `success`. 46 minutes, every instrument reading green throughout.
#
# An armed PR held by a stale gate is exactly the silently-stalled case this
# tool exists for: nothing is red, nothing is broken, and the work does not land.
# ===========================================================================
print("\nre-firing a stale corpus gate (#4206)")


class _Refire:
    """Records the re-fires attempted, so the test asserts on WHAT it did."""

    def __init__(self, ok=True):
        self.calls = []
        self.ok = ok

    def __call__(self, number):
        self.calls.append(number)
        return (True, "") if self.ok else (False, "gh failed")


def _row(number, verdict, rc, stale="CURRENT"):
    r = ap.Row(number, verdict, rc)
    r.corpus_gate = stale
    return r


# --- only a STALE gate is re-fired ------------------------------------------
_r = _Refire()
_done = ap.refire_stale(  [_row(4203, ap.OK, 0, stale="STALE")], refire=_r)
check("#4206: an armed PR whose corpus gate is STALE gets re-fired",
      _r.calls == [4203], repr(_r.calls))
check("#4206: ...and the re-fire is reported, so the coordinator sees it happened",
      len(_done) == 1 and _done[0][0] == 4203 and _done[0][1] is True, repr(_done))

_r = _Refire()
ap.refire_stale([_row(4203, ap.OK, 0, stale="CURRENT")], refire=_r)
check("#4206: a CURRENT gate is left alone -- re-firing it spends a run to learn "
      "nothing", _r.calls == [], repr(_r.calls))

# The third state. An UNKNOWN gate has not been established as stale, and
# re-firing on it would act on a measurement nobody made.
_r = _Refire()
ap.refire_stale([_row(4203, ap.OK, 0, stale="UNKNOWN")], refire=_r)
check("#4206: an UNKNOWN gate is NOT re-fired -- nobody established it is stale, "
      "and acting on an unmeasured state is what guards-need-a-third-state.md forbids",
      _r.calls == [], repr(_r.calls))

_r = _Refire()
ap.refire_stale([_row(4203, ap.OK, 0, stale=None)], refire=_r)
check("#4206: a row carrying no gate verdict at all is not re-fired",
      _r.calls == [], repr(_r.calls))

# --- a failed re-fire is reported, never swallowed --------------------------
_r = _Refire(ok=False)
_done = ap.refire_stale([_row(4203, ap.OK, 0, stale="STALE")], refire=_r)
check("#4206: a re-fire that failed is reported as failed, not as done",
      len(_done) == 1 and _done[0][1] is False, repr(_done))

# --- several at once --------------------------------------------------------
_r = _Refire()
ap.refire_stale([_row(1, ap.OK, 0, stale="STALE"),
                 _row(2, ap.OK, 0, stale="CURRENT"),
                 _row(3, ap.OK, 0, stale="STALE")], refire=_r)
check("#4206: every stale PR in one sweep is re-fired, and only those",
      _r.calls == [1, 3], repr(_r.calls))

# --- the re-fire must not move the head -------------------------------------
check("#4206: the re-fire is a LABEL toggle, never a commit -- a push restarts the "
      "BC matrix (~15 min) and re-arms auto-merge against a head nobody reviewed",
      "label" in (ap.refire_one.__doc__ or "").lower()
      and "commit" not in (ap.refire_one.__doc__ or "").split("label")[0].lower(),
      repr((ap.refire_one.__doc__ or "")[:120]))


# --- the sweep must actually POPULATE corpus_gate ---------------------------
# Without this the re-fire above can never fire: a function that is correct and
# never reached is the "names the thing but does not drive it" shape tdd.md
# warns about, and a grep for `refire_stale` would read as coverage.
print("\nthe sweep populates the gate verdict (#4206)")

check("#4206: a STALE line in ci-wait.py's output is read off as the gate verdict",
      ap.gate_from_output("GREEN on abc1234 -- ok\n"
                          "corpus PR #371: MERGED (head aaaaaaaa)\n"
                          "corpus gate: STALE -- re-fire it\n") == "STALE")
check("#4206: an UNKNOWN line is read as UNKNOWN, not folded into STALE",
      ap.gate_from_output("corpus gate: UNKNOWN -- nobody could read it\n") == "UNKNOWN")
check("#4206: output with no gate line at all is None -- the CURRENT case prints "
      "nothing, and None is 'not stale', which is what refire_stale needs",
      ap.gate_from_output("GREEN on abc1234 -- ok\n"
                          "corpus PR #371: MERGED (head aaaaaaaa)\n") is None)
check("#4206: empty output is None, never a verdict",
      ap.gate_from_output("") is None)
check("#4206: a PR whose body merely MENTIONS the phrase does not read as stale -- "
      "the line must start with the marker",
      ap.gate_from_output("  the docs say `corpus gate: STALE` happens\n") is None)

# The sweep carries it onto the Row, from ci-wait.py's real stdout.
_out = ("GREEN on abc1234 -- ok\ncorpus PR #371: MERGED (head aaaaaaaa)\n"
        "corpus gate: STALE -- corpus PR #371 is MERGED now\n")
_rc2, _rows2 = ap.sweep([{"number": 4203, "autoMergeRequest": {"enabledAt": None},
                          "headRefOid": "a" * 40}],
                        ci_wait_rc=lambda n: (0, _out))
check("#4206: an armed, GREEN PR held by a STALE gate is REPORTED rather than staying "
      "quiet -- it is exactly the silently-stalled case this tool exists for",
      len(_rows2) == 1 and _rows2[0].corpus_gate == "STALE",
      f"{len(_rows2)} row(s) {[getattr(r, 'corpus_gate', None) for r in _rows2]!r}")
check("#4206: ...and it does not become FAILING -- nothing is failing, the tick is stale",
      len(_rows2) == 1 and _rows2[0].verdict != ap.FAILING, repr(_rows2))

_rc3, _rows3 = ap.sweep([{"number": 4203, "autoMergeRequest": {"enabledAt": None},
                          "headRefOid": "a" * 40}],
                        ci_wait_rc=lambda n: (0, "GREEN on abc1234 -- ok\n"))
check("#4206: a green armed PR with no stale gate stays QUIET, as before",
      _rows3 == [] and _rc3 == 0, f"rc={_rc3} rows={_rows3!r}")

# A ci_wait_rc returning a bare int is the pre-#4206 shape. It must keep working,
# because a stale copy of this module beside a fresh test is a real arrangement.
_rc4, _rows4 = ap.sweep([{"number": 4203, "autoMergeRequest": {"enabledAt": None},
                          "headRefOid": "a" * 40}],
                        ci_wait_rc=lambda n: 1)
check("#4206: a ci_wait_rc returning a bare int still works -- the rc is the answer, "
      "and the gate verdict is simply unknown",
      len(_rows4) == 1 and _rows4[0].verdict == ap.FAILING
      and _rows4[0].corpus_gate is None, repr(_rows4))


# --- the report must SAY it, and the exit code must not lie -----------------
print("\nreporting a stale gate (#4206)")

_stale_row = _row(4203, ap.OK, 0, stale="STALE")
_text = ap.report([_stale_row])
check("#4206: a stale gate is named in the report -- a row collected and not "
      "printed is the same silence the issue measured",
      "4203" in _text and "STALE" in _text.upper(), repr(_text))
check("#4206: ...and the report says what to do, because the remedy is the "
      "whole point",
      "re-fire" in _text.lower() or "refire" in _text.lower(), repr(_text))
check("#4206: a stale gate is NOT reported under 'FAILING' -- nothing is failing, "
      "and disarming would be the wrong repair",
      "FAILING" not in _text.split("STALE")[0].upper()
      or _text.upper().index("STALE") < _text.upper().index("FAILING"),
      repr(_text))

_rc5, _rows5 = ap.sweep([{"number": 4203, "autoMergeRequest": {"enabledAt": None},
                          "headRefOid": "a" * 40}],
                        ci_wait_rc=lambda n: (0, "GREEN\ncorpus gate: STALE -- x\n"))
check("#4206: a stale gate alone is exit 0 -- nothing is failing and nothing is "
      "unreadable, so it must not claim either",
      _rc5 == 0, f"rc={_rc5}")

# An empty sweep still prints nothing at all.
check("#4206: no rows still means no report text", ap.report([]) == "")


print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)}")
    for f in FAILURES:
        print(f"  - {f}")
    sys.exit(1)
print("all armed-prs tests passed")
