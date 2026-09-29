#!/usr/bin/env python3
"""Procedure-granularity soundness: per (object, executed scope), are the tests that executed it
a subset of the tests that can statically reach a procedure/trigger of that name in that object?
usage: compare_proc.py <proc-static.json> <ref1-raw.jsonl> --app <app name>"""
import json, sys, statistics
from collections import defaultdict

st = json.load(open(sys.argv[1]))
app = sys.argv[sys.argv.index("--app") + 1] if "--app" in sys.argv else sys.exit("--app <app name> is required")
names = st["testNames"]
objs = st["objects"]
by_file = defaultdict(list)
for o in objs:
    by_file[o["file"]].append(o)
static = defaultdict(set)   # (objkey, scope lower) -> test names
for p in st["procs"]:
    static[(p["key"], p["proc"].lower())] |= {names[i] for i in p["testIdx"]}
summary = [json.loads(l) for l in open(sys.argv[2]) if '"summary"' in l][0]
executed = defaultdict(set)
for e in summary["perTestCoverage"]:
    for f in e["coverage"]:
        for s in f["statements"]:
            if s["hits"] <= 0:
                continue
            c = [o for o in by_file.get(f["file"], []) if o["start"] <= s["line"] <= o["end"]]
            if len(c) != 1:
                continue
            executed[(c[0]["key"], c[0]["App"], s["scope"].lower())].add(e["test"])
miss = unmatched = checked = 0
ratios = []
unmatched_scopes = []
for (key, a, scope), tests in executed.items():
    if a != app:
        continue
    s = static.get((key, scope))
    if s is None:
        unmatched += 1
        unmatched_scopes.append(scope)
        continue
    checked += 1
    m = tests - s
    miss += len(m)
    ratios.append(len(s) / len(tests))
print(f"executed (object, scope) pairs in {app}: {checked + unmatched}; matched to a static node by name: {checked}; unmatched: {unmatched}")
print(f"procedure-level MISSES (test executed the procedure, cannot statically reach it): {miss}")
if ratios:
    r = sorted(ratios)
    q = statistics.quantiles(r, n=10)
    print(f"over-selection |S|/|E| per executed procedure: min={r[0]:.2f} p10={q[0]:.2f} median={statistics.median(r):.2f} p90={q[-1]:.2f} max={r[-1]:.2f}")
N = len(names)
procs = [p for p in st["procs"] if p["key"].split("@")[1] == app]
sh = sorted(p["tests"] / N for p in procs)
q = statistics.quantiles(sh, n=10)
print(f"share of tests selected per {app} procedure/trigger ({len(procs)}): p10={q[0]:.3f} median={statistics.median(sh):.3f} p90={q[-1]:.3f} mean={statistics.mean(sh):.3f}")
ex_share = defaultdict(set)
for (key, a, scope), tests in executed.items():
    if a == app:
        ex_share[(key, scope)] = tests
if "--verbose" in sys.argv:
    print("unmatched scope samples:", sorted(set(unmatched_scopes))[:30])
