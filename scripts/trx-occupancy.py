#!/usr/bin/env python3
"""Occupancy timeline and per-collection cost from a VSTest TRX file (issue #1829).

Why this exists alongside scripts/phase-log-report.py
-----------------------------------------------------
The phase log measures the RUNNER SUBPROCESSES AlRunner.Tests spawns. Dividing their
summed wall clock by the step wall clock gave "1.83x achieved concurrency", which reads
like a thread-cap or lock problem and is not one: much of each test's time is host-side
work outside the subprocess, so that ratio structurally understates the real occupancy
(3.00x measured). The authoritative unit for "were xUnit's four workers busy" is the
xUnit test itself, and TRX already records startTime/endTime per test — no instrument
to build, just an extra `--logger trx`.

What it prints, and why each part is decision-relevant
------------------------------------------------------
  * an occupancy timeline — separates "not enough threads" (flat at the cap the whole
    way) from "ramp-down" (at the cap, then trailing off). Only the first is fixed by
    raising maxParallelThreads; the second is fixed by dispatching the longest unit
    earlier, and raising the cap does nothing for it.
  * summed duration per test class. Since #1818 every class is its own xUnit collection
    and a collection is STRICTLY SERIAL, so a class's summed duration is a hard floor on
    the run: it cannot finish before (its dispatch time + that sum).
  * the two bounds worth comparing the makespan against: total/threads, and the longest
    single collection. Being above both means the loss is scheduling, not work.
  * what was running during the underloaded stretches — i.e. the name to act on.

Usage:
  scripts/trx-occupancy.py <results.trx> [--label NAME] [--threads 4] [--buckets 48]
"""
import argparse
import statistics
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from datetime import datetime

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def load(path):
    root = ET.parse(path).getroot()
    names = {}
    for u in root.findall(".//t:TestDefinitions/t:UnitTest", NS):
        method = u.find("t:TestMethod", NS)
        if method is not None:
            names[u.get("id")] = (method.get("className") or "?", method.get("name") or "?")
    rows = []
    for r in root.findall(".//t:Results/t:UnitTestResult", NS):
        start, end = r.get("startTime"), r.get("endTime")
        if not start or not end:
            continue
        cls, name = names.get(r.get("testId"), ("?", r.get("testName") or "?"))
        rows.append((datetime.fromisoformat(start), datetime.fromisoformat(end),
                     cls.rsplit(".", 1)[-1], name))
    return rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("path")
    ap.add_argument("--label", default="")
    ap.add_argument("--threads", type=int, default=4)
    ap.add_argument("--buckets", type=int, default=48)
    args = ap.parse_args()

    try:
        rows = load(args.path)
    except (FileNotFoundError, ET.ParseError) as ex:
        print(f"trx '{args.path}' not usable ({ex}) — nothing to report")
        return 0
    if len(rows) < 2:
        print(f"trx '{args.path}' has {len(rows)} timed results — nothing to report")
        return 0

    t0 = min(r[0] for r in rows)
    t1 = max(r[1] for r in rows)
    span = (t1 - t0).total_seconds()
    total = sum((e - s).total_seconds() for s, e, _, _ in rows)

    print("=" * 78)
    print(f"TRX occupancy{' — ' + args.label if args.label else ''}")
    print("=" * 78)
    print(f"tests={len(rows)}  span={span:.1f}s  summed test duration={total:.1f}s")
    print(f"achieved occupancy {total / max(0.001, span):.2f}x of {args.threads} threads")

    per_class = defaultdict(float)
    first_start = {}
    for s, e, cls, _ in rows:
        per_class[cls] += (e - s).total_seconds()
        first_start[cls] = min(first_start.get(cls, s), s)
    longest = max(per_class.values())
    bound = max(total / args.threads, longest)
    print(f"floor: max(total/{args.threads}={total / args.threads:.1f}s, "
          f"longest collection={longest:.1f}s) = {bound:.1f}s   "
          f"→ {span - bound:.1f}s of scheduling loss")
    print()

    # ── timeline ────────────────────────────────────────────────────────────
    width = span / args.buckets
    busy = [0.0] * args.buckets
    for s, e, _, _ in rows:
        lo_s = (s - t0).total_seconds()
        hi_s = (e - t0).total_seconds()
        i = int(lo_s / width) if width else 0
        while i < args.buckets and i * width < hi_s:
            busy[i] += max(0.0, min(hi_s, (i + 1) * width) - max(lo_s, i * width)) / width
            i += 1
    print(f"── OCCUPANCY TIMELINE (bucket = {width:.1f}s, value = concurrent tests) " + "─" * 10)
    for chunk in range(0, args.buckets, 24):
        print(f"  t={chunk * width:6.0f}s " + "".join(f"{v:5.1f}" for v in busy[chunk:chunk + 24]))
    print(f"  mean {statistics.mean(busy):.2f}   "
          f"buckets below half the cap: "
          f"{sum(1 for v in busy if v < args.threads / 2)} "
          f"({sum(1 for v in busy if v < args.threads / 2) * width:.0f}s)")
    print()

    # ── per collection ──────────────────────────────────────────────────────
    print("── HEAVIEST COLLECTIONS (a class is one collection ⇒ strictly serial) " + "─" * 8)
    print(f"  {'summed':>8} {'dispatched':>11}  class")
    for cls, secs in sorted(per_class.items(), key=lambda kv: -kv[1])[:15]:
        print(f"  {secs:7.1f}s {(first_start[cls] - t0).total_seconds():10.1f}s  {cls}")
    late = [(cls, secs, (first_start[cls] - t0).total_seconds())
            for cls, secs in per_class.items()
            if (first_start[cls] - t0).total_seconds() + secs > span * 0.98 and secs > span * 0.1]
    for cls, secs, disp in late:
        print(f"  → {cls} is on the critical path: dispatched at {disp:.0f}s, "
              f"{secs:.0f}s of serial work, run ends at {span:.0f}s")
    print()

    # ── who is running when we are underloaded ──────────────────────────────
    idle = [(i * width, (i + 1) * width) for i, v in enumerate(busy) if v < args.threads / 2]
    if idle:
        blame = defaultdict(float)
        for s, e, cls, _ in rows:
            lo_s, hi_s = (s - t0).total_seconds(), (e - t0).total_seconds()
            for a, b in idle:
                overlap = min(hi_s, b) - max(lo_s, a)
                if overlap > 0:
                    blame[cls] += overlap
        print("── WHAT RUNS WHILE UNDERLOADED " + "─" * 46)
        for cls, secs in sorted(blame.items(), key=lambda kv: -kv[1])[:10]:
            print(f"  {secs:7.1f}s  {cls}")
        print()
        print("  Dispatch these earlier (AlRunner.Tests/CollectionCostOrderer.cs) before")
        print("  reaching for a higher maxParallelThreads — a thread cap cannot fill a")
        print("  stretch of the run that has no queued work left to give it.")
        print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
