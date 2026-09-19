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

print("a binary older than the last --restore (#4343)")
# The trap this exists for fails toward a RED, which is the opposite direction from every
# other mutation trap in tdd.md. A mutation restored in the SOURCE is still live in the
# BINARY until something rebuilds, so `--no-build` after a restore reds precisely the arm the
# mutation targeted -- deterministically, and again when the class is run alone, because the
# binary does not change. Re-derived on this repository before this test was written: five
# identical `Failed: 2` runs after a clean restore of a cross-kind leak in
# RecordPatches.CodeunitSubscriberWitness.cs; one rebuild gave 2/2.
#
# The assertion is about the VERDICT, not a warning string: a test that pinned only the
# message would pass while a caller still read `RED` as a result, which is the same shape as
# every other defect in this family (#4343).
import tempfile as _tf
import time as _time

_d = _tf.mkdtemp()
_dll = os.path.join(_d, "AlRunner.Tests.dll")
with open(_dll, "w", encoding="utf-8") as _fh:
    _fh.write("binary")
_stamp = os.path.join(_d, mv.STAMP_NAME)


def _log(body: str) -> str:
    """A dotnet-test log whose first line names our temp assembly, as dotnet test's does."""
    return f"Test run for {_dll} (.NETCoreApp,Version=v8.0)\n" + body


def _set_times(built: float, restored: float) -> tuple[float | None, str]:
    os.utime(_dll, (built, built))
    with open(_stamp, "w", encoding="utf-8") as fh:
        fh.write(f"{restored:.6f}\n/repo/AlRunner/Patches/Whatever.cs\n")
    return mv.read_restore_stamp(_d)

_now = _time.time()

# The defect: binary built 60s BEFORE the restore, and the log is a convincing narrow red.
_red_body = fixture("genuine-red-guard-mutation.txt")
_stale = mv.classify(_log(_red_body), _set_times(_now - 60, _now))
check("a RED from a binary older than the last restore is UNMEASURED, not RED",
      _stale.verdict == mv.UNMEASURED, f"got {mv.NAMES[_stale.verdict]}: {_stale.reason}")
check("  ...and the reason names the output directory and the rebuild",
      os.path.dirname(_dll) in _stale.reason and "Rebuild" in _stale.reason, _stale.reason)

# THE CONTROL. Same log, same stamp, binary built AFTER the restore: a real verdict must
# survive, or the check has only been proved able to refuse, never to discriminate.
_fresh = mv.classify(_log(_red_body), _set_times(_now, _now - 60))
check("CONTROL: the same RED from a binary built AFTER the restore is still RED",
      _fresh.verdict == mv.RED, f"got {mv.NAMES[_fresh.verdict]}: {_fresh.reason}")

# A GREEN is invalidated by a stale binary just as a RED is: the binary measured the mutant,
# so "the mutation was not caught" is a statement about code the source no longer has.
_green_stale = mv.classify(_log(fixture("green.txt")), _set_times(_now - 60, _now))
check("a GREEN from a stale binary is UNMEASURED too, not GREEN",
      _green_stale.verdict == mv.UNMEASURED, f"got {mv.NAMES[_green_stale.verdict]}: {_green_stale.reason}")
_green_fresh = mv.classify(_log(fixture("green.txt")), _set_times(_now, _now - 60))
check("CONTROL: ...and a GREEN from a fresh binary is still GREEN",
      _green_fresh.verdict == mv.GREEN, f"got {mv.NAMES[_green_fresh.verdict]}: {_green_fresh.reason}")

# The check must not fire when no mutation was restored, which is almost every run. A guard
# that refused without a stamp would refuse everything (guards-need-a-third-state.md: a
# genuinely absent thing stays a pass).
os.remove(_stamp)
check("no stamp at all is not a refusal: read_restore_stamp answers None",
      mv.read_restore_stamp(_d)[0] is None, repr(mv.read_restore_stamp(_d)))
_no_stamp = mv.classify(_log(_red_body), (None, ""))
check("...and a RED with no stamp anywhere is still RED",
      _no_stamp.verdict == mv.RED, f"got {mv.NAMES[_no_stamp.verdict]}: {_no_stamp.reason}")

# An UNREADABLE stamp is the third state, deliberately not folded into "absent": a restore
# happened and when is unknown, which is not safe to proceed on, while no restore is.
with open(_stamp, "w", encoding="utf-8") as _fh:
    _fh.write("not-a-timestamp\n")
_unreadable = mv.read_restore_stamp(_d)
check("an unreadable stamp is NOT read as absent", _unreadable[0] == float("inf"), repr(_unreadable))
_u = mv.classify(_log(_red_body), _unreadable)
check("...and it refuses the verdict rather than reporting the RED",
      _u.verdict == mv.UNMEASURED, f"got {mv.NAMES[_u.verdict]}: {_u.reason}")
os.remove(_stamp)

# The false-refusal mode, and the one that cost a round trip to find: the mutated code usually
# lives in a DEPENDENCY, so rebuilding it leaves the named test assembly untouched -- no test
# source changed. Measured end-to-end here: after a restore at 02:03:20, `dotnet build` wrote
# al-runner.dll at 02:03:52 while AlRunner.Tests.dll stayed at 01:54:28. Keying on the named
# assembly alone refused that correctly-rebuilt run, which is this check's own version of the
# defect it exists to catch.
_dep = os.path.join(_d, "al-runner.dll")
with open(_dep, "w", encoding="utf-8") as _fh:
    _fh.write("dependency")
os.utime(_dll, (_now - 600, _now - 600))          # test assembly: untouched by the rebuild
with open(_stamp, "w", encoding="utf-8") as _fh:
    _fh.write(f"{_now - 60:.6f}\n/repo/AlRunner/Patches/Whatever.cs\n")
os.utime(_dep, (_now, _now))                      # the dependency IS newer than the restore
_dep_rebuilt = mv.classify(_log(_red_body), mv.read_restore_stamp(_d))
check("a rebuilt DEPENDENCY clears the refusal, though the named test assembly is older",
      _dep_rebuilt.verdict == mv.RED, f"got {mv.NAMES[_dep_rebuilt.verdict]}: {_dep_rebuilt.reason}")

# ...and the discrimination survives it: with NOTHING in the directory rebuilt, it still refuses.
os.utime(_dep, (_now - 600, _now - 600))
_none_rebuilt = mv.classify(_log(_red_body), mv.read_restore_stamp(_d))
check("...and with no assembly in the directory newer than the restore it still refuses",
      _none_rebuilt.verdict == mv.UNMEASURED, f"got {mv.NAMES[_none_rebuilt.verdict]}: {_none_rebuilt.reason}")
os.remove(_dep)
os.remove(_stamp)

# THE CONTROL THIS CHECK MOST NEEDED, and the one whose absence let the defect ship: a
# mutation in a file no build reads. `--restore` stamps EVERY mutation, so keying the refusal
# on mtimes alone made it unclearable for those -- the demanded rebuild is a legitimate no-op,
# nothing gains an mtime, and the refusal then poisons unrelated later runs until someone
# deletes the stamp by hand. Measured on the first revision: mutate tools/mutation-verdict.py,
# restore, `dotnet build` (exit 0, 2.06s no-op), still UNMEASURED with gap=261s; a second
# rebuild did not move it. Not a corner case -- 41 tools/test_*.py guards, 20
# .github/scripts/test_*, and the PR that introduced this check is itself such a change.
#
# Same shape as the dependency case above, one population further out: refusal arms all pass
# while an honest path is refused, so only a control can find it (#4343, found in review).
os.utime(_dll, (_now - 600, _now - 600))          # nothing in the directory is newer
for _mutated, _label in (("tools/mutation-verdict.py", "a Python tool"),
                         ("tools/test_no_racing_label_edit.py", "a tools/test_*.py guard"),
                         (".claude/rules/tdd.md", "a markdown rule"),
                         ("tests/expectations/known-gaps-x.json", "a JSON manifest")):
    with open(_stamp, "w", encoding="utf-8") as _fh:
        _fh.write(f"{_now:.6f}\n{_mutated}\n")
    _r = mv.classify(_log(_red_body), mv.read_restore_stamp(_d))
    check(f"CONTROL: a restored mutation in {_label} still gets a real verdict",
          _r.verdict == mv.RED,
          f"{_mutated} -> {mv.NAMES[_r.verdict]}: {_r.reason}. No rebuild can clear this, so "
          f"refusing would be permanent rather than corrective")

# ...and the discrimination survives: a build input with the same stale directory still refuses.
for _mutated, _label in (("AlRunner/Patches/RecordPatches.CodeunitSubscriberWitness.cs", ".cs"),
                         ("AlRunner.Tests/AlRunner.Tests.csproj", ".csproj"),
                         ("Directory.Build.props", ".props")):
    with open(_stamp, "w", encoding="utf-8") as _fh:
        _fh.write(f"{_now:.6f}\n{_mutated}\n")
    _r = mv.classify(_log(_red_body), mv.read_restore_stamp(_d))
    check(f"...and a restored mutation in {_label} STILL refuses, because a rebuild clears it",
          _r.verdict == mv.UNMEASURED, f"{_mutated} -> {mv.NAMES[_r.verdict]}: {_r.reason}")

# The predicate itself, both directions, so a later editor cannot widen it by accident.
for _path in ("a/b.cs", "X.CSPROJ", "d.props", "e.targets", "f.sln", "f.slnx", "g.resx"):
    check(f"is_build_input({_path!r}) is True", mv.is_build_input(_path))
for _path in ("t.py", "r.md", "m.json", "s.sh", "w.yml", "n.al", "x"):
    check(f"is_build_input({_path!r}) is False", not mv.is_build_input(_path))

# Pin the list against the TREE, not against a hand-written roster, because a missing entry is
# the dangerous direction: "not listed" means "skip the staleness check", so an omitted build
# input silently restores the original defect for that file type. The first revision listed
# `.sln` -- which this repository does not have -- and omitted `.slnx`, which four workflows
# build (#4343, review round 2). A spare entry costs only a refusal a real rebuild clears, so
# this asserts coverage of what exists rather than equality with it.
# Three outcomes, not two. `git ls-files` RAISING and `git ls-files` succeeding with EMPTY
# stdout are different events with the same falsy value, and only the first was handled: an
# empty read made `if _tracked:` skip every assertion below and the run reported all-passed
# (#4343, review round 4). Empty-but-successful is reachable -- `git init` a directory and
# `git ls-files` exits 0 with zero bytes -- so this is the census that pins the list passing
# over nothing, which is the "green because it never looked" shape the rest of this PR is
# about. A repository with zero tracked files is not a repository whose build inputs are all
# covered; it is one nobody measured.
import subprocess as _sp
_root = os.path.dirname(HERE)
_tracked, _why = "", ""
try:
    _tracked = _sp.run(["git", "ls-files"], cwd=_root, capture_output=True, text=True,
                       check=True).stdout
    if not _tracked.strip():
        _why = "git ls-files succeeded but listed no tracked files"
except (OSError, _sp.SubprocessError) as _exc:
    _why = f"git ls-files could not be run: {_exc}"
check("the build-input list could be checked against the tree", not _why,
      f"{_why} — the assertions below pin BUILD_INPUT_SUFFIXES against what this repository "
      f"actually holds, and none of them ran. Refusing rather than reporting a pass over an "
      f"empty population")
if not _why:
    # Every extension a .NET build compiles that this repository actually HAS must be listed.
    _have = {("." + _l.rsplit(".", 1)[-1].lower()) for _l in _tracked.split("\n")
             if "." in _l.rsplit("/", 1)[-1]}
    # The census is worthless if it inspected nothing recognisable, so say what it saw.
    check("the tree census found the extensions this repository is known to hold",
          {".cs", ".csproj"} <= _have,
          f"git ls-files returned {len(_tracked.splitlines())} path(s) but no .cs/.csproj among "
          f"them — the census is reading the wrong tree, so its passes mean nothing")
    for _ext in (".cs", ".csproj", ".props", ".targets", ".slnx", ".sln"):
        if _ext in _have:
            check(f"{_ext} exists in the tree and IS treated as a build input",
                  mv.is_build_input("x" + _ext),
                  f"{_ext} is tracked here but not in BUILD_INPUT_SUFFIXES, so a mutation in one "
                  f"skips the staleness check and a stale binary answers for it")
    check("the repository's solution file is a build input",
          not any(_l.endswith(".slnx") for _l in _tracked.split("\n")) or mv.is_build_input("a.slnx"),
          "AlRunner.slnx is tracked and built by four workflows but is not a build input here")

# A stamp with NO second line is from a writer predating the field, or truncated. It must stay
# conservative -- treated as a build input -- rather than skipping the staleness question, which
# would quietly disable the whole check for anyone holding an older stamp.
with open(_stamp, "w", encoding="utf-8") as _fh:
    _fh.write(f"{_now:.6f}\n")
_legacy = mv.classify(_log(_red_body), mv.read_restore_stamp(_d))
check("a stamp naming no path is treated as a build input, not as absent",
      _legacy.verdict == mv.UNMEASURED, f"got {mv.NAMES[_legacy.verdict]}: {_legacy.reason}")
os.remove(_stamp)

# A log that does not name its assembly cannot be judged stale -- absence of evidence. The
# caller keeps its ordinary verdict rather than refusing on a file it never identified.
_unnamed = mv.classify(_red_body, (_now, "/repo/.mutation-restore-stamp"))
check("a log naming no assembly keeps its verdict rather than refusing",
      _unnamed.verdict == mv.RED, f"got {mv.NAMES[_unnamed.verdict]}: {_unnamed.reason}")

# The same refusal must reach the PROCESS-guard path. A tools/test_*.py guard is classified by
# its exit code and never parses a summary, so a staleness check wired only into classify()
# would leave --exit answering a confident RED off the same stale binary.
_proc_stale = mv.classify_exit(1, _log("12 passed, 1 failed"), _set_times(_now - 60, _now))
check("--exit 1 from a stale binary is UNMEASURED, not RED",
      _proc_stale.verdict == mv.UNMEASURED, f"got {mv.NAMES[_proc_stale.verdict]}: {_proc_stale.reason}")
_proc_fresh = mv.classify_exit(1, _log("12 passed, 1 failed"), _set_times(_now, _now - 60))
check("CONTROL: --exit 1 from a fresh binary is still RED",
      _proc_fresh.verdict == mv.RED, f"got {mv.NAMES[_proc_fresh.verdict]}: {_proc_fresh.reason}")

# And the CLI, which is what an agent actually invokes, reaches the refusal and prints the
# remedy. A verdict only reachable from the library would not protect the documented recipe.
_set_times(_now - 60, _now)
_logfile = os.path.join(_d, "run.txt")
with open(_logfile, "w", encoding="utf-8") as _fh:
    _fh.write(_log(_red_body))
_cwd = os.getcwd()
try:
    os.chdir(_d)
    with redirect_stdout(io.StringIO()) as out:
        _rc = mv.main(["mutation-verdict.py", _logfile])
finally:
    os.chdir(_cwd)
check("CLI: a stale-binary RED exits 3 (UNMEASURED), not 1", _rc == mv.UNMEASURED,
      f"got {_rc}: {out.getvalue()}")
check("CLI: ...and prints the rebuild remedy", "rebuild" in out.getvalue().lower(), out.getvalue())

# apply-mutation.py must actually WRITE the stamp this tool reads, or the two halves agree on
# a filename and nothing ever produces one. Cross-file contract, same shape as the SUFFIX /
# .gitignore pin in test_apply_mutation.py.
_am_spec = importlib.util.spec_from_file_location("apply_mutation", os.path.join(HERE, "apply-mutation.py"))
_am = importlib.util.module_from_spec(_am_spec)
_am_spec.loader.exec_module(_am)
check("apply-mutation.py and mutation-verdict.py name the same stamp file",
      _am.STAMP_NAME == mv.STAMP_NAME, f"{_am.STAMP_NAME!r} vs {mv.STAMP_NAME!r}")

_d2 = _tf.mkdtemp()
_src = os.path.join(_d2, "s.cs")
for _name, _body in ((_src, "original\n"), (os.path.join(_d2, "a"), "original"),
                     (os.path.join(_d2, "r"), "mutated")):
    with open(_name, "w", encoding="utf-8") as _fh:
        _fh.write(_body)
with redirect_stdout(io.StringIO()):
    _am.main(["apply-mutation.py", _src, "--anchor-file", os.path.join(_d2, "a"),
              "--replacement-file", os.path.join(_d2, "r")])
_before = _time.time()
with redirect_stdout(io.StringIO()) as out:
    _rc = _am.main(["apply-mutation.py", _src, "--restore"])
_written = _am.stamp_path(_src)
check("--restore still succeeds and now writes a stamp",
      _rc == _am.APPLIED and os.path.exists(_written), f"rc={_rc} stamp={_written}: {out.getvalue()}")
check("...and mutation-verdict.py reads back a time at or after the restore",
      (mv.read_restore_stamp(_d2)[0] or 0) >= _before - 1, repr(mv.read_restore_stamp(_d2)))
check("...and the message tells the reader to rebuild", "REBUILD" in out.getvalue(), out.getvalue())

if FAILURES:
    print(f"\n{len(FAILURES)} failure(s)")
    sys.exit(1)
print("\nall passed")
