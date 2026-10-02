#!/usr/bin/env python3
"""Run a command and report the peak memory of its whole PROCESS TREE, not of one process.

    tools/process-tree-peak.py --label erm-jobs2 --out results.jsonl -- al-runner <bundle> --jobs 2 ...

`/usr/bin/time -v` reports the largest single process, so for `--jobs N` it answers "how big is the
biggest worker" and not "what did the run cost". This samples /proc once a second and records:

  peak_tree_pss_mb   proportional set size summed over the tree (shared pages counted once)
  peak_tree_rss_mb   resident size summed over the tree (shared pages counted per process)
  peak_single_rss_mb the largest single process: one worker's own peak
  wall_s, cpu_s      elapsed and user+system time of the tree (cpu_s does not move with the
                     other jobs on the machine, wall_s does)
  load1_mean         the machine's 1-minute load while it ran: say it with any wall time

Memory is read from `smaps_rollup`, so this needs Linux. It exits 3 ("could not measure"),
printing no record, rather than zeros that read as a small run, when the platform cannot read it,
when no sample saw the process alive (a command shorter than one interval), or when any read
failed for a reason other than the process having exited.

If the machine's available memory drops below --kill-below-mb the tree is killed and the record
says `killed_low_memory: true`: a measurement must not take the box down with it.

Exit codes: 0 measured (the child's own exit code is in the record as `rc`), 2 usage,
3 could not measure.
"""
from __future__ import annotations

import argparse
import json
import os
import resource
import signal
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    import agent_stdio as _stdio
except Exception:  # pragma: no cover - a copy detached from its sibling module
    _stdio = None
if _stdio is not None:
    # stdout/stderr must not die on a non-cp1252 character in a command line or a path
    _stdio.enable_utf8_stdio()


def meminfo_available_kb() -> int:
    with open("/proc/meminfo") as f:
        for line in f:
            if line.startswith("MemAvailable:"):
                return int(line.split()[1])
    raise OSError("no MemAvailable in /proc/meminfo")


def descendants(root: int) -> list[int]:
    kids: dict[int, list[int]] = {}
    for p in os.listdir("/proc"):
        if not p.isdigit():
            continue
        try:
            with open(f"/proc/{p}/stat") as f:
                s = f.read()
            ppid = int(s[s.rindex(")") + 2:].split()[1])
        except (OSError, ValueError):
            continue
        kids.setdefault(ppid, []).append(int(p))
    out = [root]
    i = 0
    while i < len(out):
        out += kids.get(out[i], [])
        i += 1
    return out


class ReadFailure(Exception):
    """A smaps_rollup read failed for a reason other than the process having exited."""


def rss_pss_kb(pid: int) -> tuple[int, int]:
    rss = pss = 0
    try:
        with open(f"/proc/{pid}/smaps_rollup") as f:
            for line in f:
                if line.startswith("Rss:"):
                    rss = int(line.split()[1])
                elif line.startswith("Pss:"):
                    pss = int(line.split()[1])
    except (FileNotFoundError, ProcessLookupError):
        pass   # the process exited between listing and reading
    except OSError as e:
        raise ReadFailure(f"pid {pid}: {e}") from e
    return rss, pss


def measure(cmd: list[str], label: str, kill_below_kb: int, interval: float = 1.0) -> dict:
    """The record, with `samples` (how many times the tree was seen alive) and `read_failures`."""
    t0 = time.time()
    proc = subprocess.Popen(cmd, start_new_session=True)
    peak_rss = peak_pss = peak_single = 0
    min_avail = 1 << 60
    load_sum = 0.0
    samples = 0
    killed = False
    read_failures: list[str] = []
    seen = 0
    while proc.poll() is None:
        rss_sum = pss_sum = single = 0
        for pid in descendants(proc.pid):
            try:
                r, p = rss_pss_kb(pid)
            except ReadFailure as e:
                read_failures.append(str(e))
                continue
            if pid == proc.pid and r > 0:
                seen += 1   # the root itself was read alive: this sample measured something
            rss_sum += r
            pss_sum += p
            single = max(single, r)
        peak_rss = max(peak_rss, rss_sum)
        peak_pss = max(peak_pss, pss_sum)
        peak_single = max(peak_single, single)
        avail = meminfo_available_kb()
        min_avail = min(min_avail, avail)
        with open("/proc/loadavg") as f:
            load_sum += float(f.read().split()[0])
        samples += 1
        if avail < kill_below_kb and not killed:
            killed = True
            os.killpg(proc.pid, signal.SIGKILL)
        time.sleep(interval)
    rc = proc.wait()
    ru = resource.getrusage(resource.RUSAGE_CHILDREN)
    return {
        "label": label, "rc": rc, "wall_s": round(time.time() - t0, 1),
        "cpu_s": round(ru.ru_utime + ru.ru_stime, 1),
        "peak_tree_pss_mb": peak_pss // 1024, "peak_tree_rss_mb": peak_rss // 1024,
        "peak_single_rss_mb": peak_single // 1024,
        "samples": seen, "read_failures": read_failures,
        "min_available_mb": min_avail // 1024, "load1_mean": round(load_sum / max(1, samples), 1),
        "cores": os.cpu_count(), "killed_low_memory": killed, "cmd": cmd,
    }


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--label", default="run")
    ap.add_argument("--out", help="append the record to this JSONL file")
    ap.add_argument("--kill-below-mb", type=int, default=1000,
                    help="kill the tree when the machine's available memory falls below this")
    ap.add_argument("--interval", type=float, default=1.0)
    ap.add_argument("cmd", nargs=argparse.REMAINDER)
    args = ap.parse_args(argv)
    cmd = args.cmd[1:] if args.cmd[:1] == ["--"] else args.cmd
    if not cmd:
        print("usage: process-tree-peak.py [--label L] [--out F] -- command ...", file=sys.stderr)
        return 2
    if not sys.platform.startswith("linux") or not os.path.exists("/proc/self/smaps_rollup"):
        print("process-tree-peak: needs Linux /proc/<pid>/smaps_rollup; nothing was measured.", file=sys.stderr)
        return 3
    rec = measure(cmd, args.label, args.kill_below_mb * 1024, args.interval)
    if rec["samples"] == 0 or rec["read_failures"]:
        why = (f"{len(rec['read_failures'])} read(s) failed, first: {rec['read_failures'][0]}"
               if rec["read_failures"]
               else "no sample saw the process alive (the command ended within one --interval)")
        print(f"process-tree-peak: nothing was measured: {why}.", file=sys.stderr)
        return 3
    line = json.dumps(rec)
    if args.out:
        with open(args.out, "a") as f:
            f.write(line + "\n")
    print(line)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
