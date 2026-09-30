"""Drive al-runner --server through a list of runTests requests; one JSON result file per request.

usage: srv.py <label> <seed:0|1> <bundle> <req>...   where req is plain | record
"""
import json, os, shutil, subprocess, sys, time, threading

S = os.path.dirname(os.path.abspath(__file__))
W = "/home/stefan/Documents/Repos/community/BusinessCentral.AL.Runner/.claude/worktrees/agent-a49b61abb5ee11c8c"
HOME = os.environ["HOME"]
label, seed, bundle, reqs = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4:]
out = f"{S}/runs/{label}"
os.makedirs(out, exist_ok=True)
cache = f"{S}/cache"
shutil.rmtree(f"{cache}/affected-baseline", ignore_errors=True)
env = dict(os.environ, AL_RUNNER_EMIT_TIMEOUT_SEC="3600", DOTNET_GCHeapCount="2",
           AL_RUNNER_EVENT_DUMP=f"{out}/events.json")
if seed == "1":
    env["AL_RUNNER_RECORD_ALL_APP_EVENTS"] = "1"
args = [f"{W}/AlRunner/bin/Release/net8.0/al-runner", "--server", "--bc-version", "28.4.53241.53955",
        "--package-cache", f"{HOME}/.al-runner/platform-apps-28.4.53241.53955",
        "--package-cache", f"{HOME}/.al-runner/test-apps-28.4.53241.53955",
        "--cache", cache, "--test-timeout", "300",
        f"--test-data={HOME}/.al-runner/test-data/28.4.53241.54318/BusinessCentral-W1.bak",
        "--test-data-company", "CRONUS International Ltd_"]
err = open(f"{out}/server.stderr.log", "w")
t0 = time.time()
p = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=err, text=True, cwd=S, env=env)

peak = [0]
def sample():
    while p.poll() is None:
        try:
            tot = 0
            for pid in [p.pid] + [int(x) for x in open(f"/proc/{p.pid}/task/{p.pid}/children").read().split()]:
                for line in open(f"/proc/{pid}/status"):
                    if line.startswith("VmRSS:"): tot += int(line.split()[1])
            peak[0] = max(peak[0], tot)
        except Exception:
            pass
        time.sleep(1)
threading.Thread(target=sample, daemon=True).start()

line = p.stdout.readline()
print(f"ready after {time.time()-t0:.1f}s: {line.strip()}", flush=True)
summary_all = []
for i, r in enumerate(reqs):
    req = {"command": "runTests", "sourcePaths": [bundle]}
    if r == "record":
        req["perTestCoverage"] = True
    t = time.time()
    p.stdin.write(json.dumps(req) + "\n"); p.stdin.flush()
    tests = {}
    summary = None
    while True:
        l = p.stdout.readline()
        if not l:
            print("server stdout closed", flush=True); break
        try:
            o = json.loads(l)
        except Exception:
            continue
        if o.get("type") == "test":
            tests[o["name"]] = {"status": o["status"], "message": (o.get("message") or "")[:300]}
        elif o.get("type") == "summary" or "error" in o:
            summary = o; break
    el = time.time() - t
    if os.path.exists(f"{out}/events.json"):
        os.replace(f"{out}/events.json", f"{out}/events-{i}-{r}.json")
    json.dump({"req": r, "elapsed": el, "summary": {k: v for k, v in (summary or {}).items() if k != "coverage"},
               "tests": tests}, open(f"{out}/req-{i}-{r}.json", "w"))
    s = summary or {}
    msg = f"req {i} {r}: {el:.1f}s total={s.get('total')} passed={s.get('passed')} failed={s.get('failed')} sel={s.get('selection')} peakRSS={peak[0]//1024}MB"
    print(msg, flush=True); summary_all.append(msg)
p.stdin.write(json.dumps({"command": "shutdown"}) + "\n"); p.stdin.flush()
try: p.wait(120)
except Exception: p.kill()
open(f"{out}/summary.txt", "w").write("\n".join(summary_all) + f"\npeakRSS={peak[0]//1024}MB\n")
print("done", flush=True)
