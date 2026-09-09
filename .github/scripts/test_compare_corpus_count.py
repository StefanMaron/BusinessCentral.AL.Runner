#!/usr/bin/env python3
"""Unit tests for compare_corpus_count.py (#3675).

The pairs that must not collapse into each other:

  * a DROP and a growth -- one fails the leg, the other is the normal state of a
    corpus that moves on its own;
  * "no previous count" and "a drop to zero" -- the first is a pass, the second
    would be the largest failure this guard can report;
  * "the results file is missing" and "the run executed no tests" -- only one of
    those is a measurement (`guards-need-a-third-state.md`).

Run: python3 .github/scripts/test_compare_corpus_count.py
"""
from __future__ import annotations

import importlib.util
import json
import os
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location(
    "compare_corpus_count", os.path.join(HERE, "compare_corpus_count.py"))
cc = importlib.util.module_from_spec(_spec)
sys.modules["compare_corpus_count"] = cc
_spec.loader.exec_module(cc)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


def results_doc(n: int) -> dict:
    return {"tests": [{"name": f"Codeunit1.Test{i}", "status": "pass"} for i in range(n)],
            "exitCode": 0}


def write(path: str, doc) -> str:
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(doc, fh)
    return path


# ------------------------------------------------------------------ compare()

rc, lines = cc.compare(3152, {"tests": 3152, "corpusSha": "a" * 40}, "b" * 40)
check("an unchanged count passes", rc == 0)
check("and says so", any("unchanged" in l for l in lines), str(lines))

rc, lines = cc.compare(3160, {"tests": 3152, "corpusSha": "a" * 40}, "b" * 40)
check("growth passes", rc == 0)
check("and is reported as +8", any("+8" in l for l in lines), str(lines))

rc, lines = cc.compare(3100, {"tests": 3152, "corpusSha": "a" * 40}, "b" * 40)
check("a drop FAILS", rc == 1)
check("and names how many tests went missing",
      any("52 FEWER" in l for l in lines), str(lines))
check("and names BOTH corpus SHAs, which is the whole point",
      any("a" * 40 in l for l in lines) and any("b" * 40 in l for l in lines), str(lines))

# One test fewer is still a drop. A guard with a tolerance would let a lost suite
# through whenever it happened to be small.
rc, _ = cc.compare(3151, {"tests": 3152}, "b" * 40)
check("a drop of ONE test still fails", rc == 1)

rc, lines = cc.compare(0, {"tests": 3152, "corpusSha": "a" * 40}, "b" * 40)
check("a drop to zero fails", rc == 1)

rc, lines = cc.compare(0, None, "b" * 40)
check("zero with NO previous count is a pass, not the biggest possible drop",
      rc == 0)
check("and says there was nothing to compare against",
      any("no previous count" in l for l in lines), str(lines))


# ------------------------------------------------------------------ read_results

with tempfile.TemporaryDirectory() as tmp:
    good = write(os.path.join(tmp, "good.json"), results_doc(7))
    check("a results document yields its test count", len(cc.read_results(good)) == 7)

    empty = write(os.path.join(tmp, "empty.json"), {"tests": [], "exitCode": 1})
    check("an EMPTY tests array is a real measurement of zero",
          cc.read_results(empty) == [])

    for name, doc in (("nokey.json", {"exitCode": 0}),
                      ("wrongtype.json", {"tests": 12}),
                      ("notadict.json", [1, 2, 3])):
        p = write(os.path.join(tmp, name), doc)
        try:
            cc.read_results(p)
            check(f"{name} refuses", False, "no ValueError raised")
        except ValueError as e:
            check(f"{name} refuses rather than counting zero",
                  "zero" in str(e) or "tests" in str(e), str(e))

    with open(os.path.join(tmp, "garbage.json"), "w", encoding="utf-8") as fh:
        fh.write("{not json")
    try:
        cc.read_results(os.path.join(tmp, "garbage.json"))
        check("unparseable JSON refuses", False, "no ValueError raised")
    except ValueError as e:
        check("unparseable JSON refuses", "not readable JSON" in str(e), str(e))

    try:
        cc.read_results(os.path.join(tmp, "absent.json"))
        check("an absent results file refuses", False, "no ValueError raised")
    except ValueError as e:
        check("an absent results file refuses, naming the drop it would fake",
              "largest possible drop" in str(e), str(e))


# ------------------------------------------------------------------ read_previous

with tempfile.TemporaryDirectory() as tmp:
    check("no --previous is None, not an error", cc.read_previous(None) is None)
    check("a --previous that does not exist is None",
          cc.read_previous(os.path.join(tmp, "nope.json")) is None)

    p = write(os.path.join(tmp, "prev.json"),
              {"corpusSha": "c" * 40, "bcVersion": "28.4", "tests": 3152})
    check("a previous count is read back", cc.read_previous(p)["tests"] == 3152)

    bad = write(os.path.join(tmp, "bad.json"), {"corpusSha": "c" * 40})
    try:
        cc.read_previous(bad)
        check("a corrupt previous count refuses", False, "no ValueError raised")
    except ValueError as e:
        check("a corrupt previous count refuses rather than reading as absent",
              "corrupt previous count" in str(e), str(e))


# ------------------------------------------------------------------ main()

with tempfile.TemporaryDirectory() as tmp:
    res = write(os.path.join(tmp, "r.json"), results_doc(10))
    out = os.path.join(tmp, "count", "corpus-count.json")

    rc = cc.main(["--results", res, "--corpus-sha", "d" * 40,
                  "--bc-version", "28.4", "--out", out])
    check("a first run with no previous count exits 0", rc == 0, f"rc={rc}")
    with open(out, encoding="utf-8") as fh:
        doc = json.load(fh)
    check("and records the count, the corpus SHA and the BC version",
          doc == {"corpusSha": "d" * 40, "bcVersion": "28.4", "tests": 10}, str(doc))

    # ...and the recorded document is exactly what the NEXT run reads as previous.
    res2 = write(os.path.join(tmp, "r2.json"), results_doc(9))
    rc = cc.main(["--results", res2, "--corpus-sha", "e" * 40, "--previous", out])
    check("the recorded document is what the next run compares against", rc == 1,
          f"rc={rc}")

    rc = cc.main(["--results", os.path.join(tmp, "gone.json"),
                  "--corpus-sha", "e" * 40, "--previous", out])
    check("a missing results file exits 3, never 1 and never 0", rc == 3, f"rc={rc}")

print()
if FAILURES:
    print(f"{len(FAILURES)} FAILED: {', '.join(FAILURES)}")
    sys.exit(1)
print("all compare_corpus_count tests passed")
