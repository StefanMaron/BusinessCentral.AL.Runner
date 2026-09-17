#!/usr/bin/env python3
"""Unit tests for tools/mutation-verdict.py (#3957).

The fixtures under tools/testdata/mutation-verdict/ are recorded `dotnet test` output from
one box on 2026-09-12, with the checkout path rewritten to /repo; build-break.txt keeps only
the restore lines and the error line of a 661-line log. Two are what this tool exists to
tell apart, and both fail in under a millisecond with `REFUSING TO SKIP` in the text:

  engine-guard-unbootstrapped.txt  BcEngineReadinessGuardTests on a box with artifacts but no
                                   tools/engine-test-bootstrap.sh run: the guard, not a mutation
  genuine-red-guard-mutation.txt   BcEngineUnbootstrappedGuardTests with the guard's
                                   IsRecoverableLocally check inverted: a real mutation RED

genuine-red-quoting-al-diagnostic.txt is DotNetCompilationTargetScopeTests.CloudTarget_* with one
assertion added, `Assert.DoesNotContain(": error AL0296:", output)`: a real failure whose message
quotes an AL compiler diagnostic, which an unanchored build-error pattern read as a build break.

Cases marked "derived" edit a recorded fixture in memory, and say what they changed.

Run: python3 tools/test_mutation_verdict.py
"""
from __future__ import annotations

import importlib.util
import io
import os
import sys
from contextlib import redirect_stdout

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("mutation_verdict", os.path.join(HERE, "mutation-verdict.py"))
mv = importlib.util.module_from_spec(_spec)
sys.modules["mutation_verdict"] = mv
_spec.loader.exec_module(mv)

FIXTURES = os.path.join(HERE, "testdata", "mutation-verdict")
FAILURES: list[str] = []


def fixture(name: str) -> str:
    with open(os.path.join(FIXTURES, name), encoding="utf-8") as f:
        return f.read()


def check(name: str, cond: bool, detail: str = "") -> None:
    print(f"  {'ok  ' if cond else 'FAIL'} {name}{'' if cond else ' ' + detail}")
    if not cond:
        FAILURES.append(name)


def expect(name: str, text: str, verdict: int) -> "mv.Result":
    r = mv.classify(text)
    check(name, r.verdict == verdict,
          f"expected {mv.NAMES[verdict]}, got {mv.NAMES.get(r.verdict, r.verdict)}: {r.reason}")
    return r


print("recorded fixtures")
r = expect("unbootstrapped engine guard is not a mutation RED",
           fixture("engine-guard-unbootstrapped.txt"), mv.ENGINE_NOT_BOOTSTRAPPED)
check("  names the guarded test",
      r.engine_guard == ["AlRunner.Tests.BcEngineReadinessGuardTests.Ready_IsTrue_WhenArtifactsAreProvisioned"],
      repr(r.engine_guard))
check("  keeps the counts", (r.failed, r.passed, r.total) == (1, 3, 4), repr((r.failed, r.passed, r.total)))

r = expect("a mutation of the guard itself is a genuine RED, though its text says REFUSING TO SKIP",
           fixture("genuine-red-guard-mutation.txt"), mv.RED)
check("  the fixture really carries the token and a sub-ms failure",
      "REFUSING TO SKIP" in fixture("genuine-red-guard-mutation.txt")
      and "[< 1 ms]" in fixture("genuine-red-guard-mutation.txt"))
check("  counts 9 of 29", (r.failed, r.total) == (9, 29), repr((r.failed, r.total)))

expect("an inverted readiness guard is a genuine RED", fixture("genuine-red-readiness-mutation.txt"), mv.RED)
expect("compiler errors are BUILD-BROKE, not RED", fixture("build-break.txt"), mv.BUILD_BROKE)
r = expect("a genuine red whose assertion quotes `: error AL0296:` is RED, not BUILD-BROKE",
           fixture("genuine-red-quoting-al-diagnostic.txt"), mv.RED)
check("  the fixture really quotes an AL diagnostic",
      ": error AL0296:" in fixture("genuine-red-quoting-al-diagnostic.txt"))
expect("a clean run is GREEN", fixture("green.txt"), mv.GREEN)
r = expect("a filter matching nothing is UNMEASURED, though dotnet exits 0",
           fixture("no-filter-match.txt"), mv.UNMEASURED)
check("  says the filter matched nothing", "filter matched nothing" in r.reason, r.reason)

print("derived cases")
engine = fixture("engine-guard-unbootstrapped.txt")
expect("derived: engine fixture with its summary line cut off is UNMEASURED",
       "\n".join(l for l in engine.splitlines() if "Total:" not in l), mv.UNMEASURED)
expect("derived: empty input is UNMEASURED", "", mv.UNMEASURED)
expect("derived: a green run beside an MSBuild error line is UNMEASURED",
       fixture("green.txt") + fixture("build-break.txt").splitlines()[-1] + "\n", mv.UNMEASURED)
expect("derived: green fixture with Passed 0 / Skipped 3 is UNMEASURED",
       fixture("green.txt").replace("Passed:     3, Skipped:     0", "Passed:     0, Skipped:     3"),
       mv.UNMEASURED)
expect("derived: green fixture with Total 0 is UNMEASURED",
       fixture("green.txt").replace("Passed:     3, Skipped:     0, Total:     3",
                                    "Passed:     0, Skipped:     0, Total:     0"), mv.UNMEASURED)
expect("derived: engine fixture with its Error Message block removed cannot rule the guard out",
       engine.replace("  Error Message:\n", ""), mv.UNMEASURED)
unknown = engine.replace(
    "REFUSING TO SKIP: BC artifacts are provisioned on this machine",
    "the in-process BC engine is not ready and the reason carries no recognisable cause token")
expect("derived: the guard's unknown-cause message is also ENGINE-NOT-BOOTSTRAPPED", unknown,
       mv.ENGINE_NOT_BOOTSTRAPPED)
genuine = fixture("genuine-red-readiness-mutation.txt")
expect("derived: one engine-guard failure alongside genuine ones still refuses the RED",
       genuine.replace("Assert.NotNull() Failure: Value is null",
                       "[bc-engine-serial] REFUSING TO SKIP: BC artifacts are provisioned", 1),
       mv.ENGINE_NOT_BOOTSTRAPPED)

print("the CLI")
for name, code in (("engine-guard-unbootstrapped.txt", 5), ("genuine-red-guard-mutation.txt", 1),
                   ("build-break.txt", 4), ("genuine-red-quoting-al-diagnostic.txt", 1), ("green.txt", 0), ("no-filter-match.txt", 3)):
    with redirect_stdout(io.StringIO()) as out:
        rc = mv.main(["mutation-verdict.py", os.path.join(FIXTURES, name)])
    check(f"exit {code} for {name}", rc == code, f"got {rc}: {out.getvalue()}")
with redirect_stdout(io.StringIO()) as out:
    mv.main(["mutation-verdict.py", os.path.join(FIXTURES, "engine-guard-unbootstrapped.txt")])
check("ENGINE-NOT-BOOTSTRAPPED prints the remedy", "tools/engine-test-bootstrap.sh" in out.getvalue(),
      out.getvalue())

print("a guard that is a PROCESS, not a dotnet suite (#4314)")
# The tools/test_*.py guards print many different summary shapes -- "all checks passed",
# "13 passed, 0 failed", "PASS: ...", "OK: ..." -- so no second regex can read them. What they
# DO share is an exit code, which is a stronger signal than scraped text: it is the contract the
# guard's own author wrote, not a format that happens to be parseable.
for code, want, label in ((0, mv.GREEN, "exit 0 is GREEN"),
                          (1, mv.RED, "exit 1 is RED"),
                          (3, mv.UNMEASURED, "exit 3 stays UNMEASURED")):
    r = mv.classify_exit(code, "13 passed, 0 failed (3 file(s) carried both flag names)")
    check(f"process guard: {label}", r.verdict == want, f"got {mv.NAMES[r.verdict]}: {r.reason}")

# The 3 arm and the fallthrough BOTH answer UNMEASURED, so a verdict check cannot tell them
# apart and deleting the 3 arm passes every test (found in review of #4317). What distinguishes
# them is the reason an agent reads: "it refused to measure" sends you to the guard, "may have
# crashed" sends you to the run. Pin the reason, not only the code.
r3 = mv.classify_exit(3, "")
check("process guard: exit 3 says the guard REFUSED, not that it may have crashed",
      "refused to measure" in r3.reason and "crashed" not in r3.reason, r3.reason)

# Exit 1 is ambiguous: an unhandled Python exception exits 1 too. A traceback in the log the
# tool already read downgrades the RED, or a crashed guard reads as a caught mutation.
crash = mv.classify_exit(1, "Traceback (most recent call last):\n  File \"g.py\", line 1\nRuntimeError: boom")
check("process guard: exit 1 WITH a traceback refuses rather than claiming a catch",
      crash.verdict == mv.UNMEASURED, f"got {mv.NAMES[crash.verdict]}: {crash.reason}")
genuine = mv.classify_exit(1, "12 passed, 1 failed (3 file(s) carried both flag names)")
check("process guard: ...and a genuine red without one is still RED",
      genuine.verdict == mv.RED, f"got {mv.NAMES[genuine.verdict]}: {genuine.reason}")

# ...and the half that makes the traceback check usable at all: `unittest` prints a line-start
# traceback for EVERY ordinary assertion failure, so the traceback alone cannot separate
# "crashed" from "caught". Three guards in this repo are unittest-based, and the first version
# of the downgrade turned their genuine reds into refusals -- #4314's own defect on a new
# population. A run that reached its verdict prints `Ran N tests`; one that died does not.
unittest_red = mv.classify_exit(1, (
    "F\n======================================================================\n"
    "FAIL: test_x (__main__.T.test_x)\n"
    "Traceback (most recent call last):\n"
    '  File "/tmp/ut.py", line 3, in test_x\n'
    "AssertionError: 1 != 2 : an ordinary caught mutation\n\n"
    "----------------------------------------------------------------------\n"
    "Ran 1 test in 0.000s\n\nFAILED (failures=1)"))
check("process guard: an ordinary unittest failure is RED despite its traceback",
      unittest_red.verdict == mv.RED, f"got {mv.NAMES[unittest_red.verdict]}: {unittest_red.reason}")

# A non-zero code the tool has no meaning for must NOT be read as RED: an unhandled traceback
# exits 1 in Python, but a guard that crashed measured nothing. Anything else refuses.
crashed = mv.classify_exit(2, "Traceback (most recent call last):\n  ...\nValueError: boom")
check("process guard: an unexpected exit code refuses rather than guessing",
      crashed.verdict == mv.UNMEASURED, f"got {mv.NAMES[crashed.verdict]}: {crashed.reason}")

# And the CLI reaches it, so an agent does not have to know which classifier applies.
for code, want in ((0, 0), (1, 1), (3, 3)):
    with redirect_stdout(io.StringIO()) as out:
        rc = mv.main(["mutation-verdict.py", "--exit", str(code), os.devnull])
    check(f"CLI --exit {code} answers {want}", rc == want, f"got {rc}: {out.getvalue()}")

if FAILURES:
    print(f"\n{len(FAILURES)} failure(s)")
    sys.exit(1)
print("\nall passed")
