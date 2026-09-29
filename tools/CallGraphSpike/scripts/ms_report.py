#!/usr/bin/env python3
"""Density report over a CallGraphSpike `buckets` result.

Sample (stated in the output): a fixed list of named heavy/central Base Application objects,
a fixed list of named leaf helpers, plus 25 objects drawn uniformly at random (seed 4979) from
Base Application codeunits, tables, pages and reports.
"""
import json, random, statistics, sys

r = json.load(open(sys.argv[1]))
PER_OBJ = {}
T = r["totalTests"]
objs = [o for o in r["objects"] if o["App"] == "Base Application"]
by = {(o["Kind"], o["Id"]): o for o in objs}

HEAVY = [("Codeunit", 80), ("Codeunit", 90), ("Codeunit", 12), ("Codeunit", 22), ("Codeunit", 414), ("Codeunit", 415),
         ("Codeunit", 408), ("Codeunit", 5063), ("Table", 36), ("Table", 37), ("Table", 38), ("Table", 39),
         ("Table", 18), ("Table", 27), ("Table", 17), ("Table", 81), ("Table", 32), ("Page", 42), ("Page", 21), ("Report", 1306)]
LEAF = [("Codeunit", 10), ("Codeunit", 365), ("Codeunit", 5302), ("Codeunit", 1002),
        ("Table", 9), ("Table", 204), ("Table", 4), ("Page", 5), ("Enum", 37)]
pool = sorted((o for o in objs if o["Kind"] in ("Codeunit", "Table", "Page", "Report")), key=lambda o: (o["Kind"], o["Id"]))
rnd = random.Random(4979)
RAND = [(o["Kind"], o["Id"]) for o in rnd.sample(pool, 25)]

PER_OBJ = {}
def row(k):
    o = by.get(k)
    if not o:
        return f"{k[0]} {k[1]}: not found"
    extra = ""
    pl = PER_OBJ.get(o["key"]) if PER_OBJ else None
    if pl:
        extra = f"   procs={len(pl)} median-proc={100*statistics.median(pl):5.1f}% min-proc={100*min(pl):5.1f}%"
    return f"{o['Kind']:9} {o['Id']:>7} {o['name'][:40]:40} {o['tests']:>6} {100*o['tests']/T:6.1f}%{extra}"

print(f"tests: {T} over {len(r['perBucket'])} buckets; options {r['options']}")
print("timings", r["timings"])
s = r["stats"]
print("first-bucket graph:", {k: s.get(k) for k in ("nodes", "LargestScc", "SccCount", "globalRoots", "globalRootClosure", "UnresolvedPublishers")})
print("wildcard closure sizes:", s.get("wildcardClosure"))
if "platformEventTests" in r: print("tests reaching session-level platform events:", r["platformEventTests"])
print("tests whose closure reaches wildcard node:", {k: f"{v} ({100*v/T:.1f}%)" for k, v in r["wildcardTestsReaching"].items()})
if "wildcardTestsDirect" in r:
    print("tests whose own procedures hold a wildcard site:", {k: f"{v} ({100*v/T:.1f}%)" for k, v in r["wildcardTestsDirect"].items()})
    sizes = sorted(r["reachSizes"])
    q = statistics.quantiles(sizes, n=10)
    print(f"closure size per test (nodes): min={sizes[0]} p10={q[0]:.0f} median={statistics.median(sizes):.0f} p90={q[-1]:.0f} max={sizes[-1]} of {s['nodes']}")
else:
    print("proc-graph stats:", {k: s[k] for k in ("nodes", "LargestScc", "SccCount")})
    pc = r["procCounts"]
    per_obj = {}
    for k, v in pc.items():
        per_obj.setdefault(k.split("|")[0], []).append(v / T)
    allp = sorted(v / T for v in pc.values())
    qq = statistics.quantiles(allp, n=10)
    print(f"ALL {len(allp)} Base Application procedures/triggers: p10={qq[0]:.3f} p25={statistics.quantiles(allp, n=4)[0]:.3f} median={statistics.median(allp):.3f} p90={qq[-1]:.3f} mean={statistics.mean(allp):.3f}")
    for thr in (0.5, 0.8):
        print(f"  procedures selecting > {int(thr*100)}% of tests: {100*sum(1 for x in allp if x > thr)/len(allp):.1f}%")
    print(f"  procedures selecting no test: {100*sum(1 for x in allp if x == 0)/len(allp):.1f}%")
    PER_OBJ = per_obj
for name, lst in (("HEAVY", HEAVY), ("LEAF", LEAF), ("RANDOM(seed 4979)", RAND)):
    print(f"-- {name}")
    for k in lst:
        print("  ", row(k))
sample = [by[k] for k in HEAVY + LEAF + RAND if k in by]
sh = sorted(o["tests"] / T for o in sample)
print(f"sample of {len(sample)}: share of tests selected min={sh[0]:.3f} median={statistics.median(sh):.3f} max={sh[-1]:.3f}")
allsh = sorted(o["tests"] / T for o in objs)
q = statistics.quantiles(allsh, n=10)
print(f"ALL {len(objs)} Base Application objects: min={allsh[0]:.3f} p10={q[0]:.3f} p25={statistics.quantiles(allsh, n=4)[0]:.3f} median={statistics.median(allsh):.3f} p90={q[-1]:.3f} max={allsh[-1]:.3f}")
for thr in (0.5, 0.8, 0.9):
    print(f"  objects selecting > {int(thr*100)}% of tests: {sum(1 for x in allsh if x > thr)} ({100*sum(1 for x in allsh if x > thr)/len(allsh):.1f}%)")
print(f"  objects selecting no test: {sum(1 for x in allsh if x == 0)}")
if PER_OBJ:
    withcode = sorted(o["tests"] / T for o in objs if o["key"] in PER_OBJ)
    print(f"  among {len(withcode)} objects with at least one procedure/trigger: median={statistics.median(withcode):.3f}, share selecting > 80% of tests: {100*sum(1 for x in withcode if x > 0.8)/len(withcode):.1f}%")
