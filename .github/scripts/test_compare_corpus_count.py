#!/usr/bin/env python3
"""Unit tests for compare_corpus_count.py (#3675).

Every fixture here is the REAL shape the runner's `--count-out` writes, pinned by
`AlRunner.Tests/CountOutTests.cs` on the other side. That is not decoration: the
first version of this script read `--out`, which is a failure report and carries
no counts, and its fixtures were invented to match — so the suite was green while
every BC leg refused the document (run 34412771210). A fixture that does not come
from the producer proves the author, not the producer.

The pairs that must not collapse into each other:

  * a DROP and a growth -- one fails the leg, the other is the normal state of a
    corpus that moves on its own;
  * a PER-SUITE drop and a level total -- one suite's tests vanishing into
    another's is invisible to a total-only comparison;
  * "no previous count" and "a drop to zero" -- the first is a pass, the second
    would be the largest failure this guard can report;
  * "no `suites` key" and "an empty `suites` object" -- only one of those is a
    measurement (`guards-need-a-third-state.md`).

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


def count_doc(**suites: int) -> dict:
    """A `--count-out` document, in the shape AlRunner.Infrastructure.CountOut writes."""
    return {"bcVersion": "27.0",
            "suites": {k.replace("_", "-"): {"tests": v, "appGroups": 1}
                       for k, v in suites.items()}}


def write(path: str, doc) -> str:
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(doc, fh, indent=2)
    return path


def prev(corpus_sha: str, **suites: int) -> dict:
    return {"suites": {k.replace("_", "-"): v for k, v in suites.items()},
            "corpusSha": corpus_sha}


# ------------------------------------------------------------------ compare()

rc, lines = cc.compare({"al-language": 3123}, prev("a" * 40, al_language=3123), "b" * 40)
check("an unchanged count passes", rc == 0)
check("and says so", any("unchanged" in l for l in lines), str(lines))

rc, lines = cc.compare({"al-language": 3131}, prev("a" * 40, al_language=3123), "b" * 40)
check("growth passes", rc == 0)
check("and is reported as +8", any("+8" in l for l in lines), str(lines))

rc, lines = cc.compare({"al-language": 3100}, prev("a" * 40, al_language=3152), "b" * 40)
check("a drop FAILS", rc == 1)
check("and names how many tests went missing",
      any("52 FEWER" in l for l in lines), str(lines))
check("and names the SUITE that shrank, not just a total",
      any("al-language" in l and "FEWER" in l for l in lines), str(lines))
check("and names BOTH corpus SHAs, which is the whole point",
      any("a" * 40 in l for l in lines) and any("b" * 40 in l for l in lines), str(lines))

# The case a total-only comparison cannot see: one suite's tests vanish, another's
# grow by the same amount, and the run-wide total is untouched.
rc, lines = cc.compare({"al-language": 3152, "al-language-onprem": 0},
                       prev("a" * 40, al_language=3123, al_language_onprem=29), "b" * 40)
check("a PER-SUITE drop fails even when the run-wide total is level", rc == 1)
check("...naming the suite that lost them",
      any("al-language-onprem" in l and "FEWER" in l for l in lines), str(lines))

# A suite disappearing entirely is a drop to zero, not an absence to shrug at.
rc, lines = cc.compare({"al-language": 3123},
                       prev("a" * 40, al_language=3123, al_language_onprem=29), "b" * 40)
check("a suite that stops being reported at all is a drop", rc == 1)

# ...and a NEW suite is growth, reported and allowed.
rc, lines = cc.compare({"al-language": 3123, "brand-new": 4},
                       prev("a" * 40, al_language=3123), "b" * 40)
check("a new suite is growth, not a finding", rc == 0)
check("...and is named", any("brand-new" in l for l in lines), str(lines))

# One test fewer is still a drop. A guard with a tolerance would let a lost suite
# through whenever it happened to be small.
rc, _ = cc.compare({"al-language": 3151}, prev("a" * 40, al_language=3152), "b" * 40)
check("a drop of ONE test still fails", rc == 1)

rc, _ = cc.compare({"al-language": 0}, prev("a" * 40, al_language=3152), "b" * 40)
check("a drop to zero fails", rc == 1)

rc, lines = cc.compare({}, None, "b" * 40)
check("zero with NO previous count is a pass, not the biggest possible drop", rc == 0)
check("and says there was nothing to compare against",
      any("no previous count" in l for l in lines), str(lines))
check("...as a ::warning::, so it reaches the run summary and the annotations",
      any(l.startswith("::warning::") for l in lines), str(lines))


# ------------------------------------------------------------------ read_measured

with tempfile.TemporaryDirectory() as tmp:
    good = write(os.path.join(tmp, "good.json"), count_doc(al_language=7, runner_extras=2))
    suites, bcver = cc.read_measured(good)
    check("a count document yields its per-suite counts",
          suites == {"al-language": 7, "runner-extras": 2}, str(suites))
    check("...and the BC version it recorded", bcver == "27.0", bcver)

    empty = write(os.path.join(tmp, "empty.json"), {"bcVersion": "27.0", "suites": {}})
    check("an EMPTY suites object is a real measurement of zero",
          cc.read_measured(empty)[0] == {})

    # THE defect: `--out` is a failure report. It must refuse, not count zero.
    out_doc = write(os.path.join(tmp, "al-language-results.json"), {
        "generated": "2026-09-10T00:00:00Z", "total_failures": 0,
        "classifications": {}, "all_failures": []})
    try:
        cc.read_measured(out_doc)
        check("the runner's --out failure report refuses", False, "no ValueError raised")
    except ValueError as e:
        check("the runner's --out failure report refuses rather than counting zero",
              "suites" in str(e), str(e))
        check("...and says pointing this at --out is the mistake",
              "--out" in str(e), str(e))

    for name, doc in (("wrongtype.json", {"suites": 12}),
                      ("notadict.json", [1, 2, 3]),
                      ("badsuite.json", {"suites": {"x": {"appGroups": 1}}})):
        p = write(os.path.join(tmp, name), doc)
        try:
            cc.read_measured(p)
            check(f"{name} refuses", False, "no ValueError raised")
        except ValueError as e:
            check(f"{name} refuses rather than counting zero", True, str(e))

    with open(os.path.join(tmp, "garbage.json"), "w", encoding="utf-8") as fh:
        fh.write("{not json")
    try:
        cc.read_measured(os.path.join(tmp, "garbage.json"))
        check("unparseable JSON refuses", False, "no ValueError raised")
    except ValueError as e:
        check("unparseable JSON refuses", "not readable JSON" in str(e), str(e))

    try:
        cc.read_measured(os.path.join(tmp, "absent.json"))
        check("an absent count file refuses", False, "no ValueError raised")
    except ValueError as e:
        check("an absent count file refuses, naming the drop it would fake",
              "largest possible drop" in str(e), str(e))


# ------------------------------------------------------------------ read_previous

with tempfile.TemporaryDirectory() as tmp:
    check("no --previous is None, not an error", cc.read_previous(None) is None)
    check("a --previous that does not exist is None",
          cc.read_previous(os.path.join(tmp, "nope.json")) is None)

    p = write(os.path.join(tmp, "prev.json"),
              {"corpusSha": "c" * 40, "bcVersion": "28.4", "tests": 3152,
               "suites": {"al-language": {"tests": 3152}}})
    got = cc.read_previous(p)
    check("a previous record is read back per suite",
          got["suites"] == {"al-language": 3152}, str(got))
    check("...with the corpus SHA it was measured at",
          got["corpusSha"] == "c" * 40, str(got))

    bad = write(os.path.join(tmp, "bad.json"), {"corpusSha": "c" * 40})
    try:
        cc.read_previous(bad)
        check("a corrupt previous record refuses", False, "no ValueError raised")
    except ValueError as e:
        check("a corrupt previous record refuses rather than reading as absent",
              "suites" in str(e), str(e))


# ------------------------------------------------------------------ main()

with tempfile.TemporaryDirectory() as tmp:
    counts = write(os.path.join(tmp, "c.json"), count_doc(al_language=10))
    out = os.path.join(tmp, "count", "corpus-count.json")

    rc = cc.main(["--counts", counts, "--corpus-sha", "d" * 40, "--out", out])
    check("a first run with no previous count exits 0", rc == 0, f"rc={rc}")
    with open(out, encoding="utf-8") as fh:
        doc = json.load(fh)
    check("and records the total, the per-suite counts, the corpus SHA and the BC version",
          doc == {"corpusSha": "d" * 40, "bcVersion": "27.0", "tests": 10,
                  "suites": {"al-language": {"tests": 10}}}, str(doc))

    # ...and the recorded document is exactly what the NEXT run reads as previous.
    counts2 = write(os.path.join(tmp, "c2.json"), count_doc(al_language=9))
    rc = cc.main(["--counts", counts2, "--corpus-sha", "e" * 40, "--previous", out])
    check("the recorded document is what the next run compares against", rc == 1,
          f"rc={rc}")

    rc = cc.main(["--counts", os.path.join(tmp, "gone.json"),
                  "--corpus-sha", "e" * 40, "--previous", out])
    check("a missing count file exits 3, never 1 and never 0", rc == 3, f"rc={rc}")


# ------------------------------------- what the workflow may cache afterwards
#
# The wedge, caught in review of #3737. The record step is allowed to run when a
# count was MEASURED -- exit 0 or 1 -- and never on exit 3. That is only safe if
# what is on disk afterwards matches:
#
#   * a DROP must leave the NEW SMALLER count in --out, or main can never record
#     it and every later run restores the same larger number and fails against it
#     forever, with no in-repo remedy;
#   * an exit 3 must leave --out EXACTLY as the caller restored it, because
#     --previous and --out are the same path and saving the restored document
#     under this run's corpus SHA would launder an old number onto a new commit.

with tempfile.TemporaryDirectory() as tmp:
    record = os.path.join(tmp, "corpus-count", "corpus-count.json")
    os.makedirs(os.path.dirname(record))
    write(record, {"corpusSha": "a" * 40, "bcVersion": "28.4", "tests": 3152,
                   "suites": {"al-language": {"tests": 3152}}})

    dropped = write(os.path.join(tmp, "dropped.json"), count_doc(al_language=3100))
    rc = cc.main(["--counts", dropped, "--corpus-sha", "b" * 40,
                  "--previous", record, "--out", record])
    check("a drop still exits 1", rc == 1, f"rc={rc}")
    after = json.load(open(record, encoding="utf-8"))
    check("...and RECORDS the new smaller count, which is what unwedges main",
          after["tests"] == 3100, str(after))
    check("...at this run's corpus SHA, not the previous one",
          after["corpusSha"] == "b" * 40, str(after))

    write(record, {"corpusSha": "a" * 40, "bcVersion": "28.4", "tests": 3152,
                   "suites": {"al-language": {"tests": 3152}}})
    before = open(record, encoding="utf-8").read()
    rc = cc.main(["--counts", os.path.join(tmp, "never-written.json"),
                  "--corpus-sha", "c" * 40, "--previous", record, "--out", record])
    check("a run that measured nothing exits 3", rc == 3, f"rc={rc}")
    check("...and leaves the restored document untouched, byte for byte",
          open(record, encoding="utf-8").read() == before,
          open(record, encoding="utf-8").read())
    check("...so nothing re-stamps the old count with this run's corpus SHA",
          json.load(open(record, encoding="utf-8"))["corpusSha"] == "a" * 40)

print()
if FAILURES:
    print(f"{len(FAILURES)} FAILED: {', '.join(FAILURES)}")
    sys.exit(1)
print("all compare_corpus_count tests passed")
