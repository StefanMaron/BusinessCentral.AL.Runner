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

Still deliberately NOT checking DRIFT on entries that are present, and #4208 re-measured
why rather than inheriting the claim. On one commit and one table, every collection paired
across two legs of run 35052870824 ran 1.83x-1.96x slower on BC 28.4 than on 27.5 (median
1.90x) — a uniform property of the leg, not of any collection. That pushes a perfectly
healthy entry's observed/recorded to 1.97x (EventSubscriberScanEquivalenceTests, recorded
37, observed 73.0), while the decorative case #4208 measured sits at 2.55x (recorded 30,
observed 76.4). A percentage-drift band would have to thread between 1.97 and 2.55 on a
single sample, and the calibration below does not rescue it: both legs printed "leg clock
at or under the table's", because the table records observed MAXIMA so the median ratio
floors at 1.0 on a slow leg too. So a drift band remains the noisy, BC-version-dependent
gate that trains people to ignore CI red.

What IS checked on a present entry, and needs no tolerance at all (#4208): whether the
recorded value is EQUAL to UnmeasuredWeightSeconds. CollectionCostOrderer sorts descending
on the weight with a stable tiebreak and hands an absent collection exactly that constant,
so such an entry produces the identical sort key to being absent — provably no dispatch
change, on every leg, at every clock speed. That is a discrete fact about the table rather
than a measurement of the run, which is why it carries no false-positive mode. #2175 stated
it in prose in CollectionCostOrderer.cs's own header ("recording 31 would have silenced
check-collection-weights.py while leaving dispatch order identical to being absent") as the
reason to pick the right end of a range; nothing pinned it, so it held only as long as each
author followed it by hand, and #4208 measured that a real entry mutated to 30 kept all nine
CollectionCostOrderer tests green.

Note the rule is equality, NOT "must exceed the fallback": three real entries are recorded
BELOW it (TestTimeoutFlagTests at 21, and two at 23), which ranks those collections after
the unmeasured ones and is the correct call for a genuinely cheap collection. A
"must be > 30" rule would turn three legitimate entries red.

WHAT THIS DELIBERATELY DOES NOT CATCH, stated so nobody reads the gate as wider than it
is: an UNDERSTATED entry that still clears the fallback. #2175's own example is "recording
31" for a collection measuring 31.8s-61.2s, and 31 DOES change dispatch order — it ranks
above all ~1160 unmeasured collections — so the sort-key argument does not condemn it and
no tolerance-free test can. Catching that needs the value to be DERIVED from the TRX rather
than checked against it — #1887's own Preferred section proposed exactly that, and #4204
(the fourth-plus staleness instance) argued for it from recurrence. This check closes the
provable hole; the understated-but-above-fallback hole stays open, and deriving the table
is what would close it.

A completely MISSING entry above threshold has no false-positive mode either: below
threshold it is genuinely cheap (the file header's own argument — the ~66 collections under
it total 2.8s), and above threshold it is precisely the failure #1887 found.

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

   MEASURED, and worth knowing before you credit this half with anything: on #3110's own
   run (34054350482) BOTH unit legs printed "leg clock at or under the table's", i.e. the
   factor came back 1.0 and the bands were the unscaled 60s/75s. That is structural rather
   than luck. MeasuredWeightSeconds records each collection's observed MAXIMUM, so
   median(observed / recorded) is normally BELOW 1.0 and the floor swallows it; the
   calibration engages only on a leg slower than the historical max for most collections.
   So the flake in #3103 is fixed by the flat 60s -> 75s move, not by this. Kept anyway,
   because it is the half that protects a genuinely slow leg, and because it can only ever
   loosen -- it cannot produce a false red. Do not read a green run as evidence it works;
   read the "leg clock" clause in the output, which says which case you are in.

2. **Advisory below, failing above.** At/above 2x UnmeasuredWeightSeconds the collection is
   worth recording, and is reported as a GitHub `::warning::` annotation so it lands in the
   checks UI instead of 400 lines down a log — visible, per #1887's actual complaint, but
   exit 0. Above the failing band it exits 1. That band is an absolute number of SECONDS,
   not a fixed multiple: --fail-threshold sets it outright, and DEFAULT_FAIL_MULTIPLE (3x,
   i.e. 90s) is only the fallback for a caller that passes nothing. The shipping caller
   passes one -- bc-tests.yml runs this with --fail-threshold 75 (2.5x). 90s would stop
   enforcing over roughly eight entries of the weight table, among them
   CountBaselineIntegrationTests at 84s, one of the two collections #1887 originally FOUND
   with this gate, so it cannot catch its own founding case. That step's comment carries the
   full reasoning, and the number is pinned by
   scripts/tests/check-collection-weights.test.py::WorkflowFailThresholdTests.

What is deliberately unchanged: a genuinely heavy unlisted collection (#1887's own
InstallSeedDepCompanyCacheTests at ~196s) still fails the leg, on a slow leg too, because
196s does not become cheap when the box is 60% slow.

Usage:
  scripts/check-collection-weights.py <results.trx> [--orderer PATH]
      [--advisory-threshold SECONDS] [--fail-threshold SECONDS] [--no-load-calibration]

Exit codes (guards-need-a-third-state.md):
  0  measured, and fine
  1  loud failure — a collection above the failing band is missing from the table, or a
     present entry is recorded at UnmeasuredWeightSeconds and so changes no dispatch order
  3  could not measure — the orderer source parses but yields no UnmeasuredWeightSeconds,
     so "decorative" has no definition. Deliberately distinct from 0: a table whose
     decorative entries are UNKNOWABLE is not a table with none.

A missing/unparsable trx stays exit 0, matching scripts/trx-occupancy.py's "nothing to
report" convention for a step that should not fail the build over missing input data — but
the decorative-entry check still runs, because it reads the table rather than the run.
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

# "I could not measure", kept distinct from both 0 and 1 (guards-need-a-third-state.md).
# Only the orderer failing to yield a fallback constant reaches this: a decorative entry is
# defined as one recorded AT that constant, so without it the question is unanswerable
# rather than answered in the negative.
EXIT_CANNOT_MEASURE = 3


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
        raise UnmeasurableFallback(
            f"could not find UnmeasuredWeightSeconds in {orderer_path}")
    unmeasured = int(unmeasured_match.group(1))

    table_match = re.search(r"MeasuredWeightSeconds\s*=.*?\{(.*?)\};", text, re.DOTALL)
    if not table_match:
        raise ValueError(f"could not find MeasuredWeightSeconds dictionary body in {orderer_path}")
    entries = re.findall(r'\["([^"]+)"\]\s*=\s*(\d+)', table_match.group(1))
    return {name: int(seconds) for name, seconds in entries}, unmeasured


class UnmeasurableFallback(ValueError):
    """The orderer source yielded no UnmeasuredWeightSeconds, so the fallback weight that
    defines a decorative entry is unknown. Distinct from "the table is fine" — see
    EXIT_CANNOT_MEASURE."""


def find_decorative_entries(table, unmeasured):
    """Entries whose recorded value is EXACTLY the fallback weight, so they change no
    dispatch order at all (#4208).

    CollectionCostOrderer.WeightSeconds returns UnmeasuredWeightSeconds for any collection
    it cannot find in the table, and OrderTestCollections is a stable descending sort on
    that number. An entry recorded at the same value therefore yields the identical sort
    key AND the identical tiebreak position it would have had while absent: not "close to"
    absent, indistinguishable from it. Recording it satisfies the missing-entry gate above
    while leaving the #1887 tail exactly where it was.

    No tolerance and no dependence on the run: this compares two integers read out of one
    source file. Strictly less than the fallback is NOT decorative — it ranks the
    collection below the unmeasured ones, which is a real dispatch change and the correct
    entry for a genuinely cheap collection (three real entries sit at 21 and 23).
    """
    return sorted(cls for cls, recorded in table.items() if recorded == unmeasured)


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


def report_decorative(decorative, unmeasured, orderer_path):
    """Print the #4208 verdict for entries recorded at the fallback weight. Always exit 1:
    unlike a wall-clock band this is a discrete property of the table, identical on every
    leg, so there is no noise to demote it to an advisory over."""
    print("=" * 78)
    print("DECORATIVE CollectionCostOrderer.MeasuredWeightSeconds ENTRIES (issue #4208)")
    print("=" * 78)
    print(f"The following entr{'y is' if len(decorative) == 1 else 'ies are'} recorded at "
          f"exactly UnmeasuredWeightSeconds ({unmeasured}s)")
    print(f"in {orderer_path}. The orderer sorts descending on that number and gives an")
    print("ABSENT collection the same value, so each of these is dispatched in exactly the")
    print("position it would occupy with no entry at all — it satisfies the missing-entry")
    print("check above while leaving the #1887 tail untouched.")
    print()
    for cls in decorative:
        print(f"  {cls}")
    print()
    print("Record the collection's observed MAXIMUM instead (CollectionCostOrderer.cs's")
    print("own header, #2175: 'whichever end you pick, the recorded value must change")
    print("dispatch order relative to the fallback, or the entry is decoration'), or")
    print("delete the entry if the collection is genuinely no heavier than the fallback.")
    return 1


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

    # The table is loaded before the trx because the decorative-entry check (#4208) is a
    # claim about the TABLE, not about the run: it must still fire on a leg whose trx is
    # missing, truncated, or filtered down to a subset that never ran the entry.
    try:
        table, unmeasured = load_table(args.orderer)
    except UnmeasurableFallback as ex:
        print(f"REFUSING TO REPORT: {ex}. An entry recorded at UnmeasuredWeightSeconds "
              f"changes no dispatch order, so without that constant the table's "
              f"decorative entries are unknowable — which is not the same as there being "
              f"none (guards-need-a-third-state.md).")
        return EXIT_CANNOT_MEASURE

    decorative = find_decorative_entries(table, unmeasured)

    try:
        observed = load_trx_per_collection_seconds(args.path)
    except (FileNotFoundError, ET.ParseError) as ex:
        print(f"trx '{args.path}' not usable ({ex}) — no missing-entry check")
        observed = {}
    if not observed:
        if not decorative:
            print(f"trx '{args.path}': no timed results to check for missing entries; "
                  f"{len(table)} recorded entries carry no decorative weight. OK.")
            return 0
        return report_decorative(decorative, unmeasured, args.orderer)

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
        if decorative:
            print(f"CollectionCostOrderer.MeasuredWeightSeconds: no collection above "
                  f"{fail:.0f}s is missing from the table; {bands}.")
            print()
            return report_decorative(decorative, unmeasured, args.orderer)
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
    if decorative:
        print()
        report_decorative(decorative, unmeasured, args.orderer)
    return 1


if __name__ == "__main__":
    sys.exit(main())
