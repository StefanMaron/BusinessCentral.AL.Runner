#!/usr/bin/env python3
"""Soundness + over-selection: static reachability (CallGraphSpike project) vs per-test executed objects.

usage: compare.py <static.json> <ref-executed.json> --app <app name>
Per app object X:  E(X) = tests that executed a statement inside X,  S(X) = tests that can statically reach X.
A miss is a test in E(X) \\ S(X).
"""
import json, sys, statistics
from collections import defaultdict

st = json.load(open(sys.argv[1]))
ref = json.load(open(sys.argv[2]))
app = sys.argv[sys.argv.index("--app") + 1] if "--app" in sys.argv else sys.exit("--app <app name> is required")
verbose = "--verbose" in sys.argv

objs = st["objects"]
by_file = defaultdict(list)
for o in objs:
    by_file[o["file"]].append(o)

def obj_of(file, line):
    cands = [o for o in by_file.get(file, []) if o["start"] <= line <= o["end"]]
    return cands[0]["key"] if len(cands) == 1 else None

executed = defaultdict(set)   # obj key -> tests
unmapped = 0
for test, files in ref["tests"].items():
    for f, lines in files.items():
        for ln in lines:
            k = obj_of(f, ln)
            if k is None:
                unmapped += 1
                continue
            executed[k].add(test)

static = defaultdict(set)
for test, v in st["tests"].items():
    for k in v["reach"]:
        static[k].add(test)

status = ref["status"]
all_tests = set(st["tests"]) | set(ref["tests"])
app_objs = [o for o in objs if o["App"] == app]
misses = []
rows = []
for o in app_objs:
    k = o["key"]
    E, S = executed.get(k, set()), static.get(k, set())
    m = E - S
    for t in m:
        misses.append((k, o["name"], t, status.get(t, {}).get("status")))
    rows.append((k, o["Kind"], len(E), len(S)))

print(f"tests: static={len(st['tests'])} reference={len(ref['tests'])} both={len(set(st['tests']) & set(ref['tests']))}")
print(f"only in static: {len(set(st['tests']) - set(ref['tests']))}  only in reference: {len(set(ref['tests']) - set(st['tests']))}")
print(f"executed lines not mapped to exactly one object: {unmapped}")
print(f"{app} objects: {len(app_objs)}; executed by >=1 test: {sum(1 for r in rows if r[2])}")
print(f"MISSES (test executed object but cannot statically reach it): {len(misses)} over {len({m[0] for m in misses})} objects")
if verbose:
    for m in sorted(misses)[:200]:
        print("  MISS", m)
executed_objs = [r for r in rows if r[2] > 0]
ratios = sorted(r[3] / r[2] for r in executed_objs)
if ratios:
    q = statistics.quantiles(ratios, n=10)
    print(f"over-selection |S|/|E| over {len(ratios)} executed objects: min={ratios[0]:.2f} p10={q[0]:.2f} median={statistics.median(ratios):.2f} p90={q[-1]:.2f} max={ratios[-1]:.2f}")
N = len(st["tests"])
share_s = sorted(r[3] / N for r in rows)
share_e = sorted(r[2] / N for r in rows)
def dist(xs):
    q = statistics.quantiles(xs, n=10)
    return f"min={xs[0]:.3f} p10={q[0]:.3f} median={statistics.median(xs):.3f} p90={q[-1]:.3f} max={xs[-1]:.3f} mean={statistics.mean(xs):.3f}"
print(f"share of all tests selected per {app} object, static:   {dist(share_s)}")
print(f"share of all tests selected per {app} object, executed: {dist(share_e)}")
# expected selection size for a single-object edit, averaged over objects
print(f"mean selected tests per one-object edit: static={statistics.mean(r[3] for r in rows):.1f} executed={statistics.mean(r[2] for r in rows):.1f} (of {N})")
nonexec = [r for r in rows if r[2] == 0]
print(f"objects never executed: {len(nonexec)}; of those statically reached by >=1 test: {sum(1 for r in nonexec if r[3])}")
w = defaultdict(int)
for t, v in st["tests"].items():
    for k in v["wildcardsReached"]:
        w[k] += 1
print("tests whose closure reaches each wildcard node:", dict(w))
wd = defaultdict(int)
for t, v in st["tests"].items():
    for k in v["directWildcards"]:
        wd[k] += 1
print("tests whose own procedures hold a wildcard site:", dict(wd))
tot = sorted(v["total"] for v in st["tests"].values())
print(f"closure size (all nodes) per test: min={tot[0]} median={statistics.median(tot)} max={tot[-1]}")
if "--dump" in sys.argv:
    json.dump({"rows": rows, "misses": misses}, open(sys.argv[sys.argv.index("--dump") + 1], "w"))
