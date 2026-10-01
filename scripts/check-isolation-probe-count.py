#!/usr/bin/env python3
"""Check an isolation-probe run executed every [Test] its app declares (#4826).

bc-tests.yml runs each corpus isolation probe (scripts/corpus-app-dirs.py
--isolation-probes) with --strict, so a failing test already fails the step. What
--strict cannot see is a test that never ran; this compares the runner's
--count-out tally with the number of [Test] procedures in the probe's .al files.

Exit: 0 counts match; 1 they differ; 3 unmeasurable (unreadable counts, the probe
suite absent from them, or a probe with no [Test] at all).

Usage:
    check-isolation-probe-count.py --counts <count-out.json> --probe-dir <dir>
"""
import argparse
import json
import os
import re
import sys

TEST_ATTR = re.compile(r"^\s*\[Test\]", re.M)


def declared_tests(probe_dir):
    n = 0
    for dirpath, _, files in os.walk(probe_dir):
        for f in files:
            if f.lower().endswith(".al"):
                with open(os.path.join(dirpath, f), encoding="utf-8-sig") as fh:
                    n += len(TEST_ATTR.findall(fh.read()))
    return n


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--counts", required=True)
    ap.add_argument("--probe-dir", required=True)
    args = ap.parse_args(argv)

    expected = declared_tests(args.probe_dir)
    if expected == 0:
        print(f"::error::isolation probe {args.probe_dir}: no [Test] procedure found; nothing to check")
        return 3
    try:
        with open(args.counts, encoding="utf-8") as fh:
            suites = json.load(fh)["suites"]
    except (OSError, ValueError, KeyError, TypeError) as exc:
        print(f"::error::isolation probe: cannot read {args.counts}: {exc}")
        return 3
    name = os.path.basename(os.path.normpath(args.probe_dir))
    if name not in suites:
        print(f"::error::isolation probe: suite '{name}' absent from {args.counts} (have: {sorted(suites)})")
        return 3
    ran = suites[name].get("tests")
    if ran != expected:
        print(f"::error::isolation probe {name}: ran {ran} test(s), its source declares {expected}")
        return 1
    print(f"isolation probe {name}: ran all {ran} declared test(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
