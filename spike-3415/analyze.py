"""Selection vs truth for the #3415 spike.

usage: analyze.py <events.json> <control junit>
Probe runs are read from runs/cli-probe<N>/junit.xml. The control run is the bucket plus a
probe app with no subscriber (same app set, so the same test data load).

truth(strict) = tests failing with PROBE-<n> in the message.
truth(broad)  = strict + tests whose outcome differs from the control run (the probe error was
                caught and replaced, e.g. by BC's error-message collection).
Selection by test = tests whose recorded keys contain the event key.
Selection by codeunit = every test of a codeunit in which at least one test raised it.
"""
import json, os, sys, xml.etree.ElementTree as ET

S = os.path.dirname(os.path.abspath(__file__))
events_path, control_junit = sys.argv[1], sys.argv[2]
KEYS = {
    1: ["ev|Codeunit|80|OnBeforePostSalesDoc"],
    2: ["ev|Codeunit|80|OnAfterPostSalesDoc"],
    3: ["trig|Table|37", "trig|?"],   # how #4999 records a table field trigger event: per table
    4: ["ev|Codeunit|86|OnBeforeOnRun"],
}

def junit(path):
    res = {}
    for tc in ET.parse(path).getroot().iter("testcase"):
        key = f"{tc.get('classname')}.{tc.get('name')}"
        f = tc.find("failure") if tc.find("failure") is not None else tc.find("error")
        if f is not None:
            res[key] = ("fail", (f.get("message") or "") + " " + (f.text or ""))
        elif tc.find("skipped") is not None:
            res[key] = ("skipped", "")
        else:
            res[key] = ("pass", "")
    return res

def cu(t): return t.split(".", 1)[0]

ev = json.load(open(events_path)); ev.pop("<bundle>", None)
ctl = junit(control_junit)
ctl_fail = {t for t, (s, _) in ctl.items() if s != "pass"}
print(f"bucket tests {len(ctl)}; control failing {len(ctl_fail)}; recorded tests {len(ev)}")
tests_by_cu = {}
for t in ctl: tests_by_cu.setdefault(cu(t), set()).add(t)
out = {}
U = {k: set() for k in ["sel", "selcu", "strict", "broad"]}
for n, keys in KEYS.items():
    sel = {t for t, v in ev.items() if any(k in v for k in keys)}
    selcu = {t for c in {cu(t) for t in sel} for t in tests_by_cu.get(c, ())}
    pj = f"{S}/runs/cli-probe{n}/junit.xml"
    if not os.path.exists(pj):
        print(f"probe {n}: selected {len(sel)} tests / {len(selcu)} by codeunit (no truth run yet)"); continue
    pr = junit(pj)
    strict = {t for t, (s, m) in pr.items() if s != "pass" and f"PROBE-{n}" in m}
    changed = {t for t, (s, m) in pr.items() if t not in strict and ctl.get(t, ("?",))[0] != s}
    broad = strict | changed
    for k, v in [("sel", sel), ("selcu", selcu), ("strict", strict), ("broad", broad)]: U[k] |= v
    r = {"keys": keys, "selected_tests": len(sel), "selected_by_codeunit": len(selcu),
         "codeunits_selected": len({cu(t) for t in sel}),
         "truth_strict": len(strict), "truth_broad": len(broad),
         "miss_strict_test": sorted(strict - sel), "miss_broad_test": sorted(broad - sel),
         "miss_strict_codeunit": sorted(strict - selcu), "miss_broad_codeunit": sorted(broad - selcu),
         "over_test_vs_broad": len(sel - broad), "over_codeunit_vs_broad": len(selcu - broad),
         "selected_already_failing_in_control": len(sel & ctl_fail),
         "selcu_already_failing_in_control": len(selcu & ctl_fail),
         "changed_without_probe_text": {t: [ctl[t][0], pr[t][0], pr[t][1][:200]] for t in sorted(changed)},
         "probe_run_fail": sum(1 for s, _ in pr.values() if s != "pass")}
    out[n] = r
    print(f"probe {n} {keys[0]}: sel {len(sel)} tests ({r['codeunits_selected']} codeunits -> {len(selcu)} tests); "
          f"truth strict {len(strict)} broad {len(broad)}; "
          f"MISSES by-test strict/broad {len(strict-sel)}/{len(broad-sel)}, by-codeunit {len(strict-selcu)}/{len(broad-selcu)}; "
          f"over by-test {len(sel-broad)}, by-codeunit {len(selcu-broad)}; "
          f"selected already failing in control {len(sel & ctl_fail)} (by-codeunit {len(selcu & ctl_fail)})")
    for m in sorted(broad - selcu)[:12]:
        print("   MISS(cu)", m, "| control:", ctl.get(m, ("?",))[0], "| probe:", pr[m][0], pr[m][1][:150].replace("\n", " "))
print("UNION:", {k: len(v) for k, v in U.items()},
      "misses by-test strict/broad", len(U["strict"] - U["sel"]), len(U["broad"] - U["sel"]),
      "by-codeunit", len(U["strict"] - U["selcu"]), len(U["broad"] - U["selcu"]))
out["union"] = {k: len(v) for k, v in U.items()}
json.dump(out, open(f"{S}/analysis.json", "w"), indent=1)
