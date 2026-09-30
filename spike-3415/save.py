"""Copy harness scripts and small result files from the scratchpad into the spike branch directory."""
import gzip, os, shutil, glob

S = os.path.dirname(os.path.abspath(__file__))
D = "/home/stefan/Documents/Repos/community/BusinessCentral.AL.Runner/.claude/worktrees/agent-a49b61abb5ee11c8c/spike-3415"
os.makedirs(f"{D}/results", exist_ok=True)
for f in ["srv.py", "runcli.sh", "mkprobes.py", "peek.py", "waitrun.sh", "st.sh", "save.py", "analyze.py"]:
    if os.path.exists(f"{S}/{f}"): shutil.copy(f"{S}/{f}", f"{D}/{f}")
if os.path.isdir(f"{S}/probes"):
    shutil.copytree(f"{S}/probes", f"{D}/probes", dirs_exist_ok=True)
for run in sorted(glob.glob(f"{S}/runs/*")):
    name = os.path.basename(run)
    out = f"{D}/results/{name}"
    os.makedirs(out, exist_ok=True)
    for f in os.listdir(run):
        p = f"{run}/{f}"
        if f in ("rc.txt", "elapsed.txt", "summary.txt") or (f.startswith("req-") and os.path.getsize(p) < 2_000_000):
            shutil.copy(p, f"{out}/{f}")
        elif f == "run.log":
            with open(p, errors="replace") as src, open(f"{out}/run-tail.log", "w") as dst:
                dst.writelines(src.readlines()[-40:])
        elif f == "results.json" or f == "junit.xml" or (f.startswith("events-") and f.endswith(".json")) \
                or (f.startswith("req-") and f.endswith(".json")):
            with open(p, "rb") as src, gzip.open(f"{out}/{f}.gz", "wb") as dst:
                shutil.copyfileobj(src, dst)
for f in glob.glob(f"{S}/*.md") + glob.glob(f"{S}/analysis*.json"):
    shutil.copy(f, f"{D}/results/")
print(sum(os.path.getsize(os.path.join(dp, f)) for dp, _, fs in os.walk(D) for f in fs) // 1024, "KB in", D)
