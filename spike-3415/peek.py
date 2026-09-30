import json, sys, collections
d = json.load(open(sys.argv[1]))
print("entries", len(d))
ks = list(d)[:3]
for k in ks: print(k, len(d[k]), d[k][:8])
allkeys = collections.Counter(k for v in d.values() for k in v)
kinds = collections.Counter(k.split("|")[0] + "|" + (k.split("|")[1] if "|" in k else "") for k in allkeys)
print("distinct keys", len(allkeys), kinds.most_common(20))
for pat in sys.argv[2:]:
    print(pat, sum(1 for t, v in d.items() if t != "<bundle>" and pat in v))
print("sample ev keys", [k for k in allkeys if k.startswith("ev|Codeunit|80|")][:20])
