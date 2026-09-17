#!/usr/bin/env python3
"""Does apply-mutation.py tell APPLIED, NOT-APPLIED and AMBIGUOUS apart?

#4316's own bar: "whatever route is taken needs a RED that distinguishes the three states a
mutation application can be in — applied, not applied, and applied more than once — because a
mutator that detects zero matches but not two has the same hole one step along."

So each state gets its own case, and each asserts the EXIT CODE rather than the message: the
code is what a calling script branches on, and a message nobody reads is not a guard.

Run: python3 tools/test_apply_mutation.py
"""
from __future__ import annotations

import importlib.util
import io
import os
import sys
import tempfile
from contextlib import redirect_stdout

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("apply_mutation", os.path.join(HERE, "apply-mutation.py"))
am = importlib.util.module_from_spec(_spec)
sys.modules["apply_mutation"] = am
_spec.loader.exec_module(am)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
        return
    FAILURES.append(name)
    print(f"  FAIL {name}" + (f" {detail}" if detail else ""))


def run(body: str, anchor: str, replacement: str) -> tuple[int, str, str]:
    """Apply into a temp file; return (exit code, printed text, resulting file body)."""
    d = tempfile.mkdtemp()
    path = os.path.join(d, "subject.cs")
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(body)
    a = os.path.join(d, "a.txt")
    r = os.path.join(d, "r.txt")
    for f, s in ((a, anchor), (r, replacement)):
        with open(f, "w", encoding="utf-8") as fh:
            fh.write(s)
    with redirect_stdout(io.StringIO()) as out:
        code = am.main(["apply-mutation.py", path, "--anchor-file", a, "--replacement-file", r])
    with open(path, encoding="utf-8") as fh:
        return code, out.getvalue(), fh.read()


print("the three states")

code, msg, body = run("alpha\nbeta\ngamma\n", "beta", "BETA")
check("a unique anchor is APPLIED", code == am.APPLIED, f"{code}: {msg}")
check("...and the file actually changed", "BETA" in body and "beta" not in body, body)

# The measured defect: a repair moved the line, so the anchor is stale.
code, msg, body = run("alpha\nbeta-was-repaired\ngamma\n", "beta\n", "BETA\n")
check("a stale anchor is NOT-APPLIED, not a silent pass", code == am.NOT_APPLIED, f"{code}: {msg}")
check("...and the file is untouched", body == "alpha\nbeta-was-repaired\ngamma\n", body)
# Pin WHICH arm answered, not only the code. The count check and the post-write re-read both
# return NOT_APPLIED, so a verdict-only assertion passes with the count check deleted -- the
# arm never fires and nothing says so. Found by mutating this file's own zero-match arm.
check("...and it is the COUNT check that says so, not the post-write re-read",
      "matched NOTHING" in msg, msg)

# The hole one step along: detecting zero but not two.
code, msg, body = run("beta\nalpha\nbeta\n", "beta", "BETA")
check("two matches are AMBIGUOUS, not a silent first-match mutation", code == am.AMBIGUOUS, f"{code}: {msg}")
check("...and the file is untouched", body == "beta\nalpha\nbeta\n", body)
# Both fixtures assert the count, not just one. Pinning it on the 3-match case alone left a
# hardcoded 3 GREEN, and a 2-match input then printed "matched 3 times" -- a wrong count handed
# to an agent deciding how far to narrow its anchor, which is the one thing this number is for.
check("...and the count is the REAL one here too, not whatever the other fixture has",
      "2 times" in msg, msg)

# Falsifying the reported count stayed GREEN in review, because the AMBIGUOUS fixture has
# exactly 2 matches and "matched 2 times" is what a hardcoded 2 also prints. Three matches
# distinguishes them: the count is what tells an agent how far to narrow the anchor.
code, msg, _ = run("beta\nbeta\nalpha\nbeta\n", "beta", "BETA")
check("three matches are AMBIGUOUS too", code == am.AMBIGUOUS, f"{code}: {msg}")
check("...and the message reports the REAL count, not a constant", "3 times" in msg, msg)

print("a second apply cannot destroy the backup")
d2 = tempfile.mkdtemp()
p2 = os.path.join(d2, "s.cs")
with open(p2, "w", encoding="utf-8") as fh:
    fh.write("line ONE\nline TWO\n")
def _files(a, r):
    fa, fr = os.path.join(d2, "a"), os.path.join(d2, "r")
    for f, s in ((fa, a), (fr, r)):
        with open(f, "w", encoding="utf-8") as fh:
            fh.write(s)
    return fa, fr
fa, fr = _files("line ONE", "line ONE-MUT")
with redirect_stdout(io.StringIO()):
    am.main(["apply-mutation.py", p2, "--anchor-file", fa, "--replacement-file", fr])
fa, fr = _files("line TWO", "line TWO-MUT")
with redirect_stdout(io.StringIO()) as out:
    rc2 = am.main(["apply-mutation.py", p2, "--anchor-file", fa, "--replacement-file", fr])
check("a second apply over a live backup is REFUSED", rc2 == am.REFUSED, f"{rc2}: {out.getvalue()}")
with redirect_stdout(io.StringIO()):
    am.main(["apply-mutation.py", p2, "--restore"])
with open(p2, encoding="utf-8") as fh:
    back = fh.read()
check("...so --restore recovers the file COMPLETELY, not just the second mutation",
      back == "line ONE\nline TWO\n", back)

print("I/O failures refuse rather than reading as NOT-APPLIED")
with redirect_stdout(io.StringIO()) as out:
    rc3 = am.main(["apply-mutation.py", os.path.join(d2, "nope.cs"),
                   "--anchor-file", os.path.join(d2, "missing"), "--replacement-file", fr])
check("a missing anchor file is REFUSED, not NOT-APPLIED", rc3 == am.REFUSED, f"{rc3}: {out.getvalue()}")
check("...because a mistyped path and a stale anchor have opposite remedies",
      rc3 != am.NOT_APPLIED, str(rc3))

print("anchor == replacement is a refusal, and strands nothing")
# Review of #4321 found this arm stayed GREEN under mutation: it was reachable (a paste error
# makes anchor and replacement the same) and it returned NOT_APPLIED *after* writing a backup,
# so it stranded one -- and the next apply then refused with "a mutation is still applied" when
# none ever was. Both halves are pinned here.
d3 = tempfile.mkdtemp()
p3 = os.path.join(d3, "s.cs")
with open(p3, "w", encoding="utf-8") as fh:
    fh.write("line ONE\nline TWO\n")
same = os.path.join(d3, "same")
with open(same, "w", encoding="utf-8") as fh:
    fh.write("line ONE")
with redirect_stdout(io.StringIO()) as out:
    rc4 = am.main(["apply-mutation.py", p3, "--anchor-file", same, "--replacement-file", same])
msg4 = out.getvalue()
check("anchor identical to replacement is REFUSED, not NOT-APPLIED", rc4 == am.REFUSED, f"{rc4}: {msg4}")
check("...because nothing was measured — no mutation was expressed at all",
      "byte-identical" in msg4, msg4)
check("...and NO backup is left stranded", not os.path.exists(p3 + am.SUFFIX),
      "a .mutation-backup survived a run that mutated nothing")
with redirect_stdout(io.StringIO()) as out:
    fa, fr = os.path.join(d3, "a"), os.path.join(d3, "r")
    for f, s in ((fa, "line TWO"), (fr, "line TWO-MUT")):
        with open(f, "w", encoding="utf-8") as fh:
            fh.write(s)
    rc5 = am.main(["apply-mutation.py", p3, "--anchor-file", fa, "--replacement-file", fr])
check("...so a REAL mutation afterwards still applies", rc5 == am.APPLIED, f"{rc5}: {out.getvalue()}")

print("a usage error is a refusal, not a measured answer")
with redirect_stdout(io.StringIO()) as out:
    rc6 = am.main(["apply-mutation.py", p3])
check("--anchor-file omitted is REFUSED", rc6 == am.REFUSED, f"{rc6}: {out.getvalue()}")
check("...not NOT-APPLIED, which would read as 'your anchor is stale'", rc6 != am.NOT_APPLIED, str(rc6))

print("every refusal arm is asserted, not merely produced")
# Rounds 2-4 of review each found the same shape: a property pinned on the member it was shown
# and left bare on the rest of its population. Round 4 found it on the REFUSAL arms -- REFUSED
# was asserted on four and merely produced on six, so three reachable arms could be flipped to a
# measured answer with the suite fully green. The worst was a failed --restore reporting success
# while the file stayed mutated, which is round 1's defect by another door.
#
# So this block enumerates the population rather than three more instances. A refusal arm added
# later and left unasserted fails the LAST check here, which is the point: an omission becomes a
# failure instead of a silence.
def _fresh():
    d = tempfile.mkdtemp()
    f = os.path.join(d, "s.cs")
    with open(f, "w", encoding="utf-8") as fh:
        fh.write("line ONE\nline TWO\n")
    a, r = os.path.join(d, "a"), os.path.join(d, "r")
    for path, s in ((a, "line ONE"), (r, "line ONE-MUT")):
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(s)
    return d, f, a, r

# A permission-based case cannot work as root or on Windows. The skip is LOUD and the
# population check above no longer has a constant to absorb it: skipping here makes those arms
# unreached, which reds. That is deliberate -- a silent skip asserting coverage is the defect
# round 5 found.
_unreadable_ok = os.name != "nt" and os.geteuid() != 0
if not _unreadable_ok:
    print(f"  NOTE permission-based refusal cases cannot run here "
          f"(os.name={os.name!r}, euid={getattr(os, 'geteuid', lambda: '?')()}); "
          f"the arms they cover will report as unreached")

def _argparse_failure():
    d, f, a, r = _fresh()
    with redirect_stdout(io.StringIO()):
        return am.main(["apply-mutation.py", f, "--no-such-flag"])

def _unreadable_target():
    d, f, a, r = _fresh()
    os.chmod(f, 0o000)
    try:
        with redirect_stdout(io.StringIO()):
            return am.main(["apply-mutation.py", f, "--anchor-file", a, "--replacement-file", r])
    finally:
        os.chmod(f, 0o644)

def _unreadable_backup():
    d, f, a, r = _fresh()
    with redirect_stdout(io.StringIO()):
        am.main(["apply-mutation.py", f, "--anchor-file", a, "--replacement-file", r])
    os.chmod(f + am.SUFFIX, 0o000)
    try:
        with redirect_stdout(io.StringIO()) as out:
            rc = am.main(["apply-mutation.py", f, "--restore"])
        with open(f, encoding="utf-8") as fh:
            still_mutated = "ONE-MUT" in fh.read()
        # A failed restore must not report success WHILE leaving the file mutated.
        check("...and a failed --restore does not report success over a still-mutated file",
              not (rc == am.APPLIED and still_mutated), f"rc={rc} mutated={still_mutated}")
        return rc
    finally:
        os.chmod(f + am.SUFFIX, 0o644)

REFUSAL_ARMS = [
    ("a missing anchor file", lambda: _missing_anchor()),
    ("a live backup blocking a second apply", lambda: _live_backup()),
    ("--restore with no backup", lambda: _no_backup()),
    ("anchor identical to replacement", lambda: _identical()),
    ("--anchor-file omitted", lambda: _omitted()),
    ("an unparseable command line", _argparse_failure),
]
if _unreadable_ok:
    REFUSAL_ARMS += [
        ("an unreadable target file", _unreadable_target),
        ("an unreadable backup during --restore", _unreadable_backup),
        ("an unwritable target file", lambda: _unwritable_target()),
        ("a backup that cannot be rolled back", lambda: _unrollbackable()),
    ]

def _unwritable_target():
    # Read succeeds, write fails: a read-only file in a writable directory.
    d, f, a, r = _fresh()
    os.chmod(f, 0o444)
    try:
        with redirect_stdout(io.StringIO()):
            return am.main(["apply-mutation.py", f, "--anchor-file", a, "--replacement-file", r])
    finally:
        os.chmod(f, 0o644)

def _unrollbackable():
    # Reaching the rollback-failed arm needs os.replace to fail with BOTH writes succeeding.
    # Locking the directory does not do it: that breaks the backup WRITE, which is upstream, so
    # the case lands on the "could not write" arm another case already covers — measured in
    # review round 5, where it printed full coverage while this arm stayed unpinned.
    #
    # So fail os.replace itself, shadowed on the module only. A real cross-device rename is the
    # production shape (EXDEV, errno 18).
    d, f, a, r = _fresh()

    class _OsReplaceFails:
        def __getattr__(self, name):
            return getattr(os, name)

        def replace(self, src, dst):
            raise OSError(18, "Invalid cross-device link")

    real_os = am.os
    am.os = _OsReplaceFails()
    try:
        with redirect_stdout(io.StringIO()):
            rc = am.main(["apply-mutation.py", f, "--anchor-file", a, "--replacement-file", a])
    finally:
        am.os = real_os
    # The backup must survive a failed rollback -- it is the only copy of the original.
    check("...and a failed rollback leaves the backup in place",
          os.path.exists(f + am.SUFFIX), "the backup vanished when the rollback failed")
    return rc

def _missing_anchor():
    d, f, a, r = _fresh()
    with redirect_stdout(io.StringIO()):
        return am.main(["apply-mutation.py", f, "--anchor-file", os.path.join(d, "nope"),
                        "--replacement-file", r])

def _live_backup():
    d, f, a, r = _fresh()
    with redirect_stdout(io.StringIO()):
        am.main(["apply-mutation.py", f, "--anchor-file", a, "--replacement-file", r])
    a2 = os.path.join(d, "a2")
    with open(a2, "w", encoding="utf-8") as fh:
        fh.write("line TWO")
    with redirect_stdout(io.StringIO()):
        return am.main(["apply-mutation.py", f, "--anchor-file", a2, "--replacement-file", r])

def _no_backup():
    d, f, a, r = _fresh()
    with redirect_stdout(io.StringIO()):
        return am.main(["apply-mutation.py", f, "--restore"])

def _identical():
    d, f, a, r = _fresh()
    with redirect_stdout(io.StringIO()):
        return am.main(["apply-mutation.py", f, "--anchor-file", a, "--replacement-file", a])

def _omitted():
    d, f, a, r = _fresh()
    with redirect_stdout(io.StringIO()):
        return am.main(["apply-mutation.py", f])

# Which arm did each case actually REACH? Counting cases proved nothing: review round 5 found
# a case whose chmod broke the backup WRITE (line 86) rather than the os.replace it named
# (line 99), so 10 sites / 10 cases printed while line 99 was unpinned -- flipping it to APPLIED
# left all 36 checks green. And forcing the platform guard off dropped four cases while the same
# line still read "10 sites, 10 cases", because a hardcoded constant stood in for them: coverage
# nobody measured, which is this tool's own defect class.
#
# So trace execution and assert the SET of refusal lines reached. That subsumes the count, needs
# no constant, and a case that does not reach its arm is a failure rather than a tally.
_AM_FILE = os.path.join(HERE, "apply-mutation.py")
_REFUSAL_LINES = {n for n, line in enumerate(open(_AM_FILE, encoding="utf-8"), 1)
                  if line.strip().startswith("return REFUSED")}

def _lines_hit(fn):
    """Run fn under a tracer; return the refusal lines in apply-mutation.py it executed."""
    hit: set[int] = set()
    real = os.path.realpath(_AM_FILE)

    def tracer(frame, event, arg):
        if event == "call":
            return tracer if os.path.realpath(frame.f_code.co_filename) == real else None
        if event == "line" and frame.f_lineno in _REFUSAL_LINES:
            hit.add(frame.f_lineno)
        return tracer

    old = sys.gettrace()
    sys.settrace(tracer)
    try:
        rc = fn()
    finally:
        sys.settrace(old)
    return rc, hit

_reached: set[int] = set()
for _name, _fn in REFUSAL_ARMS:
    _rc, _hit = _lines_hit(_fn)
    check(f"REFUSED (exit 3) for {_name}", _rc == am.REFUSED, f"{_name} did not refuse")
    check(f"...and {_name} reaches a refusal arm in apply-mutation.py", bool(_hit),
          f"{_name} refused without executing any `return REFUSED` line — it is not testing "
          f"the arm it names")
    _reached |= _hit

# The population check, keyed on lines REACHED rather than cases declared. A skipped
# platform-conditional case now shows up here as an unreached arm, because nothing stands in
# for it.
_missing = sorted(_REFUSAL_LINES - _reached)
check(f"every `return REFUSED` arm is reached by a case ({len(_reached)}/{len(_REFUSAL_LINES)})",
      not _missing,
      f"apply-mutation.py line(s) {_missing} return REFUSED and no case above executes them. "
      f"Add a case that reaches the arm — not one that merely refuses, and never a constant "
      f"standing in for a skipped case.")

print("the three codes are distinct")
check("APPLIED, NOT-APPLIED and AMBIGUOUS are three different values",
      len({am.APPLIED, am.NOT_APPLIED, am.AMBIGUOUS}) == 3,
      f"{am.APPLIED}/{am.NOT_APPLIED}/{am.AMBIGUOUS}")

print("restore")
d = tempfile.mkdtemp()
p = os.path.join(d, "s.cs")
with open(p, "w", encoding="utf-8") as fh:
    fh.write("original\n")
a, r = os.path.join(d, "a"), os.path.join(d, "r")
for f, s in ((a, "original"), (r, "mutated")):
    with open(f, "w", encoding="utf-8") as fh:
        fh.write(s)
with redirect_stdout(io.StringIO()):
    am.main(["apply-mutation.py", p, "--anchor-file", a, "--replacement-file", r])
with redirect_stdout(io.StringIO()) as out:
    rc = am.main(["apply-mutation.py", p, "--restore"])
with open(p, encoding="utf-8") as fh:
    restored = fh.read()
check("--restore puts the original back", rc == am.APPLIED and restored == "original\n", restored)
check("...and removes the backup, so a stale one cannot restore the wrong revision later",
      not os.path.exists(p + am.SUFFIX))
with redirect_stdout(io.StringIO()) as out:
    rc = am.main(["apply-mutation.py", p, "--restore"])
check("--restore with no backup refuses rather than reporting success", rc == am.REFUSED, out.getvalue())

if FAILURES:
    print(f"\n{len(FAILURES)} failed, {0} passed")
    sys.exit(1)
print("\nall checks passed")
