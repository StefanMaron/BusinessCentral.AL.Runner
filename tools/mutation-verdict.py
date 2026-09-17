#!/usr/bin/env python3
"""Did a `dotnet test` run measure your mutation? Classify its console output.

    dotnet test AlRunner.Tests --filter ... > run.txt 2>&1
    tools/mutation-verdict.py run.txt          # or: ... | tools/mutation-verdict.py -

A `tools/test_*.py` guard is a PROCESS, not a dotnet suite. They print many different
summary shapes -- "all checks passed", "N passed, M failed", "PASS: ...", "OK: ..." -- so no
regex reads them all, and this tool answered UNMEASURED for every one of them (#4314). Pass
their exit code instead; it is the contract their authors wrote:

    python3 tools/test_no_racing_label_edit.py > g.txt 2>&1; rc=$?
    tools/mutation-verdict.py --exit $rc g.txt

Only 0, 1 and 3 have meanings. Anything else REFUSES rather than guessing, because a
Python traceback also exits 1 and a guard that crashed measured nothing.

A mutation check reads one number -- `Failed: N` -- and three different things
print a non-zero one (#3957, .claude/rules/tdd.md):

  exit 0  GREEN        every test that ran passed, none skipped: the mutation was NOT caught
  exit 1  RED          assertion failures, none of them the engine guard: the mutation WAS caught
  exit 3  UNMEASURED   no summary line, a filter that matched nothing, zero tests, or skips
  exit 4  BUILD-BROKE  MSBuild errors and no summary, so no test ran against the mutation (#3900)
  exit 5  ENGINE-NOT-BOOTSTRAPPED
                       failures raised by BcEngineUnbootstrappedGuard before any test work:
                       run tools/engine-test-bootstrap.sh, then --settings engine.runsettings

Exit 5 is keyed on the FIRST LINE of a failure's `Error Message:`. Neither the guard's
text anywhere in the block nor a sub-millisecond duration is enough: a mutation of the
guard itself fails the guard's own tests in < 1 ms with `REFUSING TO SKIP` nested under
`Actual:`, and that red is genuine (fixture in tools/test_mutation_verdict.py).
"""
from __future__ import annotations

import os
import re
import sys
from dataclasses import dataclass, field

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    import agent_stdio as _stdio
except Exception:  # pragma: no cover - a copy detached from its sibling module
    _stdio = None
if _stdio is not None:
    # Before any print: a failure message carries an em dash, which cp1252 stdout cannot encode.
    _stdio.enable_utf8_stdio()

GREEN, RED, UNMEASURED, BUILD_BROKE, ENGINE_NOT_BOOTSTRAPPED = 0, 1, 3, 4, 5

# Both of BcEngineUnbootstrappedGuard.AssertBootstrapWasRun's Assert.Fail messages
# (AlRunner.Tests/BcEngineCollection.cs). Change them together.
ENGINE_GUARD_TOKENS = (
    "REFUSING TO SKIP",
    "carries no recognisable cause token",
)

SUMMARY_RE = re.compile(
    r"^\s*(?:Passed|Failed|Skipped)!\s+-\s+Failed:\s*(\d+),\s*Passed:\s*(\d+),"
    r"\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)", re.M)
FAILED_HEADER_RE = re.compile(r"^\s*Failed (\S.*?) \[[^\]]*\]\s*$")
# MSBuild's shape: a diagnostic line ending in the project it belongs to. A bare ": error AL0118:"
# also appears in assertion messages about compiler output, which are genuine test failures.
BUILD_ERROR_RE = re.compile(r"^\S.*: error [A-Z]{2,}\d+: .*\[[^\]]+\.csproj\]\s*$", re.M)
NO_MATCH = "No test matches the given testcase filter"
# An unhandled Python exception exits 1, the same code a guard uses for "I caught it".
TRACEBACK_RE = re.compile(r"^Traceback \(most recent call last\):$", re.M)


@dataclass
class Result:
    verdict: int
    failed: int = 0
    passed: int = 0
    skipped: int = 0
    total: int = 0
    engine_guard: list[str] = field(default_factory=list)
    reason: str = ""


def first_message_lines(text: str) -> dict[str, str]:
    """Test name -> the first line after that failure's `Error Message:`."""
    out: dict[str, str] = {}
    lines = text.splitlines()
    current: str | None = None
    for i, line in enumerate(lines):
        m = FAILED_HEADER_RE.match(line)
        if m:
            current = m.group(1)
            continue
        if current is not None and line.strip() == "Error Message:":
            out[current] = lines[i + 1].strip() if i + 1 < len(lines) else ""
            current = None
    return out


def classify(text: str) -> Result:
    sums = SUMMARY_RE.findall(text)
    build_error = BUILD_ERROR_RE.search(text) is not None
    if not sums and build_error:
        return Result(BUILD_BROKE, reason="the build failed, so no test ran against the mutation")
    if not sums:
        why = ("the test filter matched nothing" if NO_MATCH in text
               else "no `Total:` summary line: the output is empty, truncated, or not dotnet test's")
        return Result(UNMEASURED, reason=why)

    failed, passed, skipped, total = (sum(int(s[i]) for s in sums) for i in range(4))
    r = Result(RED, failed, passed, skipped, total)

    if total == 0:
        r.verdict, r.reason = UNMEASURED, "zero tests ran"
        return r
    if build_error:
        r.verdict = UNMEASURED
        r.reason = ("a project failed to build alongside the tests that ran, so the mutated code "
                    "may not be in what was measured")
        return r

    firsts = first_message_lines(text)
    r.engine_guard = sorted(name for name, first in firsts.items()
                            if any(tok in first for tok in ENGINE_GUARD_TOKENS))
    if r.engine_guard:
        r.verdict = ENGINE_NOT_BOOTSTRAPPED
        r.reason = (f"{len(r.engine_guard)} of {failed} failure(s) came from the engine-bootstrap "
                    "guard, not from an assertion over your change")
        return r

    if failed > 0:
        if len(firsts) < failed:
            r.verdict = UNMEASURED
            r.reason = (f"`Failed: {failed}` but only {len(firsts)} failure message(s) could be read, "
                        "so the engine guard cannot be ruled out")
            return r
        r.reason = "assertion failures, none raised by the engine guard"
        return r

    if skipped > 0:
        r.verdict = UNMEASURED
        r.reason = f"{skipped} test(s) skipped: a green says nothing about whether they saw the mutation"
        return r

    r.verdict, r.reason = GREEN, "every test ran and passed"
    return r


def classify_exit(code: int, text: str = "") -> Result:
    """Classify a guard that is a PROCESS rather than a `dotnet test` suite.

    The `tools/test_*.py` guards print many different summary shapes — "all checks passed",
    "13 passed, 0 failed", "PASS: …", "OK: …" — so no regex reads them all, and scraping the
    few that happen to look parseable would answer UNMEASURED for the rest while looking like
    it worked. (Counting them here would be a snapshot that rots as guards are added; the
    argument does not need the number.) Their exit code is the contract their own authors wrote, and it is the same
    contract this tool already publishes.

    An exit code this tool has no meaning for REFUSES.

    And exit 1 is AMBIGUOUS, which is the trap: an unhandled Python exception exits 1 too, so
    "the guard failed its assertions" and "the guard crashed before judging anything" arrive as
    the same number. Reading a crash as a caught mutation is the false RED `tdd.md` warns
    about. The log is already in hand, so a traceback in it downgrades the RED to a refusal
    rather than being ignored. Found in review of #4317, where an earlier revision of this
    function refused on code 2 while citing a condition that produces code 1.
    """
    if code == GREEN:
        return Result(GREEN, reason="the guard exited 0: it passed, so the mutation was NOT caught")
    if code == RED:
        if TRACEBACK_RE.search(text):
            return Result(UNMEASURED,
                          reason="the guard exited 1, but its output carries a Python traceback: "
                                 "it crashed rather than judged, so nothing was measured")
        return Result(RED, reason="the guard exited 1: it failed, so the mutation WAS caught")
    if code == UNMEASURED:
        return Result(UNMEASURED, reason="the guard exited 3: it refused to measure")
    return Result(UNMEASURED,
                  reason=f"the guard exited {code}, which is not one of 0/1/3 — it may have "
                         f"crashed rather than judged")


NAMES = {GREEN: "GREEN", RED: "RED", UNMEASURED: "UNMEASURED",
         BUILD_BROKE: "BUILD-BROKE", ENGINE_NOT_BOOTSTRAPPED: "ENGINE-NOT-BOOTSTRAPPED"}


def main(argv: list[str]) -> int:
    # --exit N classifies a PROCESS guard (tools/test_*.py) by its exit code, because they
    # print many different summary shapes and no regex reads them all (#4314). The log is still
    # read, so the printed reason can quote it, but the VERDICT comes from the code.
    exit_code: int | None = None
    if len(argv) >= 3 and argv[1] == "--exit":
        try:
            exit_code = int(argv[2])
        except ValueError:
            print(f"--exit wants an integer, got {argv[2]!r}")
            return 2
        argv = [argv[0], *argv[3:]]
    if len(argv) != 2 or argv[1] in ("-h", "--help"):
        print(__doc__)
        return 2
    text = sys.stdin.read() if argv[1] == "-" else open(argv[1], encoding="utf-8", errors="replace").read()
    r = classify_exit(exit_code, text) if exit_code is not None else classify(text)
    print(f"{NAMES[r.verdict]}: {r.reason}")
    if r.total:
        print(f"  Failed: {r.failed}, Passed: {r.passed}, Skipped: {r.skipped}, Total: {r.total}")
    for name in r.engine_guard:
        print(f"  engine guard: {name}")
    if r.verdict == ENGINE_NOT_BOOTSTRAPPED:
        print("  remedy: build Release, run tools/engine-test-bootstrap.sh, re-run with "
              "--settings engine.runsettings -- again after EVERY build")
    return r.verdict


if __name__ == "__main__":
    sys.exit(main(sys.argv))
