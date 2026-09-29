#!/usr/bin/env python3
"""Mean/median tests selected by a one-procedure edit in one app: static (procedure graph) vs executed."""
import json, statistics, sys
from collections import defaultdict

# usage: proc_edit_sizes.py <ref-raw.jsonl> <proc-project result.json>... [--app Name]
app = sys.argv[sys.argv.index("--app") + 1] if "--app" in sys.argv else sys.exit("--app <app name> is required")
args = [a for a in sys.argv[1:] if a not in ("--app", app)]
raw = [json.loads(l) for l in open(args[0]) if '"summary"' in l][0]
for v in args[1:]:
    st = json.load(open(v))
    byf = defaultdict(list)
    for o in st["objects"]:
        byf[o["file"]].append(o)
    ex = defaultdict(set)
    for e in raw["perTestCoverage"]:
        for f in e["coverage"]:
            for s in f["statements"]:
                if s["hits"] > 0:
                    c = [o for o in byf.get(f["file"], []) if o["start"] <= s["line"] <= o["end"]]
                    if len(c) == 1 and c[0]["App"] == app:
                        ex[(c[0]["key"], s["scope"].lower())].add(e["test"])
    S = defaultdict(set)
    for p in st["procs"]:
        if p["key"].endswith("@" + app):
            S[(p["key"], p["proc"].lower())] |= set(p["testIdx"])
    N = len(st["testNames"])
    sc = [len(x) for x in S.values()]
    ec = [len(ex.get(k, ())) for k in S]
    print(f"{v}: procedures={len(S)} static mean={statistics.mean(sc):.1f} median={statistics.median(sc)}  executed mean={statistics.mean(ec):.1f} median={statistics.median(ec)}  of {N} tests")
