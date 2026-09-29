#!/usr/bin/env python3
"""Reference data for the soundness check: per-test executed statements from al-runner --server.

usage: ref_drive.py <al-runner binary> <fresh cache dir> <out prefix> <bundle dir> [<bundle dir> ...]

Runs every test of the named source bundles once with perTestCoverage:true
(flags: --auto-provision --test-data --test-data-company cronus --bc-version 28.1) and writes
<out prefix>-raw.jsonl (every server line) and <out prefix>-executed.json
({"tests": {test: {file: [lines hit]}}, "status": {test: {status, message}}}).
Name the app bundle AND its test bundle: naming only the test bundle loads the app's packaged .app.
"""
import json, os, subprocess, sys, time

binary, cache, prefix, *bundles = sys.argv[1:]
if os.path.exists(cache):
    sys.exit(f"refusing: cache dir must be fresh: {cache}")
raw = open(prefix + "-raw.jsonl", "w")
err = open(prefix + "-stderr.log", "w")
t0 = time.time()
proc = subprocess.Popen(
    [binary, "--server", "--auto-provision", "--test-data", "--test-data-company", "cronus", "--bc-version", "28.1", "--cache", cache],
    cwd=os.path.dirname(os.path.abspath(bundles[0])), stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=err, text=True, bufsize=1)
raw.write(json.dumps({"event": "ready", "line": proc.stdout.readline().strip(), "secs": round(time.time() - t0, 1)}) + "\n")
proc.stdin.write(json.dumps({"command": "runTests", "sourcePaths": [os.path.abspath(b) for b in bundles], "perTestCoverage": True}) + "\n")
proc.stdin.flush()
summary, status = None, {}
while True:
    line = proc.stdout.readline()
    if not line:
        break
    raw.write(line if line.endswith("\n") else line + "\n")
    try:
        m = json.loads(line)
    except ValueError:
        continue
    if m.get("type") == "test":
        status[m["name"]] = {"status": m["status"], "message": (m.get("message") or "")[:300]}
    if m.get("type") == "summary" or ("error" in m and m.get("type") != "test"):
        summary = m
        break
raw.write(json.dumps({"event": "done", "secs": round(time.time() - t0, 1)}) + "\n")
try:
    proc.stdin.write('{"command":"shutdown"}\n'); proc.stdin.flush(); proc.wait(120)
except Exception:
    proc.kill()
if not summary or "perTestCoverage" not in summary:
    sys.exit("refusing: no summary with perTestCoverage — see the -raw.jsonl / -stderr.log")
tests = {}
for e in summary["perTestCoverage"]:
    tests[e["test"]] = {f["file"]: sorted({s["line"] for s in f["statements"] if s["hits"] > 0}) for f in e["coverage"]}
json.dump({"tests": tests, "status": status}, open(prefix + "-executed.json", "w"))
print(f"tests={len(tests)} pass={sum(1 for v in status.values() if v['status'] == 'pass')} "
      f"fail={sum(1 for v in status.values() if v['status'] != 'pass')} secs={time.time() - t0:.1f}")
