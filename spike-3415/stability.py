"""Are two recordings of the same bucket the same? And what differs between runs' results."""
import json, os, sys
S = os.path.dirname(os.path.abspath(__file__))
a = json.load(open(f"{S}/runs/srv-noseed-1/events-0-record.json"))
b = json.load(open(f"{S}/runs/srv-seed/events-0-record.json"))
c = json.load(open(f"{S}/runs/srv-seed/events-2-record.json"))
for name, x, y in [("noseed-cold vs seed-cold", a, b), ("seed-cold vs seed-warm", b, c)]:
    diff = [t for t in x if t != "<bundle>" and set(x[t]) != set(y.get(t, []))]
    print(name, "tests with differing key sets:", len(diff))
    for t in diff[:5]:
        print("  ", t, "only-first", sorted(set(x[t]) - set(y.get(t, [])))[:5], "only-second", sorted(set(y.get(t, [])) - set(x[t]))[:5])
    for k in ["ev|Codeunit|80|OnBeforePostSalesDoc", "ev|Codeunit|80|OnAfterPostSalesDoc", "trig|Table|37"]:
        sx = {t for t, v in x.items() if k in v}; sy = {t for t, v in y.items() if k in v}
        print("   ", k, len(sx), len(sy), "sym-diff", sorted(sx ^ sy)[:5])
sizes = sorted(len(v) for t, v in a.items() if t != "<bundle>")
print("keys per test: median", sizes[len(sizes)//2], "p90", sizes[int(len(sizes)*.9)], "max", sizes[-1])
print("dump size MB", os.path.getsize(f"{S}/runs/srv-noseed-1/events-0-record.json") / 1e6)
def res(p):
    return {n: v["status"] for n, v in json.load(open(p))["tests"].items()}
r1 = res(f"{S}/runs/srv-noseed-1/req-1-plain.json"); r2 = res(f"{S}/runs/srv-seed/req-1-plain.json")
r0 = res(f"{S}/runs/srv-seed/req-0-record.json"); r3 = res(f"{S}/runs/srv-seed/req-2-record.json")
print("plain(no seeding ever) vs plain(after recording):", [(t, r1[t], r2.get(t)) for t in r1 if r1[t] != r2.get(t)])
print("record cold vs record warm:", [(t, r0[t], r3.get(t)) for t in r0 if r0[t] != r3.get(t)])
