#!/usr/bin/env python3
"""Loud guard against CollectionCostOrderer.MeasuredWeightSeconds table drift (#1887).

Why this exists
----------------
CollectionCostOrderer dispatches heaviest-measured collections first (#1829), but its
weight table is HAND-MAINTAINED: a collection missing from it silently falls back to
UnmeasuredWeightSeconds (30s) and gets scheduled as if it were nearly free. #1887 found
two collections that had drifted into that fallback — InstallSeedDepCompanyCacheTests
(~196s) and CountBaselineIntegrationTests (~84s) — each costing 50-73s of scheduling loss
per CI leg, silently, for however long it took someone to read a TRX occupancy report by
hand and notice.

This script closes that loop. Given the same trx/unit-tests.trx the "TRX occupancy
report" CI step already parses, it flags any collection whose summed duration THIS run
exceeds a threshold and is NOT a key in the table — a loud, failing check, per
.claude/rules/loud-failures.md, instead of a report nobody is looking at until the next
manual audit.

Deliberately NOT checking drift on entries that ARE already present in the table:
the same class's summed duration varies materially leg to leg — CacheKeyDependencyClosureTests
measured 196s on one BC leg and 294s on another within the very run that reported #1887,
because different BC versions ship different platform symbol sets and that changes AL
compile cost. A percentage-drift check on top of that would be exactly the kind of noisy,
BC-version-dependent gate that trains people to ignore CI red. A completely MISSING entry
above threshold has no such false-positive mode: below threshold it is genuinely cheap (the
file header's own argument — the ~66 collections under it total 2.8s), and above threshold
it is precisely the failure #1887 found.

Two bands, and a clock that is not the wall clock (#3103)
--------------------------------------------------------
This runs in bc-tests.yml WITHOUT continue-on-error, inside the legs that roll up into the
`BC test matrix passed` required check (renamed from `All BC versions passed` by #3200
when the pull-request matrix narrowed to three legs; the check-collection-weights step is
unaffected by that narrowing, because it is gated on the matrix's `unit-tests` flag, which
#3200 still computes from the FULL version list -- 27.5 and 28.4, on a pull request exactly
as on a push). So every number it compares against decides
whether somebody's pull request goes red — and until #3103 it compared summed wall-clock
seconds measured on GitHub's shared runners against one fixed line at 2x
UnmeasuredWeightSeconds (60s). Wall clock on a shared runner is not a property of the
collection: SuiteAbortOnTimeoutTests, untouched by either pull request, summed 59.4s on
PR #3082's run and 63.7s on PR #3083's and produced opposite verdicts on the same code.
A red required check that is routinely somebody else's fault is worse than the drift it
guards, because it teaches people to skim past red.

The evidence that the old line sat exactly where the mass is: of the collections added to
MeasuredWeightSeconds *because* they tripped this gate, most were recorded at 60-63s, and
their own comments say they crossed only on whichever leg happened to run slow. So:

1. **Calibrate to the leg, not to the wall.** Collections already in the table measure long
   on exactly the legs where an unlisted one measures long, so the median of
   observed/recorded over the paired entries IS this leg's clock relative to the clock the
   table was recorded on. Both bands are scaled by it. The factor floors at 1.0 — a fast
   leg must never make the gate STRICTER than the historical line, or this change would
   turn PRs red that pass today — and is clamped at MAX_LOAD_FACTOR so a wrecked table
   cannot switch the gate off. It needs MIN_CALIBRATION_SAMPLES pairs; below that it is two
   numbers, not a measurement, and the factor stays 1.0.

2. **Advisory below, failing above.** At/above 2x UnmeasuredWeightSeconds the collection is
   worth recording, and is reported as a GitHub `::warning::` annotation so it lands in the
   checks UI instead of 400 lines down a log — visible, per #1887's actual complaint, but
   exit 0. Only at/above 3x does it fail: a collection weighted 30s when it really costs
   90s+ is the tail #1887 measured, and 90s sits in a sparse part of the observed
   distribution rather than in the middle of it.

What is deliberately unchanged: a genuinely heavy unlisted collection (#1887's own
InstallSeedDepCompanyCacheTests at ~196s) still fails the leg, on a slow leg too, because
196s does not become cheap when the box is 60% slow.

Usage:
  scripts/check-collection-weights.py <results.trx> [--orderer PATH]
      [--advisory-threshold SECONDS] [--fail-threshold SECONDS] [--no-load-calibration]

Exit code is 1 (loud failure) when a collection above the failing band is missing from the
table, 0 otherwise — including when the trx file is absent/unparsable, matching
scripts/trx-occupancy.py's "nothing to report" convention for a step that should not fail
the build over missing input data.
"""
import argparse
import re
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path

# Sibling module, imported by path so this works both when the script is run
# directly and when a test loads it through importlib.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from trxtime import parse_trx_time  # noqa: E402


NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}

DEFAULT_ORDERER = Path(__file__).resolve().parent.parent / "AlRunner.Tests" / "CollectionCostOrderer.cs"

# A collection below 2x UnmeasuredWeightSeconds cannot create a meaningful tail — the file
# header's own argument for why the ~66 collections below that line total 2.8s and are
# harmless. At or above it, the collection is worth recording: that is the ADVISORY band.
DEFAULT_ADVISORY_MULTIPLE = 2

# ...and 3x is where being weighted at UnmeasuredWeightSeconds costs a tail worth turning a
# required check red for. #3103: keeping the failing line at 2x put it in the middle of the
# observed distribution — most collections ever added to the table because this gate caught
# them measured 60-63s, i.e. within noise of the line itself.
DEFAULT_FAIL_MULTIPLE = 3

# Fewer paired collections than this is not a measurement of the leg, so no calibration.
# The real table carries ~45 entries and a real run observes nearly all of them.
MIN_CALIBRATION_SAMPLES = 8

# An upper bound on how far a slow leg may push the bands out, so a table that has drifted
# wholesale (or a truncated trx) cannot silently disable the gate.
MAX_LOAD_FACTOR = 3.0


def load_trx_per_collection_seconds(path):
    """Bare class name -> summed test duration (seconds) from a VSTest TRX file."""
    root = ET.parse(path).getroot()
    names = {}
    for u in root.findall(".//t:TestDefinitions/t:UnitTest", NS):
        method = u.find("t:TestMethod", NS)
        if method is not None:
            names[u.get("id")] = method.get("className") or "?"
    per_class = defaultdict(float)
    for r in root.findall(".//t:Results/t:UnitTestResult", NS):
        start, end = r.get("startTime"), r.get("endTime")
        if not start or not end:
            continue
        cls = names.get(r.get("testId"), "?").rsplit(".", 1)[-1]
        per_class[cls] += (parse_trx_time(end) - parse_trx_time(start)).total_seconds()
    return dict(per_class)


def load_table(orderer_path):
    """Parse MeasuredWeightSeconds and UnmeasuredWeightSeconds straight out of the C#
    source, so this script and the orderer it checks can never silently disagree about
    what the table currently says."""
    text = Path(orderer_path).read_text()

    unmeasured_match = re.search(r"UnmeasuredWeightSeconds\s*=\s*(\d+)", text)
    if not unmeasured_match:
        raise ValueError(f"could not find UnmeasuredWeightSeconds in {orderer_path}")
    unmeasured = int(unmeasured_match.group(1))

    table_match = re.search(r"MeasuredWeightSeconds\s*=.*?\{(.*?)\};", text, re.DOTALL)
    if not table_match:
        raise ValueError(f"could not find MeasuredWeightSeconds dictionary body in {orderer_path}")
    entries = re.findall(r'\["([^"]+)"\]\s*=\s*(\d+)', table_match.group(1))
    return {name: int(seconds) for name, seconds in entries}, unmeasured


def find_missing_heavy(observed_seconds, table, threshold_seconds):
    """Collections observed this run at/above threshold that the table does not know
    about — sorted heaviest first so the loudest offender prints first."""
    return sorted(
        ((cls, secs) for cls, secs in observed_seconds.items()
         if cls not in table and secs >= threshold_seconds),
        key=lambda kv: -kv[1],
    )


def leg_load_factor(observed_seconds, table,
                    min_samples=MIN_CALIBRATION_SAMPLES, max_factor=MAX_LOAD_FACTOR):
    """How slow THIS leg ran, relative to the clock MeasuredWeightSeconds was recorded on.

    #3103. Every collection already in the table is a stopwatch that was started on the
    same box as the unlisted one, so the median of observed/recorded across the paired
    entries separates "this collection got heavier" from "this runner was busy". The median
    (not the mean) because the table is hand-maintained and a couple of its entries are
    knowingly stale — the script's header says drift-on-a-present-entry is out of scope, so
    those entries must not be allowed to drag the calibration.

    Floored at 1.0: this is allowed to widen the bands on a slow leg, never to narrow them
    on a fast one. Narrowing would fail collections that pass today, which is a different
    change from the one #3103 asks for. Clamped at max_factor, and refused outright below
    min_samples pairs.
    """
    ratios = sorted(observed_seconds[cls] / recorded
                    for cls, recorded in table.items()
                    if recorded > 0 and cls in observed_seconds)
    if len(ratios) < min_samples:
        return 1.0
    mid = len(ratios) // 2
    median = ratios[mid] if len(ratios) % 2 else (ratios[mid - 1] + ratios[mid]) / 2
    return min(max(median, 1.0), max_factor)


def classify_missing(observed_seconds, table, advisory_seconds, fail_seconds):
    """Split the unlisted-and-heavy collections into (failing, advisory).

    Failing is at/above fail_seconds — the #1887 tail, loud and red. Advisory is the band
    between advisory_seconds and fail_seconds: worth recording, reported by name, exit 0.
    Both heaviest-first.
    """
    over = find_missing_heavy(observed_seconds, table, advisory_seconds)
    failing = [(cls, secs) for cls, secs in over if secs >= fail_seconds]
    advisory = [(cls, secs) for cls, secs in over if secs < fail_seconds]
    return failing, advisory


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("path")
    ap.add_argument("--orderer", default=str(DEFAULT_ORDERER))
    ap.add_argument("--advisory-threshold", type=float, default=None,
                    help="override the computed 2x-unmeasured 'worth recording' line")
    ap.add_argument("--fail-threshold", type=float, default=None,
                    help="override the computed 3x-unmeasured 'fail the leg' line")
    ap.add_argument("--no-load-calibration", action="store_true",
                    help="compare raw wall-clock seconds, without the #3103 leg calibration")
    args = ap.parse_args()

    try:
        observed = load_trx_per_collection_seconds(args.path)
    except (FileNotFoundError, ET.ParseError) as ex:
        print(f"trx '{args.path}' not usable ({ex}) — nothing to check")
        return 0
    if not observed:
        print(f"trx '{args.path}' has no timed results — nothing to check")
        return 0

    table, unmeasured = load_table(args.orderer)

    load_factor = 1.0 if args.no_load_calibration else leg_load_factor(observed, table)
    advisory = (args.advisory_threshold if args.advisory_threshold is not None
                else unmeasured * DEFAULT_ADVISORY_MULTIPLE) * load_factor
    fail = (args.fail_threshold if args.fail_threshold is not None
            else unmeasured * DEFAULT_FAIL_MULTIPLE) * load_factor

    failing, borderline = classify_missing(observed, table, advisory, fail)

    calibration = (f"leg clock {load_factor:.2f}x the table's"
                   if load_factor > 1.0 else "leg clock at or under the table's")
    bands = (f"bands this run: report >= {advisory:.0f}s, fail >= {fail:.0f}s "
             f"({calibration}; {len(table)} entries checked against "
             f"{len(observed)} observed collections)")

    # Borderline entries are reported whether or not anything failed: they are the drift
    # #1887 cares about, and ::warning:: puts them in the checks UI rather than 400 lines
    # down a log. Not a failure — see this file's header on why wall clock at 60s is not a
    # property of the collection (#3103).
    for cls, secs in borderline:
        print(f"::warning file=AlRunner.Tests/CollectionCostOrderer.cs::"
              f"{cls} cost {secs:.1f}s this run and is absent from "
              f"CollectionCostOrderer.MeasuredWeightSeconds, so it is dispatched as if it "
              f"cost {unmeasured}s. Record it (issue #1887).")

    if not failing:
        print(f"CollectionCostOrderer.MeasuredWeightSeconds: no collection above "
              f"{fail:.0f}s is missing from the table; {bands}. OK.")
        return 0

    print("=" * 78)
    print("STALE CollectionCostOrderer.MeasuredWeightSeconds TABLE (issue #1887)")
    print("=" * 78)
    print(f"The following collection(s) cost >= {fail:.0f}s this run but are absent")
    print(f"from the table in {args.orderer}. Each falls back to")
    print(f"UnmeasuredWeightSeconds ({unmeasured}s) and can be scheduled as a")
    print("single-threaded tail late in the run — exactly the failure issue #1887 found.")
    print()
    for cls, secs in failing:
        print(f"  {secs:7.1f}s  {cls}")
    print()
    print(bands + ".")
    print()
    noun = "it" if len(failing) == 1 else "them"
    print(f"Add {noun} to MeasuredWeightSeconds in AlRunner.Tests/CollectionCostOrderer.cs")
    print("with its measured seconds (round down), per the file header's")
    print("'Why a measured table' note.")
    return 1


if __name__ == "__main__":
    sys.exit(main())
