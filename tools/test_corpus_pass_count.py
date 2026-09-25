#!/usr/bin/env python3
"""Unit tests for tools/corpus-pass-count.py's log parsing and classification.

Every fixture below is **verbatim from a real corpus run** -- run 34079169063
(corpus PR #227, prefix `TestPart_`) for the passing spellings and the OnPrem
suite, run 34073808538 for the failing ones. That matters more than usual here:
the defect this tool closes is a parser written against one leg's spelling and
believed on all of them, so a synthetic fixture shaped to satisfy the regex
would test the author's idea of the log rather than the log (#3311).

Run: python3 tools/test_corpus_pass_count.py
"""
from __future__ import annotations

import importlib.util
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location(
    "corpus_pass_count", os.path.join(HERE, "corpus-pass-count.py"))
cpc = importlib.util.module_from_spec(_spec)
sys.modules["corpus_pass_count"] = cpc
_spec.loader.exec_module(cpc)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


# --------------------------------------------------------------------------
# Recorded fixtures. Copied byte-for-byte, including the leading timestamp and
# the indentation, which differs between the two BC families and is part of
# what a hand-written pattern gets wrong.
# --------------------------------------------------------------------------

# BC 27.0 leg (job 101611063472): `PASS  Name` -- two spaces, no timing.
LOG_27X = """2026-09-07T03:24:39.8545666Z     PASS  TestPart_Visible_AnswersTrueForAReachablePart
2026-09-07T03:24:39.8546083Z     PASS  TestPart_InvisiblePart_IsNotInTheControlTreeAtAll
2026-09-07T03:24:39.8546817Z     PASS  TestPart_Enabled_AnswersTrueForAReachablePart
2026-09-07T03:24:39.8547274Z     PASS  TestPart_Editable_IsNotDrivenByTheHostControlsEditableProperty
2026-09-07T03:27:42.7740689Z 2915 total, 2915 passed, 0 failed, 0 skipped, 0 codeunit error(s) in 411s
"""

# BC 28.4 leg (job 101611063464): `PASS Name (240ms)` -- one space, a duration.
LOG_28X = """2026-09-07T03:26:47.4624638Z       PASS TestPart_Visible_AnswersTrueForAReachablePart (240ms)
2026-09-07T03:26:47.4624821Z       PASS TestPart_InvisiblePart_IsNotInTheControlTreeAtAll (703ms)
2026-09-07T03:26:47.4624990Z       PASS TestPart_Enabled_AnswersTrueForAReachablePart (282ms)
2026-09-07T03:26:47.4625228Z       PASS TestPart_Editable_IsNotDrivenByTheHostControlsEditableProperty (766ms)
2026-09-07T03:26:47.5267023Z 2948 total, 2948 passed, 0 failed, 0 skipped, 0 codeunit error(s) in 357s
"""

# BC OnPrem 27.0 leg (job 101611063560): green, ran a 29-test suite, and none of
# it is TestPart_. A zero here is expected and means something different.
LOG_ONPREM = """2026-09-07T03:20:59.4650313Z     PASS  Object_Get_UnknownKey_RaisesRecordNotFound
2026-09-07T03:20:59.4653118Z     PASS  Object_HoldsNoRows_WhileObjectMetadataDoes
2026-09-07T03:21:01.6546957Z 29 total, 29 passed, 0 failed, 0 skipped, 0 codeunit error(s) in 10s
"""

# Run 34073808538, a 28.x leg: FAIL carries the 28.x spelling too.
LOG_28X_FAIL = """2026-09-07T01:49:19.7778707Z       FAIL FilterPageBuilder_AddTable_ZeroTableId_Throws (308ms)
2026-09-07T01:49:19.7781836Z       FAIL FilterPageBuilder_Name_IndexZero_Throws (129ms)
2026-09-07T01:49:19.7790000Z       PASS FilterPageBuilder_AddTable_KnownTable_Succeeds (44ms)
2026-09-07T01:52:00.0000000Z 2915 total, 2911 passed, 4 failed, 0 skipped, 0 codeunit error(s) in 299s
"""

# Run 34073808538, the BC 27.5 leg: FAIL carries the 27.x spelling, and the
# detail after the name must not be mistaken for another test.
LOG_27X_FAIL = """2026-09-07T01:50:40.0711423Z     FAIL  FilterPageBuilder_AddTable_ZeroTableId_Throws — Assert.ExpectedError failed. Expected: . Actual: The filter page table ID must be a positive number greater than zero..
2026-09-07T01:50:40.0745038Z     FAIL  FilterPageBuilder_Name_IndexZero_Throws — Assert.ExpectedError failed. Expected: . Actual: The filter control index value 0 is out of range.
2026-09-07T01:52:00.0000000Z 2915 total, 2913 passed, 2 failed, 0 skipped, 0 codeunit error(s) in 299s
"""

# A leg that died before the test phase. No PASS/FAIL, no summary line -- so a
# zero from it says nothing about anyone's tests.
LOG_NO_SUITE = """2026-09-07T03:06:36.2318892Z Starting BC container
2026-09-07T03:08:33.0862095Z error AL1018: Could not resolve dependency
"""


# --------------------------------------------------------------------------
# 1. Both spellings, and the exact false zero this closes.
# --------------------------------------------------------------------------
print("both log spellings")

r27 = cpc.parse_leg(LOG_27X, "TestPart_")
r28 = cpc.parse_leg(LOG_28X, "TestPart_")

check("the 27.x spelling (`PASS  Name`, two spaces, no timing) yields 4",
      len(r27["passed"]) == 4, str(r27["passed"]))
check("the 28.x spelling (`PASS Name (240ms)`) yields 4 as well",
      len(r28["passed"]) == 4, str(r28["passed"]))
check("...and both legs name the SAME four tests",
      r27["passed"] == r28["passed"], f"{r27['passed']} vs {r28['passed']}")

# The proof that the fixtures really do differ -- otherwise the two checks above
# could both pass against one spelling accidentally pasted twice.
check("the recorded fixtures genuinely carry different spellings",
      "PASS  TestPart_Visible" in LOG_27X
      and "PASS  TestPart_" not in LOG_28X
      and "PASS TestPart_" in LOG_28X
      and "ms)" in LOG_28X and "ms)" not in LOG_27X)

# This is #3311 variant 1, held as an executable statement: the pattern an agent
# actually wrote reports zero on a green leg that ran everything.
check("a FIXED two-space pattern would report 0 on the 28.x leg (the bug)",
      LOG_28X.count("PASS  TestPart_") == 0)
check("...while the parser reports 4 there",
      len(r28["passed"]) == 4)

# The 28.x timing suffix must not be swallowed into the name.
check("the (240ms) suffix is not part of the parsed name",
      "TestPart_Visible_AnswersTrueForAReachablePart" in r28["passed"],
      str(r28["passed"]))


# --------------------------------------------------------------------------
# 2. Distinct names, not matching lines.
# --------------------------------------------------------------------------
print("\ndistinct names, not lines")

dup = LOG_27X + LOG_27X
rdup = cpc.parse_leg(dup, "TestPart_")
check("a log carrying every line twice still counts 4, not 8",
      len(rdup["passed"]) == 4, str(len(rdup["passed"])))


# --------------------------------------------------------------------------
# 3. The three meanings of zero -- the point of the tool.
# --------------------------------------------------------------------------
print("\nzero has three different meanings")

ronprem = cpc.parse_leg(LOG_ONPREM, "TestPart_")
rnosuite = cpc.parse_leg(LOG_NO_SUITE, "TestPart_")

check("a leg that ran the codeunit classifies as `ran`",
      cpc.classify(r27) == "ran", cpc.classify(r27))
check("a green OnPrem leg that ran a different suite is `not-run`, not a failure",
      cpc.classify(ronprem) == "not-run", cpc.classify(ronprem))
check("...and it is recognised as having run a suite (29 tests) all the same",
      ronprem["total"] == {"total": 29, "passed": 29, "failed": 0, "skipped": 0},
      str(ronprem["total"]))
check("a leg that never reached the test phase is `no-suite`, a different answer",
      cpc.classify(rnosuite) == "no-suite", cpc.classify(rnosuite))
check("...and reports no suite total at all, rather than zero",
      rnosuite["total"] is None, str(rnosuite["total"]))
check("`not-run` and `no-suite` are distinguished, not collapsed",
      cpc.classify(ronprem) != cpc.classify(rnosuite))

# The guessed-prefix case (#3311 variant 2): a wrong prefix against a leg that
# demonstrably ran 2915 tests must not look like "the codeunit did not run".
rwrong = cpc.parse_leg(LOG_27X, "TPart_")
check("a wrong prefix on a leg that ran 2915 tests yields 0 passes",
      len(rwrong["passed"]) == 0)
check("...but the leg is still seen to have run a suite, so the zero is "
      "attributable",
      rwrong["total"]["total"] == 2915 and rwrong["suite_names"] == 4,
      str(rwrong))


# --------------------------------------------------------------------------
# 4. Failures are surfaced, in both spellings.
# --------------------------------------------------------------------------
print("\nfailures, both spellings")

f28 = cpc.parse_leg(LOG_28X_FAIL, "FilterPageBuilder_")
f27 = cpc.parse_leg(LOG_27X_FAIL, "FilterPageBuilder_")

check("the 28.x FAIL spelling is picked up (2 failed, 1 passed)",
      len(f28["failed"]) == 2 and len(f28["passed"]) == 1,
      f"{f28['failed']} / {f28['passed']}")
check("the 27.x FAIL spelling is picked up too",
      len(f27["failed"]) == 2, str(f27["failed"]))
check("a leg with failures classifies as `failed`, never as `ran`",
      cpc.classify(f28) == "failed" and cpc.classify(f27) == "failed")
check("the em-dash detail after a 27.x FAIL is not parsed as another test",
      f27["failed"] == ["FilterPageBuilder_AddTable_ZeroTableId_Throws",
                        "FilterPageBuilder_Name_IndexZero_Throws"],
      str(f27["failed"]))
check("a failed test is not also counted as passed",
      not (set(f28["failed"]) & set(f28["passed"])))


# --------------------------------------------------------------------------
# 5. Anti-stub checks -- tdd.md. These are the ones a gutted implementation
#    must not survive.
# --------------------------------------------------------------------------
print("\na stub implementation must not survive")

# A parser returning a constant count fails: the same fixture yields different
# counts for different prefixes, and different fixtures yield different counts.
check("a constant-count stub is refused: same log, two prefixes, two answers",
      len(cpc.parse_leg(LOG_27X, "TestPart_")["passed"]) == 4
      and len(cpc.parse_leg(LOG_27X, "TestPart_Visible")["passed"]) == 1)
check("...and different logs yield different counts",
      len(cpc.parse_leg(LOG_ONPREM, "Object_")["passed"]) == 2
      and len(cpc.parse_leg(LOG_27X, "TestPart_")["passed"]) == 4)

# An always-zero stub fails on every `ran` fixture above; state it directly so
# the intent is legible rather than implied.
check("an always-zero stub is refused: three fixtures report non-zero",
      all(len(cpc.parse_leg(lg, px)["passed"]) > 0
          for lg, px in ((LOG_27X, "TestPart_"), (LOG_28X, "TestPart_"),
                         (LOG_ONPREM, "Object_"))))

# An always-`ran` classifier fails, and so does an always-`not-run` one.
kinds = {cpc.classify(r27), cpc.classify(ronprem), cpc.classify(rnosuite),
         cpc.classify(f28)}
check("classify() returns four distinct verdicts across the four fixtures",
      kinds == {"ran", "not-run", "no-suite", "failed"}, str(sorted(kinds)))


# --------------------------------------------------------------------------
# 6. The pattern itself, and the flag whose absence is its own false zero.
# --------------------------------------------------------------------------
print("\nthe parser's own shape")

check("the result pattern uses a `+` quantifier on whitespace, not a fixed run",
      r"\s+" in cpc._RESULT.pattern, cpc._RESULT.pattern)
check("...so it cannot be a literal two-space or one-space match",
      "PASS  " not in cpc._RESULT.pattern)

# gh writes NOTHING and exits 0 on an ANSI-carrying log without this flag --
# measured on this very run while building the tool. An empty file reads as
# "no matches", so the flag is load-bearing, not cosmetic.
import inspect  # noqa: E402
src = inspect.getsource(cpc.fetch_log)
check("fetch_log passes --allow-escape-sequences (without it gh emits nothing, "
      "exit 0)",
      "--allow-escape-sequences" in src)
check("...and treats an empty body as unavailable rather than as zero passes",
      "not out.strip()" in src)


# --------------------------------------------------------------------------
# 7. AL Runner's own output is a THIRD spelling (#3357).
#
# preflight.py's corpus-baseline check parses a local run with this same
# parser rather than hand-rolling a second one, and a local run names a test
# `Codeunit65551.Method`, not `Method`. Captured verbatim from
#   dotnet run --project AlRunner -c Release -- tests/runner-extras/object-system-table \
#     --package-cache ~/.al-runner/platform-apps --show-pass
# on 2026-09-07.
# --------------------------------------------------------------------------
print("\nAL Runner's local per-test spelling")

LOG_LOCAL = """\
=== object-system-table ===
PASS  Codeunit65551.Object_IsEmpty_WhileAllObj_StillListsTheSameObjects (45ms)
PASS  Codeunit65551.EmptyObject_StillAnswersOnAllFourRequestPaths (10ms)
PASS  Codeunit65551.OemTextColumns_ReadAsEmptyText_RatherThanRaising (1ms)
"""

rloc = cpc.parse_leg(LOG_LOCAL, "Codeunit65551.")
check("a dotted local name is captured WHOLE, not truncated at the codeunit",
      rloc["passed"] == [
          "Codeunit65551.EmptyObject_StillAnswersOnAllFourRequestPaths",
          "Codeunit65551.Object_IsEmpty_WhileAllObj_StillListsTheSameObjects",
          "Codeunit65551.OemTextColumns_ReadAsEmptyText_RatherThanRaising"],
      str(rloc["passed"]))
check("...so three tests in one codeunit count as three, not as one",
      len(rloc["passed"]) == 3, str(len(rloc["passed"])))

# An expectations-reclassified pass carries a qualifier between the verdict and
# the name. Skipping those undercounts by exactly the number of oos/known-gap
# entries in tests/expectations/ -- a shortfall that looks like a real one.
LOG_QUALIFIED = """\
=== al-language ===
PASS (oos) Codeunit60101.SendEmail_IsOutOfScope (2ms)
PASS (known-gap) Codeunit60101.TestPageBlankTemporal (3ms)
PASS (divergence) Codeunit60101.SessionIdDiverges (1ms)
PASS  Codeunit60101.PlainPass (1ms)
FAIL  Codeunit60101.Broken (4ms)
"""
rq = cpc.parse_leg(LOG_QUALIFIED, "Codeunit60101.")
check("PASS (oos) / (known-gap) / (divergence) all count as passes",
      len(rq["passed"]) == 4, str(rq["passed"]))
check("...and a FAIL next to them is still a FAIL",
      rq["failed"] == ["Codeunit60101.Broken"], str(rq["failed"]))

# The additive half: the corpus fixtures must parse to exactly what they did
# before the pattern learned the local spelling.
#
# The expectation is a LITERAL, transcribed from the fixtures above, and that is
# the whole point of it (#3361). Both of these used to read
# `cpc.parse_leg(LOG_27X, ...)["passed"] == r27["passed"]`, comparing one call
# against another call of the same parser in the same process -- `x == x`, green
# under a parser returning an empty list. They were cited as #3359's evidence that
# widening `_RESULT` is additive for corpus logs, and they were the only two checks
# in this file that survived a deliberate revert of that widening.
CORPUS_FIXTURE_NAMES = [
    "TestPart_Editable_IsNotDrivenByTheHostControlsEditableProperty",
    "TestPart_Enabled_AnswersTrueForAReachablePart",
    "TestPart_InvisiblePart_IsNotInTheControlTreeAtAll",
    "TestPart_Visible_AnswersTrueForAReachablePart",
]
check("the 27.x corpus fixture parses to the four recorded names, unchanged by "
      "the widened pattern",
      r27["passed"] == CORPUS_FIXTURE_NAMES, str(r27["passed"]))
check("the 28.x corpus fixture parses to those same four names too",
      r28["passed"] == CORPUS_FIXTURE_NAMES, str(r28["passed"]))
check("a bare corpus name is still captured without a dot",
      all("." not in n for n in r27["passed"]), str(r27["passed"]))

# --------------------------------------------------------------------------
# 8. Every REQUIRED leg, read from the corpus ruleset (#4593).
#
# The corpus's required legs are whatever its ruleset says -- nine since
# 2026-09-25, when `BC 28.5 / test` was added -- and a required leg that did not
# run the codeunit is the finding, whether it ran another suite, never reached
# the test phase, or is absent from the run altogether. Before this, one leg
# running the codeunit was enough for exit 0.
# --------------------------------------------------------------------------
print("\nevery required leg, from the ruleset")

REQ = ["BC 27.0 / test", "BC 28.4 / test", "BC 28.5 / test"]


def _row(name, log, prefix="TestPart_"):
    leg = cpc.parse_leg(log, prefix)
    return ({"id": name, "name": name, "conclusion": "success"}, leg, cpc.classify(leg))


ROWS_ALL = [_row("BC 27.0 / test", LOG_27X), _row("BC 28.4 / test", LOG_28X),
            _row("BC 28.5 / test", LOG_28X), _row("BC OnPrem 27.0 / test", LOG_ONPREM)]

code, lines = cpc.required_leg_verdict(ROWS_ALL, REQ, "")
check("every required leg ran it and the OnPrem leg did not: exit 0",
      code == 0, f"{code} {lines}")

code, lines = cpc.required_leg_verdict(ROWS_ALL[:2] + ROWS_ALL[3:], REQ, "")
check("a required leg ABSENT from the run is a finding (exit 1) -- the run predates it",
      code == 1, f"{code} {lines}")
check("...and the line names that leg",
      any("BC 28.5 / test" in l and "absent" in l for l in lines), str(lines))

code, lines = cpc.required_leg_verdict(
    ROWS_ALL[:2] + [_row("BC 28.5 / test", LOG_ONPREM)] + ROWS_ALL[3:], REQ, "")
check("a required leg that ran a suite WITHOUT the codeunit is a finding (exit 1)",
      code == 1 and any("BC 28.5 / test" in l for l in lines), f"{code} {lines}")

code, lines = cpc.required_leg_verdict(
    ROWS_ALL[:2] + [_row("BC 28.5 / test", LOG_NO_SUITE)] + ROWS_ALL[3:], REQ, "")
check("a required leg that never reached the test phase is a finding (exit 1)",
      code == 1, f"{code} {lines}")

code, lines = cpc.required_leg_verdict(
    ROWS_ALL[:2] + [({"id": "x", "name": "BC 28.5 / test", "conclusion": "success"},
                     None, "log-unavailable")] + ROWS_ALL[3:], REQ, "")
check("a required leg whose log could not be fetched is UNMEASURED (exit 3), not a pass",
      code == 3, f"{code} {lines}")

code, lines = cpc.required_leg_verdict(ROWS_ALL, None, "HTTP 403")
check("a ruleset that could not be read is exit 3 -- never 0, never 1",
      code == 3 and any("HTTP 403" in l for l in lines), f"{code} {lines}")

code, _ = cpc.required_leg_verdict(ROWS_ALL, ["BC OnPrem 27.0 / test"], "")
check("the verdict follows the ruleset it is handed, not a built-in list",
      code == 1, str(code))


def _main_with(rows_by_name, required, why=""):
    """Drive main() end to end with the three GitHub reads stubbed."""
    saved = (cpc.fetch_jobs, cpc.fetch_log, cpc.required_legs, sys.argv)
    logs = {n: lg for n, lg in rows_by_name}
    cpc.fetch_jobs = lambda run_id: [{"id": n, "name": n, "conclusion": "success"}
                                     for n, _ in rows_by_name]
    cpc.fetch_log = lambda job_id: logs[job_id]
    cpc.required_legs = lambda repo: (required, why)
    sys.argv = ["corpus-pass-count.py", "1", "TestPart_"]
    buf = io.StringIO()
    out = sys.stdout
    try:
        sys.stdout = buf
        rc = cpc.main()
    finally:
        sys.stdout = out
        cpc.fetch_jobs, cpc.fetch_log, cpc.required_legs, sys.argv = saved
    return rc, buf.getvalue()


import io  # noqa: E402

EIGHT_LEG_RUN = [("BC 27.0 / test", LOG_27X), ("BC 28.4 / test", LOG_28X),
                 ("BC OnPrem 27.0 / test", LOG_ONPREM)]
rc, out = _main_with(EIGHT_LEG_RUN, REQ)
check("main(): a run with no leg for a required context exits 1 (was 0: one leg ran it)",
      rc == 1 and "BC 28.5 / test" in out, f"rc={rc}\n{out}")
rc, out = _main_with(EIGHT_LEG_RUN + [("BC 28.5 / test", LOG_28X)], REQ)
check("main(): the same run with that leg present exits 0", rc == 0, f"rc={rc}\n{out}")
rc, out = _main_with(EIGHT_LEG_RUN + [("BC 28.5 / test", LOG_28X)], None, "no gh auth")
check("main(): an unreadable ruleset exits 3 even when every leg ran it",
      rc == 3, f"rc={rc}\n{out}")

check("no leg count survives in the tool's prose",
      not any(w in open(os.path.join(HERE, "corpus-pass-count.py")).read().lower()
              for w in ("eight onprem", "eight cloud", "eight required")))

print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
