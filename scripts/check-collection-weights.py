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

Usage:
  scripts/check-collection-weights.py <results.trx> [--orderer PATH] [--threshold SECONDS]

Exit code is 1 (loud failure) when a heavy collection is missing from the table, 0
otherwise — including when the trx file is absent/unparsable, matching
scripts/trx-occupancy.py's "nothing to report" convention for a step that should not fail
the build over missing input data.
"""
import argparse
import re
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from datetime import datetime
from pathlib import Path

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}

DEFAULT_ORDERER = Path(__file__).resolve().parent.parent / "AlRunner.Tests" / "CollectionCostOrderer.cs"

# A collection below 2x UnmeasuredWeightSeconds cannot create a meaningful tail — the file
# header's own argument for why the ~66 collections below that line total 2.8s and are
# harmless. Anything at or above it is exactly the shape #1887 found.
DEFAULT_THRESHOLD_MULTIPLE = 2


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
        per_class[cls] += (datetime.fromisoformat(end) - datetime.fromisoformat(start)).total_seconds()
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


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("path")
    ap.add_argument("--orderer", default=str(DEFAULT_ORDERER))
    ap.add_argument("--threshold", type=float, default=None,
                     help="override the computed 2x-unmeasured threshold directly")
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
    threshold = args.threshold if args.threshold is not None else unmeasured * DEFAULT_THRESHOLD_MULTIPLE

    missing = find_missing_heavy(observed, table, threshold)
    if not missing:
        print(f"CollectionCostOrderer.MeasuredWeightSeconds: no collection above "
              f"{threshold:.0f}s is missing from the table ({len(table)} entries checked "
              f"against {len(observed)} observed collections). OK.")
        return 0

    print("=" * 78)
    print("STALE CollectionCostOrderer.MeasuredWeightSeconds TABLE (issue #1887)")
    print("=" * 78)
    print(f"The following collection(s) cost >= {threshold:.0f}s this run but are absent")
    print(f"from the table in {args.orderer}. Each falls back to")
    print(f"UnmeasuredWeightSeconds ({unmeasured}s) and can be scheduled as a")
    print("single-threaded tail late in the run — exactly the failure issue #1887 found.")
    print()
    for cls, secs in missing:
        print(f"  {secs:7.1f}s  {cls}")
    print()
    noun = "it" if len(missing) == 1 else "them"
    print(f"Add {noun} to MeasuredWeightSeconds in AlRunner.Tests/CollectionCostOrderer.cs")
    print("with its measured seconds (round down), per the file header's")
    print("'Why a measured table' note.")
    return 1


if __name__ == "__main__":
    sys.exit(main())
