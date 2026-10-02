#!/usr/bin/env python3
"""Does process-tree-peak.py measure the TREE, and say so when it could not measure?

The point of the tool is that `--jobs N` memory is a property of N processes, so each case is a
shape that a single-process reading gets wrong:

  * a parent whose CHILDREN hold the memory (the largest single process is a child; the tree
    peak is above it),
  * a child's exit code survives into the record,
  * a run below the --kill-below-mb floor is killed and says so,
  * a platform that cannot read smaps_rollup refuses with exit 3 instead of printing zeros.

Run: python3 tools/test_process_tree_peak.py
"""
from __future__ import annotations

import importlib.util
import io
import os
import sys
import tempfile
from contextlib import redirect_stderr, redirect_stdout

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("process_tree_peak", os.path.join(HERE, "process-tree-peak.py"))
ptp = importlib.util.module_from_spec(_spec)
sys.modules["process_tree_peak"] = ptp
_spec.loader.exec_module(ptp)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
        return
    FAILURES.append(name)
    print(f"  FAIL {name}" + (f" {detail}" if detail else ""))


HOLD = (
    "import subprocess,sys,time\n"
    "kid='import time; b=bytearray(150*1024*1024); b[::4096]=b\"x\"*len(b[::4096]); time.sleep(1.5)'\n"
    "ps=[subprocess.Popen([sys.executable,'-c',kid]) for _ in range(3)]\n"
    "[p.wait() for p in ps]\n"
)


def main() -> int:
    if not sys.platform.startswith("linux"):
        print("skipped: needs Linux /proc")
        return 0

    rec = ptp.measure([sys.executable, "-c", HOLD], "tree", kill_below_kb=0, interval=0.2)
    check("the child's exit code is recorded", rec["rc"] == 0, str(rec))
    check("a single process is about one child, not the tree",
          100 <= rec["peak_single_rss_mb"] < 300, str(rec))
    check("the tree peak counts every child, so it is above the largest one",
          rec["peak_tree_rss_mb"] >= rec["peak_single_rss_mb"] * 2, str(rec))

    rec = ptp.measure([sys.executable, "-c", "import sys; sys.exit(7)"], "rc", kill_below_kb=0, interval=0.2)
    check("a failing child is measured, with its own exit code", rec["rc"] == 7, str(rec))

    # an impossible floor: every machine is below 1 TB of available memory, so the tree is killed
    rec = ptp.measure([sys.executable, "-c", "import time; time.sleep(30)"], "kill",
                      kill_below_kb=1 << 40, interval=0.2)
    check("below the floor the tree is killed and the record says so",
          rec["killed_low_memory"] and rec["wall_s"] < 10 and rec["rc"] != 0, str(rec))

    with tempfile.TemporaryDirectory() as d:
        out = os.path.join(d, "r.jsonl")
        with redirect_stdout(io.StringIO()):
            code = ptp.main(["--label", "x", "--out", out, "--interval", "0.2", "--",
                             sys.executable, "-c", "pass"])
        check("main exits 0 and appends one JSONL record",
              code == 0 and len(open(out).read().splitlines()) == 1)

    err = io.StringIO()
    with redirect_stderr(err):
        code = ptp.main(["--label", "x"])
    check("no command is a usage error, exit 2", code == 2)

    real = ptp.os.path.exists
    try:
        ptp.os.path.exists = lambda p: False if p == "/proc/self/smaps_rollup" else real(p)
        err = io.StringIO()
        with redirect_stderr(err):
            code = ptp.main(["--", sys.executable, "-c", "pass"])
    finally:
        ptp.os.path.exists = real
    check("a platform that cannot read smaps_rollup exits 3 and measures nothing",
          code == 3 and "nothing was measured" in err.getvalue())

    print(f"{len(FAILURES)} failure(s)")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
