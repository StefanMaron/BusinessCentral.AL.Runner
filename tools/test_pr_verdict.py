#!/usr/bin/env python3
"""Unit tests for tools/pr-verdict.py: the verdict grammar and its exit codes.

The grammar is the whole point of the tool, so it is proven here against the
comment shapes that actually exist on this repository rather than only against
the one shape the tool was written for. 1 of 796 merged pull requests in the
measured window carries a GitHub review object -- review happens in comments,
in at least five header styles, none of them parseable
(https://fbakkensen.github.io/al-runner-retro/#e-11). Each of those five is a
fixture below and each must come back "no verdict", never a green.

The asymmetry that matters: exit 0 is the only answer that lets a PR be armed
for auto-merge, so every fixture that is not an exact MERGE on the current head
AND the current patch-id asserts a NON-ZERO exit. A parser bug that loses a
FIX-FIRST is the failure this suite exists to catch.

Run: python3 tools/test_pr_verdict.py
"""
from __future__ import annotations

import importlib.util
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

_spec = importlib.util.spec_from_file_location("pr_verdict", os.path.join(HERE, "pr-verdict.py"))
pv = importlib.util.module_from_spec(_spec)
sys.modules["pr_verdict"] = pv
_spec.loader.exec_module(pv)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


HEAD = "1f2e3d4c5b6a79880123456789abcdef01234567"
OTHER = "9988776655443322110099887766554433221100"
PATCH = "0123456789ab"
OTHER_PATCH = "ba9876543210"

DOCUMENTED = f"Verdict: MERGE — head {HEAD} — patch {PATCH} — kind: full"

# The five header styles found in the window. None carries a verdict line, so
# each must be unparseable -- that is the state this PR is changing, and the
# tool must never guess a verdict out of prose.
LEGACY = [
    "Review of #3401\n\nLooks good, the proving test asserts a value. Ship it.",
    f"Review - head {HEAD[:8]}\n\nNo findings. Meets the merge bar.",
    "Orchestrator review\n\nMerge bar met; arming auto-merge.",
    "Reviewer pass\n\nOne nit, not blocking. Approve.",
    "Agent review\n\nHold: the corpus PR has not merged yet.",
]


# --- grammar --------------------------------------------------------------

def parsed(body: str):
    return pv.parse_verdict(body)


v, bad = parsed(DOCUMENTED)
check("the documented line parses", v is not None, f"malformed={bad!r}")
if v is not None:
    check("decision", v.decision == "MERGE", v.decision)
    check("head", v.head == HEAD, v.head)
    check("patch", v.patch == PATCH, v.patch)
    check("kind", v.kind == "full", v.kind)
    check("MERGE carries no reason", v.reason is None, repr(v.reason))

v, bad = parsed(f"Some findings.\n\nVerdict: FIX-FIRST (the negative case is not covered) "
                f"— head {HEAD} — patch {PATCH} — kind: full")
check("FIX-FIRST with a reason parses", v is not None and v.decision == "FIX-FIRST", f"{v} {bad}")
check("its reason is kept", v is not None and v.reason == "the negative case is not covered",
      repr(v.reason if v else None))

v, bad = parsed(f"Verdict: HOLD (corpus PR not merged) — head {HEAD} — patch {PATCH} "
                "— kind: arm-check")
check("HOLD parses, kind arm-check",
      v is not None and v.decision == "HOLD" and v.kind == "arm-check", f"{v} {bad}")

for i, body in enumerate(LEGACY):
    v, bad = parsed(body)
    check(f"legacy style {i + 1} carries no verdict", v is None, repr(body.splitlines()[0]))

v, bad = parsed(f"{DOCUMENTED}\n\nThanks for the quick turnaround!")
check("a verdict line that is not the last line does not match", v is None, str(v))

v, bad = parsed(f"{DOCUMENTED}\n\n   \n")
check("trailing blank lines are ignored", v is not None, f"malformed={bad!r}")

v, bad = parsed(DOCUMENTED + "\r\n")
check("a CRLF body parses", v is not None, f"malformed={bad!r}")

v, bad = parsed(f"Verdict: MERGE (all good) — head {HEAD} — patch {PATCH} — kind: full")
check("MERGE with a reason is malformed, not a MERGE", v is None and bad is not None, f"{v} {bad}")

v, bad = parsed(f"Verdict: FIX-FIRST — head {HEAD} — patch {PATCH} — kind: full")
check("FIX-FIRST without a reason is malformed", v is None and bad is not None, f"{v} {bad}")

v, bad = parsed(f"Verdict: MERGE — head {HEAD[:8]} — patch {PATCH} — kind: full")
check("an abbreviated head does not match", v is None, str(v))

v, bad = parsed(f"Verdict: MERGE — head {HEAD} — patch {PATCH[:11]} — kind: full")
check("an 11-hex patch id does not match", v is None, str(v))

v, bad = parsed(f"Verdict: MERGE — head {HEAD} — patch {PATCH} — kind: quick")
check("an unknown kind does not match", v is None, str(v))

v, bad = parsed(f"**Verdict: MERGE** — head {HEAD} — patch {PATCH} — kind: full")
check("a bolded marker does not match", v is None, str(v))

# --stamp round-trips through the parser: the reviewer pastes exactly this.
line = "Verdict: MERGE " + pv.stamp(HEAD, PATCH, "full")
v, bad = parsed(line)
check("--stamp output round-trips (MERGE)",
      v is not None and v.head == HEAD and v.patch == PATCH, line)
line = "Verdict: HOLD (waiting on the corpus PR) " + pv.stamp(HEAD, PATCH, "arm-check")
v, bad = parsed(line)
check("--stamp output round-trips (HOLD, arm-check)",
      v is not None and v.kind == "arm-check" and v.decision == "HOLD", line)


# --- newest wins ----------------------------------------------------------

def comment(body: str, when: str, cid: int = 0):
    return {"body": body, "created_at": when, "id": cid}


older = "Verdict: FIX-FIRST (missing negative case) " + pv.stamp(OTHER, OTHER_PATCH, "full")
newer = "Verdict: MERGE " + pv.stamp(HEAD, PATCH, "full")
v, bad = pv.newest_verdict([comment(older, "2026-09-01T10:00:00Z", 1),
                            comment(newer, "2026-09-02T10:00:00Z", 2)])
check("the newest verdict wins", v is not None and v.decision == "MERGE", str(v))
v, bad = pv.newest_verdict([comment(newer, "2026-09-02T10:00:00Z", 2),
                            comment(older, "2026-09-03T10:00:00Z", 1)])
check("newest by created_at, not by list order",
      v is not None and v.decision == "FIX-FIRST", str(v))


# --- exit codes -----------------------------------------------------------

def fake(pr_json: dict | None, comments: list | None, *, boom: bool = False):
    """A runner keyed on the command prefix. Nothing here touches the network."""
    def runner(argv, **kw):
        if boom:
            raise OSError("gh is not installed")
        joined = " ".join(argv)
        if joined.startswith("gh pr view"):
            return (0, json.dumps(pr_json)) if pr_json is not None else (1, "no such PR")
        if "/comments" in joined:
            return (0, json.dumps(comments)) if comments is not None else (1, "404")
        return (1, "unexpected: " + joined)
    return runner


PRJSON = {"headRefOid": HEAD, "baseRefName": "main", "headRefName": "agent/fbk-1/issue-3680"}


def result(comments, *, pr_json=PRJSON, patch=PATCH, boom=False):
    return pv.evaluate("o/r", "1", runner=fake(pr_json, comments, boom=boom),
                       patch_id_of=lambda *a, **k: (patch, ""))


r = result([comment("Verdict: MERGE " + pv.stamp(HEAD, PATCH, "full"), "2026-09-02T10:00:00Z")])
check("MERGE on the current head and patch exits 0", r.exit_code == 0, str(r.exit_code))
check("...and reports both as unmoved", r.head_ok and r.patch_ok, f"{r.head_ok} {r.patch_ok}")

r = result([comment("Verdict: MERGE " + pv.stamp(OTHER, PATCH, "full"), "2026-09-02T10:00:00Z")])
check("MERGE on a stale head exits 2", r.exit_code == 2, str(r.exit_code))
check("...and says the head moved, not the patch", (not r.head_ok) and r.patch_ok,
      f"{r.head_ok} {r.patch_ok}")
check("...and the output names the head", any("head" in l for l in r.lines), str(r.lines))

r = result([comment("Verdict: MERGE " + pv.stamp(HEAD, OTHER_PATCH, "full"), "2026-09-02T10:00:00Z")])
check("MERGE on a changed patch exits 2", r.exit_code == 2, str(r.exit_code))
check("...and says the patch moved", r.head_ok and not r.patch_ok, f"{r.head_ok} {r.patch_ok}")

r = result([comment("Verdict: FIX-FIRST (no negative case) " + pv.stamp(HEAD, PATCH, "full"),
                    "2026-09-02T10:00:00Z")])
check("FIX-FIRST exits 1", r.exit_code == 1, str(r.exit_code))

r = result([comment("Verdict: HOLD (corpus PR open) " + pv.stamp(HEAD, PATCH, "full"),
                    "2026-09-02T10:00:00Z")])
check("HOLD exits 1", r.exit_code == 1, str(r.exit_code))

r = result([comment("Verdict: FIX-FIRST (x) " + pv.stamp(OTHER, OTHER_PATCH, "full"),
                    "2026-09-02T10:00:00Z")])
check("a stale FIX-FIRST still exits 1, never 0", r.exit_code == 1, str(r.exit_code))

r = result([comment(b, "2026-09-0%dT10:00:00Z" % (i + 1)) for i, b in enumerate(LEGACY)])
check("five legacy comments and no verdict line exits 3", r.exit_code == 3, str(r.exit_code))

r = result([])
check("no comments at all exits 3", r.exit_code == 3, str(r.exit_code))

r = result([comment(f"Verdict: MERGE (all good) — head {HEAD} — patch {PATCH} "
                    "— kind: full", "2026-09-02T10:00:00Z")])
check("a malformed verdict line exits 3, not 0", r.exit_code == 3, str(r.exit_code))
check("...and says malformed rather than absent",
      any("malformed" in l.lower() for l in r.lines), str(r.lines))

# A malformed line NEWER than a readable verdict must not leave the older one
# standing: a FIX-FIRST that forgot its reason would otherwise be armed as the
# MERGE below it, which is the mis-arming the grammar exists to stop.
STALE_MERGE_UNDER_MALFORMED = [
    comment("Verdict: MERGE " + pv.stamp(HEAD, PATCH, "full"), "2026-09-01T10:00:00Z", 1),
    comment(f"Verdict: FIX-FIRST — head {HEAD} — patch {PATCH} — kind: full",
            "2026-09-02T10:00:00Z", 2)]
r = result(STALE_MERGE_UNDER_MALFORMED)
check("a malformed line newer than a readable MERGE exits 3, not 0", r.exit_code == 3,
      f"{r.exit_code} {r.lines}")
check("...and says the malformed line is the newer one",
      any("NEWER" in l for l in r.lines), str(r.lines))

v, bad = pv.newest_verdict(STALE_MERGE_UNDER_MALFORMED)
check("newest_verdict reports the older verdict AND the newer malformed line",
      v is not None and bad is not None, f"{v} {bad}")

v, bad = parsed(f"**Verdict: MERGE** — head {HEAD} — patch {PATCH} — kind: full")
check("a bolded marker is reported as malformed, not absent", bad is not None, repr(bad))

r = result([comment("Verdict: MERGE " + pv.stamp(HEAD, PATCH, "full"), "2026-09-02T10:00:00Z")],
           pr_json=None)
check("an unreadable PR exits 3", r.exit_code == 3, str(r.exit_code))

r = result(None)
check("unreadable comments exit 3", r.exit_code == 3, str(r.exit_code))

r = result([comment("Verdict: MERGE " + pv.stamp(HEAD, PATCH, "full"), "2026-09-02T10:00:00Z")],
           boom=True)
check("a runner that raises exits 3, not a traceback", r.exit_code == 3, str(r.exit_code))

r = pv.evaluate("o/r", "1",
                runner=fake(PRJSON, [comment("Verdict: MERGE " + pv.stamp(HEAD, PATCH, "full"),
                                             "2026-09-02T10:00:00Z")]),
                patch_id_of=lambda *a, **k: (None, "git diff produced nothing"))
check("an uncomputable patch id exits 3, never 0", r.exit_code == 3, str(r.exit_code))

# The whole-suite invariant: nothing but a MERGE on the current head and patch
# exits 0, because exit 0 is what arms auto-merge.
greens = 0
for comments in ([comment("Verdict: MERGE " + pv.stamp(OTHER, PATCH, "full"), "2026-09-02T10:00:00Z")],
                 [comment("Verdict: FIX-FIRST (x) " + pv.stamp(HEAD, PATCH, "full"),
                          "2026-09-02T10:00:00Z")],
                 [comment(LEGACY[0], "2026-09-02T10:00:00Z")], [],
                 STALE_MERGE_UNDER_MALFORMED):
    if result(comments).exit_code == 0:
        greens += 1
check("no non-MERGE fixture exits 0", greens == 0, f"{greens} green(s)")


# --- patch-id computation -------------------------------------------------

DIFF = b"diff --git a/x b/x\n--- a/x\n+++ b/x\n@@ -1 +1 @@\n-a\n+b\n"


def brunner(argv, input=None):
    if argv[:2] == ["git", "diff"]:
        return 0, DIFF
    if argv[:2] == ["git", "patch-id"]:
        assert input == DIFF, "the diff must be piped in unchanged"
        return 0, b"0123456789abcdef0123456789abcdef01234567 " + b"0" * 40 + b"\n"
    return 1, b""


pid, note = pv.compute_patch_id("origin/main", HEAD, brunner=brunner)
check("compute_patch_id returns the first 12 hex of the patch id", pid == "0123456789ab", str(pid))


def empty_brunner(argv, input=None):
    return 0, b""


pid, note = pv.compute_patch_id("origin/main", HEAD, brunner=empty_brunner)
check("an empty patch-id is None, never a value", pid is None, str(pid))


# --- prose and parser cannot drift ---------------------------------------

reviewer = open(os.path.join(ROOT, ".claude", "agents", "reviewer.md"), encoding="utf-8").read()
check("reviewer.md carries the grammar verbatim", pv.GRAMMAR in reviewer, "GRAMMAR=" + pv.GRAMMAR)
check("reviewer.md tells the reviewer how to produce the line",
      "tools/pr-verdict.py --stamp" in reviewer)
check("reviewer.md says the verdict is the last line",
      "last line" in reviewer.lower())

print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
