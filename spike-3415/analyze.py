"""Selection vs truth for the #3415 spike.

usage: analyze.py <events.json> <baseline junit> <probe-run-dir-prefix>
Probe runs are expected at runs/cli-probe<N>/junit.xml.
"""
import json, os, sys, xml.etree.ElementTree as ET

S = os.path.dirname(os.path.abspath(__file__))
events_path, base_junit = sys.argv[1], sys.argv[2]
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
        sk = tc.find("skipped")
        if f is not None:
            res[key] = ("fail", (f.get("message") or "") + " " + (f.text or ""))
        elif sk is not None:
            res[key] = ("skipped", "")
        else:
            res[key] = ("pass", "")
    return res

ev = json.load(open(events_path))
ev.pop("<bundle>", None)
base = junit(base_junit)
all_tests = set(base)
print(f"bucket tests {len(all_tests)}; recorded tests {len(ev)}; recorded-not-in-junit {len(set(ev)-all_tests)}; "
      f"junit-not-recorded {len(all_tests-set(ev))}")
base_fail = {t for t, (s, _) in base.items() if s != "pass"}
out = {}
union_sel, union_truth = set(), set()
for n, keys in KEYS.items():
    sel = {t for t, v in ev.items() if any(k in v for k in keys)}
    pj = f"{S}/runs/cli-probe{n}/junit.xml"
    if not os.path.exists(pj):
        print(f"probe {n}: selected {len(sel)} (no truth run yet)")
        continue
    pr = junit(pj)
    truth = {t for t, (s, m) in pr.items() if s != "pass" and f"PROBE-{n}" in m}
    changed_no_probe = {t for t, (s, m) in pr.items()
                        if t not in truth and base.get(t, ("?",))[0] != s}
    misses = truth - sel
    over = sel - truth
    union_sel |= sel
    union_truth |= truth
    r = {
        "keys": keys, "selected": len(sel), "truth_probe_failures": len(truth),
        "misses": sorted(misses), "over_selected": len(over),
        "over_selected_failing_in_baseline": len(over & base_fail),
        "selected_failing_in_baseline": len(sel & base_fail),
        "truth_failing_in_baseline": len(truth & base_fail),
        "outcome_changed_without_probe_text": sorted(changed_no_probe),
        "probe_run_totals": {"tests": len(pr), "fail": sum(1 for s, _ in pr.values() if s != "pass")},
    }
    out[n] = r
    print(f"probe {n} {keys[0]}: selected {len(sel)}, truth {len(truth)}, misses {len(misses)}, "
          f"over {len(over)} (of which {len(over & base_fail)} already failing), "
          f"selected-already-failing {len(sel & base_fail)}, changed-without-PROBE {len(changed_no_probe)}")
    for m in sorted(misses)[:15]:
        print("   MISS", m, "| baseline:", base.get(m, ("?", ""))[0], "| probe msg:", pr[m][1][:160].replace("\n", " "))
    for c in sorted(changed_no_probe)[:10]:
        print("   CHANGED", c, base.get(c, ("?",))[0], "->", pr[c][0], pr[c][1][:160].replace("\n", " "))
print(f"union: selected {len(union_sel)}, truth {len(union_truth)}, misses {len(union_truth - union_sel)}")
json.dump(out, open(f"{S}/analysis-{os.path.basename(os.path.dirname(events_path))}.json", "w"), indent=1)
