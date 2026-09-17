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
