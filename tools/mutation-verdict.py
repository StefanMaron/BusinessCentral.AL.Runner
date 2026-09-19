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

A run whose test binary is OLDER than the last `apply-mutation.py --restore` is UNMEASURED,
whatever it printed (#4343). A mutation restored in the source is still live in the binary
until something rebuilds, so `--no-build` after a restore produces a red that is
deterministic, narrow, on the right arm for the hypothesis, and survives running the class
alone -- every property that normally ENDS an investigation. Measured on this repository:
five identical `Failed: 2` runs after a clean restore; one rebuild gave 2/2.

It is the only trap in this family that fails toward a RED. The rest fail toward a green
that reads as coverage, and a reader told to distrust a surprising green has no defence
here, because a red is what a reviewer is hunting.
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
# ...but `unittest` prints a line-start traceback for every ORDINARY assertion failure, so the
# traceback alone cannot separate "crashed" from "caught". What does: a run that reached its
# verdict prints its own summary, and one that died before judging does not.
JUDGED_RE = re.compile(r"^Ran \d+ tests?\b", re.M)
# `dotnet test` names the assembly it ran on its first line. That is the only thing in the log
# that identifies the BINARY the verdict is about, which is the question a staleness check asks
# -- the summary line names `AlRunner.Tests.dll` without a path, and a filter names source
# symbols that may live in a different assembly from the mutation.
TEST_RUN_FOR_RE = re.compile(r"^Test run for (.+?\.dll)\s*\(", re.M)
STAMP_NAME = ".mutation-restore-stamp"


# What a rebuild can actually put into the build output. A mutation in anything else -- a
# Python guard, a markdown rule, a JSON manifest -- leaves nothing for `dotnet build` to
# rewrite, so no assembly gains an mtime and a staleness check keyed on mtimes could never
# clear (#4343, found in review). Extensions rather than a directory list, because
# `AlRunner.Tests/` holds `.cs` AND `.json` fixtures and only the first is compiled.
#
# The direction of a wrong entry is asymmetric, which is why this list is checked rather than
# guessed: a MISSING extension silently restores the original defect for that file type (the
# check is skipped and a stale binary answers), while a SPARE one costs only a refusal that a
# real rebuild clears. So err toward listing.
#
# `.slnx` and not `.sln`: this repository's solution is `AlRunner.slnx`, which four workflows
# build, and there is no `.sln` at all -- the first revision listed the one that does not exist
# and omitted the one that does (#4343, review round 2). Counted with `git ls-files`: .cs 1223,
# .csproj 7, .props 1, .targets 1, .slnx 1, .sln 0. `.sln`, `.resx` and `.razor` are kept for
# the asymmetry above; they are zero here today and free to carry.
BUILD_INPUT_SUFFIXES = (".cs", ".csproj", ".props", ".targets",
                        ".slnx", ".sln", ".resx", ".razor")


def is_build_input(path: str) -> bool:
    """Can `dotnet build` turn a change to this file into a new assembly?

    The question the staleness check must ask FIRST. `--restore` stamps every mutation,
    including files no build reads; demanding a rebuild for those prints a remedy that cannot
    work and refuses every later run until the stamp is deleted by hand.
    """
    return path.lower().endswith(BUILD_INPUT_SUFFIXES)


def read_restore_stamp(start: str | None = None) -> tuple[float | None, str]:
    """The time of the last `apply-mutation.py --restore`, or None if there was none.

    Returns (when, detail). `None` is the ordinary case and a PASS: most runs follow no
    mutation at all, and a check that refused without one would refuse everything
    (guards-need-a-third-state.md -- a genuinely absent thing stays a pass).

    `None` is ALSO the answer when the stamped file is not a build input, for the same
    reason: no rebuild can clear it, so refusing would be permanent rather than corrective.
    The stamp's second line carries that path -- it was written from the first revision and
    read by nothing, which is what made the defect possible (#4343, found in review).

    Looked up by walking up from `start` to the `.git` entry, matching where
    `apply-mutation.py` writes it. A stamp that exists but cannot be parsed is NOT treated as
    absent: it is reported so the caller can refuse, because "no mutation was restored" and
    "a restore happened and I cannot tell when" have opposite consequences and only the first
    is safe to proceed on.
    """
    d = os.path.abspath(start or os.getcwd())
    seen = []
    while True:
        cand = os.path.join(d, STAMP_NAME)
        if os.path.exists(cand):
            seen.append(cand)
            break
        if os.path.exists(os.path.join(d, ".git")):
            break
        parent = os.path.dirname(d)
        if parent == d:
            break
        d = parent
    if not seen:
        return None, ""
    stamp = seen[0]
    try:
        with open(stamp, encoding="utf-8") as fh:
            first = fh.readline().strip()
            mutated = fh.readline().strip()
        when = float(first)
    except (OSError, ValueError) as exc:
        return float("inf"), f"{stamp} exists but could not be read ({exc})"
    # A stamp naming no path is from a writer that predates this field, or a truncated file.
    # Treat it as a build input -- the conservative direction, since that is the case the
    # check exists for -- rather than silently skipping the staleness question entirely.
    if mutated and not is_build_input(mutated):
        return None, ""
    return when, stamp


def stale_binary(text: str, stamp_when: float | None, stamp_detail: str) -> str:
    """Why this run's binary cannot carry the current source, or "" if it can.

    Compares the mtime of the assembly the log names against the restore stamp. An assembly
    the log does not name, or one that is no longer on disk, yields "" -- absence of evidence,
    and the caller keeps its ordinary verdict. Only a binary demonstrably OLDER than the
    restore refuses; that is the case with an actual wrong answer behind it.
    """
    if stamp_when is None:
        return ""
    if stamp_when == float("inf"):
        return (f"a mutation restore stamp is present but unreadable ({stamp_detail}), so "
                f"whether this run's binary predates the restore could not be established")
    m = TEST_RUN_FOR_RE.search(text)
    if not m:
        return ""
    dll = m.group(1)
    out_dir = os.path.dirname(dll)
    if not os.path.isdir(out_dir):
        return ""

    # Read the NEWEST assembly in the output directory, not the one the log names. The mutated
    # code usually lives in a DEPENDENCY -- `al-runner.dll` here -- and rebuilding it leaves
    # AlRunner.Tests.dll untouched, because no test source changed. Keying on the named
    # assembly alone refuses a run that was correctly rebuilt, which is this check's own
    # false-refusal mode and the mirror of the defect it exists to catch
    # (guards-need-a-third-state.md: a genuinely absent thing must stay a pass).
    #
    # Measured while building this: after a restore at 02:03:20, `dotnet build` produced
    # al-runner.dll at 02:03:52 while AlRunner.Tests.dll stayed at 01:54:28 -- a correct
    # rebuild that the named-assembly form called stale.
    newest = 0.0
    try:
        for name in os.listdir(out_dir):
            if name.endswith(".dll"):
                newest = max(newest, os.path.getmtime(os.path.join(out_dir, name)))
    except OSError:
        return ""
    if newest == 0.0 or newest >= stamp_when:
        return ""
    return (f"every assembly in {out_dir} was built at least {stamp_when - newest:.0f}s BEFORE "
            f"the last `apply-mutation.py --restore` ({stamp_detail}), so the binary still "
            f"carries the mutation the source no longer has. Rebuild and re-run — --no-build "
            f"measured the mutant")


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


def classify(text: str, stamp: tuple[float | None, str] | None = None) -> Result:
    # The staleness check runs BEFORE anything reads the numbers, because it invalidates every
    # one of them equally: a binary that predates the restore measured the mutant, so its RED,
    # its GREEN and its counts are all about code the source no longer has. Ordering it after
    # the summary parse would let the ENGINE_NOT_BOOTSTRAPPED and BUILD_BROKE arms return a
    # verdict on that binary.
    when, detail = stamp if stamp is not None else read_restore_stamp()
    why = stale_binary(text, when, detail)
    if why:
        return Result(UNMEASURED, reason=why)

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


def classify_exit(code: int, text: str = "",
                  stamp: tuple[float | None, str] | None = None) -> Result:
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
    about. The log is already in hand, so a traceback in it downgrades the RED to a refusal —
    UNLESS the run also printed a summary of its own (`Ran N tests`), because `unittest` emits a
    line-start traceback for every ordinary assertion failure. A guard that reached its verdict
    says so; one that died before judging does not. Both halves found in review of #4317: the
    first because an earlier revision refused on code 2 while citing a condition that produces
    code 1, the second because the fix for that regressed three unittest-based guards.
    """
    # Defence in depth, and honestly bounded: this cannot fire on the input it is documented
    # for. `stale_binary` needs the `Test run for …dll (` line, and measured over all 41
    # tools/test_*.py guards, ZERO emit it -- they are processes, not dotnet suites. It is
    # kept because --exit also accepts a dotnet log (the CLI reads one either way) and
    # because the alternative is a classifier that silently cannot refuse; it is NOT a claim
    # that Python guards are protected. What protects those is the build-input gate in
    # read_restore_stamp, which stops their stamps refusing anything at all (#4343).
    when, detail = stamp if stamp is not None else read_restore_stamp()
    why = stale_binary(text, when, detail)
    if why:
        return Result(UNMEASURED, reason=why)

    if code == GREEN:
        return Result(GREEN, reason="the guard exited 0: it passed, so the mutation was NOT caught")
    if code == RED:
        if TRACEBACK_RE.search(text) and not JUDGED_RE.search(text):
            return Result(UNMEASURED,
                          reason="the guard exited 1, but its output carries a Python traceback "
                                 "and no run summary: it crashed rather than judged, so nothing "
                                 "was measured")
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
    if "apply-mutation.py --restore" in r.reason or "restore stamp" in r.reason:
        print("  remedy: rebuild, then re-run. A restored mutation stays live in the binary, "
              "so --no-build re-measures the mutant and reds the same arm every time")
    return r.verdict


if __name__ == "__main__":
    sys.exit(main(sys.argv))
