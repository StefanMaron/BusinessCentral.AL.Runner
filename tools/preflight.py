#!/usr/bin/env python3
"""Check that this box can be trusted to run an autonomous AL Runner cycle.

This is `.claude/skills/autonomous-cycle/SKILL.md`'s "Preflight" section as an
executable check with a real exit code, rather than prose. It implements what
that skill already specifies; it does not add policy of its own.

It exists because the prose version failed, measurably. A coordinator ran ~20
agents across an evening without ever performing step 5 ("Headroom. Read free
RAM and disk, and derive worker and job counts from them.") and filled `/tmp`,
which on that box is a 7.7 GB **tmpfs** rather than the 400 GB `/` that "check
disk" brings to mind. Every shell on the machine then failed without naming the
cause: `echo` exited 1, `ls` exited 2, `python3 -c "print(1)"` exited 120 with
"error flushing std streams", and a redirect produced a zero-byte file because
the shell died between open() and write(). Three agents diagnosed it as a broken
shell before a fourth surfaced the real errno, EDQUOT. About 40 minutes lost.

So the report below always names the **mount point and filesystem type** behind
every number. "Disk is fine" was true of `/` and false of `/tmp` at the same
moment, and that ambiguity is the whole incident.

A check a busy coordinator can decline is not a check.

Usage:
    tools/preflight.py                 # human-readable report
    tools/preflight.py --json          # machine-readable, same verdicts
    tools/preflight.py --strict        # warnings are also non-zero
    tools/preflight.py --reap          # remove worktrees of MERGED PRs, if clean
    tools/preflight.py --with-corpus   # also run the corpus baseline (minutes)
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import re
import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from typing import Any, Iterable, Optional

GIB = 1024 ** 3

# The skill offers these as a *starting point* and says explicitly to derive from
# measurement rather than hardcode a worker count. They are the per-worker
# footprint, not a worker count: the count is computed from them and from what
# this box actually has free, in suggest_workers() below.
PER_WORKER_BYTES_NO_TEST_DATA = int(1.1 * GIB)
PER_WORKER_BYTES_WITH_TEST_DATA = int(2.3 * GIB)

# Percentage-full alone never hard-fails: a 500 GB disk at 99% still has 5 GB
# free, which is room for several workers, whereas a 7.7 GB tmpfs at 99% has 77
# MB and is the state that broke the box. Absolute free space is what decides;
# the percentage is a heads-up. (The brief's "a box at 60% disk is not the same
# as one at 99%" is exactly this split.)
SPACE_WARN_USED_PCT = 90

# A budget window this far consumed is worth saying out loud. It never hard-fails
# -- an exhausted budget produces *no* answers, which is visible, rather than
# wrong answers, which are not. See classify_budget().
BUDGET_WARN_FRACTION = 0.85
BUDGET_STALE_MINUTES = 60

OMARCHY_USAGE_BIN = "/usr/share/omarchy/bin/omarchy-agent-usage-claude"

EXIT_MEANING = {
    0: "every check passed (warnings may be present; see --strict)",
    1: "at least one check FAILED - this box would produce untrustworthy results",
    2: "--strict was given and at least one check WARNED (nothing failed)",
    3: "preflight could not complete - bad usage, or no checks ran",
}


# --------------------------------------------------------------------------
# process helpers
# --------------------------------------------------------------------------
@dataclass
class Ran:
    rc: int
    out: str
    err: str
    timed_out: bool = False

    @property
    def ok(self) -> bool:
        return self.rc == 0 and not self.timed_out


def run(argv: list[str], *, cwd: Optional[str] = None, timeout: float = 30,
        env: Optional[dict] = None, stdin: bytes = b"") -> Ran:
    """Run a command, never raise, and treat a timeout as its own outcome.

    A timeout is distinct from a failure on purpose: a locked signing agent makes
    `git commit` hang forever rather than fail, and an unattended loop simply
    stops there. That is the difference between "signing is broken" and "signing
    will silently eat your cycle", and the report says which.
    """
    try:
        p = subprocess.run(argv, cwd=cwd, env=env, input=stdin,
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                           timeout=timeout)
    except subprocess.TimeoutExpired:
        return Ran(rc=124, out="", err="", timed_out=True)
    except (FileNotFoundError, PermissionError, OSError) as exc:
        return Ran(rc=127, out="", err=str(exc))
    return Ran(rc=p.returncode,
               out=p.stdout.decode("utf-8", "replace"),
               err=p.stderr.decode("utf-8", "replace"))


def run_retry(argv: list[str], *, attempts: int = 3, **kw) -> Ran:
    """Retry a network-touching command. This box's network times out
    intermittently, and a single timeout read as a FAIL would halt a healthy
    loop."""
    last = Ran(rc=127, out="", err="never ran")
    for _ in range(attempts):
        last = run(argv, **kw)
        if last.ok:
            return last
    return last


def human(n: Optional[float]) -> str:
    if n is None:
        return "?"
    n = float(n)
    for unit in ("B", "KiB", "MiB", "GiB", "TiB"):
        if abs(n) < 1024 or unit == "TiB":
            return f"{n:.1f} {unit}" if unit != "B" else f"{int(n)} B"
        n /= 1024
    return f"{n:.1f} TiB"


# --------------------------------------------------------------------------
# data
# --------------------------------------------------------------------------
@dataclass
class Mount:
    device: str
    fstype: str
    total: int
    used: int
    free: int
    used_pct: int
    mountpoint: str


@dataclass
class Worktree:
    path: str
    head: str
    branch: Optional[str]
    detached: bool = False
    is_main: bool = False
    size_bytes: Optional[int] = None


@dataclass
class Disposition:
    reapable: bool
    reason: str


@dataclass
class Suggestion:
    workers: int
    limited_by: str
    detail: dict


@dataclass
class BudgetWindow:
    label: str
    fraction: float             # 0..1, share of the window already consumed
    resets_at: Optional[dt.datetime]

    def minutes_left(self, now: dt.datetime) -> Optional[float]:
        if self.resets_at is None:
            return None
        return (self.resets_at - now).total_seconds() / 60.0


@dataclass
class CheckResult:
    name: str
    status: str                 # PASS | WARN | FAIL | SKIP
    summary: str
    command: str = ""
    detail: list = field(default_factory=list)
    remedy: str = ""
    data: dict = field(default_factory=dict)


# --------------------------------------------------------------------------
# pure parsing
# --------------------------------------------------------------------------
def parse_df(text: str) -> list[Mount]:
    """Parse `df -PT -B1 <paths>`.

    -P guarantees one line per filesystem (without it a long device name wraps
    and every field shifts), -T adds the filesystem type -- the column this whole
    tool exists to print -- and -B1 gives exact bytes rather than a rounded
    human figure that cannot be compared against a per-worker budget.

    The mount point is the last field and may contain spaces, so the split is
    bounded at 6 rather than done greedily.
    """
    mounts: list[Mount] = []
    for line in text.splitlines():
        line = line.rstrip("\n")
        if not line.strip() or line.startswith("Filesystem"):
            continue
        parts = line.split(None, 6)
        if len(parts) < 7:
            continue
        device, fstype, total, used, free, capacity, mountpoint = parts
        try:
            total_i, used_i, free_i = int(total), int(used), int(free)
            pct = int(capacity.rstrip("%"))
        except ValueError:
            continue
        mounts.append(Mount(device=device, fstype=fstype, total=total_i, used=used_i,
                            free=free_i, used_pct=pct, mountpoint=mountpoint))
    return mounts


def mount_for_path(path: str, mounts: Iterable[Mount]) -> Optional[Mount]:
    """Longest-prefix match, so /tmp/x resolves to the tmpfs and not to /.

    Getting this wrong is the incident: "/" had 400 GB free at the exact moment
    /tmp had none, and a check that resolved a /tmp path to "/" would have
    reported the box healthy.
    """
    if not os.path.isabs(path):
        return None
    path = os.path.abspath(path)
    best: Optional[Mount] = None
    for m in mounts:
        prefix = m.mountpoint.rstrip("/") + "/"
        if path == m.mountpoint or path.startswith(prefix):
            if best is None or len(m.mountpoint) > len(best.mountpoint):
                best = m
    return best


def parse_meminfo(text: str) -> dict:
    """/proc/meminfo, converted to bytes.

    MemAvailable, not MemFree: MemFree excludes reclaimable page cache and so
    understates what a new worker can actually get, which would suppress the
    worker count for no reason.
    """
    out: dict = {}
    for line in text.splitlines():
        m = re.match(r"^(\w+):\s+(\d+)(?:\s+(\w+))?", line)
        if not m:
            continue
        value = int(m.group(2))
        if (m.group(3) or "").lower() == "kb":
            value *= 1024
        out[m.group(1)] = value
    return out


def parse_worktree_porcelain(text: str) -> list[Worktree]:
    """Parse `git worktree list --porcelain`. The first record is the main
    checkout; git guarantees that ordering."""
    out: list[Worktree] = []
    cur: dict = {}

    def flush():
        if cur.get("path"):
            branch = cur.get("branch")
            if branch and branch.startswith("refs/heads/"):
                branch = branch[len("refs/heads/"):]
            out.append(Worktree(path=cur["path"], head=cur.get("head", ""),
                                branch=branch, detached=bool(cur.get("detached")),
                                is_main=not out))
        cur.clear()

    for line in text.splitlines():
        if not line.strip():
            flush()
            continue
        key, _, value = line.partition(" ")
        if key == "worktree":
            flush()
            cur["path"] = value
        elif key == "HEAD":
            cur["head"] = value
        elif key == "branch":
            cur["branch"] = value
        elif key == "detached":
            cur["detached"] = True
    flush()
    return out


def _iso(value: Any) -> Optional[dt.datetime]:
    raw = str(value or "").strip()
    if not raw:
        return None
    try:
        parsed = dt.datetime.fromisoformat(raw.replace("Z", "+00:00"))
    except ValueError:
        return None
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=dt.timezone.utc)
    return parsed


def parse_omarchy_limits(text: str) -> tuple[list[BudgetWindow], dict]:
    """Parse `omarchy-agent-usage-claude --limits-only`.

    Its `percent` field is a **fraction in 0..1**, not a number of percent. That
    is not a guess: the producer's normalize_utilization() divides by 100 when
    the upstream payload is percent-scaled and clamps to 1.0, and the reading is
    confirmed by measurement -- the session window read 0.43, then 0.48 twenty
    minutes later under 9-12 concurrent agents, i.e. 15 percentage points/hour,
    matching the burn measured independently.

    A value above 1.0 would mean the producer changed its convention, so it is
    treated as already being a percentage rather than silently rendered as
    4800%.
    """
    try:
        payload = json.loads(text)
    except (ValueError, TypeError):
        return [], {}
    windows: list[BudgetWindow] = []
    for entry in payload.get("limits") or []:
        if not isinstance(entry, dict):
            continue
        label = str(entry.get("label") or "").strip()
        raw = entry.get("percent")
        if label == "" or raw is None:
            continue
        try:
            fraction = float(raw)
        except (TypeError, ValueError):
            continue
        if fraction > 1.0:
            fraction = fraction / 100.0
        windows.append(BudgetWindow(label=label, fraction=max(0.0, min(1.0, fraction)),
                                    resets_at=_iso(entry.get("resetsAt"))))
    meta = {k: payload.get(k) for k in
            ("tierLabel", "todayTotalTokens", "todayPrompts", "todaySessions", "updatedAt")
            if k in payload}
    return windows, meta


def parse_ccusage_blocks(text: str) -> Optional[dict]:
    """Parse `ccusage blocks --json` and return the ACTIVE 5-hour block.

    Deliberately the `--json` form. ccusage's table rendering has a `%` column
    that is a guess -- it is measured against the largest block ccusage has ever
    seen, not against the plan cap -- and reading it as the cap once led a
    coordinator to conclude there was no headroom when the real figure was 37%.
    The JSON has no such field at all, so parsing it cannot make that mistake:
    absolute tokens and cost only.
    """
    try:
        payload = json.loads(text)
    except (ValueError, TypeError):
        return None
    blocks = payload.get("blocks")
    if not isinstance(blocks, list):
        return None
    for block in blocks:
        if isinstance(block, dict) and block.get("isActive") and not block.get("isGap"):
            projection = block.get("projection") or {}
            burn = block.get("burnRate") or {}
            return {
                "startTime": block.get("startTime"),
                "endTime": block.get("endTime"),
                "totalTokens": block.get("totalTokens"),
                "costUSD": block.get("costUSD"),
                "remainingMinutes": projection.get("remainingMinutes"),
                "tokensPerMinute": burn.get("tokensPerMinute"),
            }
    return None


# --------------------------------------------------------------------------
# pure policy
# --------------------------------------------------------------------------
def classify_space(free: int, used_pct: int, per_worker: int) -> tuple[str, str]:
    """Absolute free space decides; percent-full only warns.

    Returns (status, reason). The reason is written to be read by someone who has
    never run this before, so it says how many workers fit rather than only how
    many bytes are free.
    """
    workers = int(free // per_worker) if per_worker > 0 else 0
    per = human(per_worker)
    if workers < 1:
        return ("FAIL", f"{human(free)} free is not enough for even one worker "
                        f"({per} each) - work started here will run out of space mid-run")
    if workers < 2:
        return ("WARN", f"{human(free)} free leaves room for {workers} worker "
                        f"({per} each)")
    if used_pct >= SPACE_WARN_USED_PCT:
        return ("WARN", f"{used_pct}% used, though {human(free)} free is still room for "
                        f"{workers} workers ({per} each)")
    return ("PASS", f"{human(free)} free is room for {workers} workers ({per} each)")


def suggest_workers(*, tmp_free: int, repo_free: int, mem_available: int, cpus: int,
                    per_worker: int) -> Suggestion:
    """Derive a concurrent-agent count from what this box actually has.

    Nothing here is a constant except the per-worker footprint the caller passes
    in, which is itself the skill's starting figure. Each resource is divided by
    that footprint independently and the smallest wins, so the report can name
    the resource that is actually limiting rather than printing one number with
    no cause.
    """
    candidates = {
        "/tmp free space": int(tmp_free // per_worker) if per_worker else 0,
        "repo filesystem free space": int(repo_free // per_worker) if per_worker else 0,
        "available RAM": int(mem_available // per_worker) if per_worker else 0,
        "CPU count": int(cpus),
    }
    limited_by = min(candidates, key=lambda k: (candidates[k], k))
    return Suggestion(workers=max(0, candidates[limited_by]), limited_by=limited_by,
                      detail=candidates)


def unpushed_against_pr(head: str, pr: Optional[dict]) -> Optional[int]:
    """How many local commits the remote has never seen, judged by the PR.

    After a squash merge GitHub deletes the head branch, so `git log @{u}..HEAD`
    has no upstream left to resolve and reports nothing rather than reporting
    zero. The PR's headRefOid is the surviving record of what was actually
    pushed. Returns None when there is no PR, because "cannot prove anything was
    pushed" must not be rendered as "nothing is unpushed".
    """
    if not pr or not pr.get("headRefOid"):
        return None
    return 0 if head == pr["headRefOid"] else 1


def disposition(wt: Worktree, *, pr: Optional[dict], dirty: bool,
                unpushed: Optional[int], is_current: bool = False) -> Disposition:
    """Decide whether a worktree may be removed.

    The merged test asks the **pull request**, never `git merge-base
    --is-ancestor`. This repository squash-merges, so the branch head of a merged
    PR is not an ancestor of main and never becomes one: measured here against
    three merged branches, ancestry reported all three as unmerged. Built on
    ancestry, a reaper reaps nothing and calls every worktree live -- safe by
    luck. The mirror of the same mistake, on a repo that merges with a merge
    commit, deletes unmerged work.
    """
    if wt.is_main:
        return Disposition(False, "the main checkout is never removed")
    if is_current:
        return Disposition(False, "you are standing in this worktree")
    if wt.detached or not wt.branch:
        return Disposition(False, "detached HEAD - no branch, so no pull request to consult")
    if pr is None:
        return Disposition(False, f"no pull request found for {wt.branch} - merged state "
                                  f"unknown, so it is kept")
    state = str(pr.get("state") or "").upper()
    if state != "MERGED":
        return Disposition(False, f"PR #{pr.get('number')} is {state or 'in an unknown state'}, "
                                  f"not MERGED")
    if dirty:
        return Disposition(False, f"PR #{pr.get('number')} is merged, but the tree has "
                                  f"uncommitted changes - reported, not removed")
    if unpushed is None:
        return Disposition(False, f"PR #{pr.get('number')} is merged, but nothing proves the "
                                  f"local commits were pushed - reported, not removed")
    if unpushed > 0:
        return Disposition(False, f"PR #{pr.get('number')} is merged, but {unpushed} local "
                                  f"commit(s) were never pushed - reported, not removed")
    return Disposition(True, f"PR #{pr.get('number')} is MERGED, tree is clean and fully "
                             f"pushed")


def classify_budget(windows: list[BudgetWindow], now: dt.datetime) -> tuple[str, str, list]:
    """Report remaining budget, and derive a sustainable burn from it.

    Never a hard FAIL. An exhausted budget produces *no* answers, which is
    visible the moment it happens; the checks that hard-fail are the ones where
    work proceeds and produces *wrong* answers. That split is the brief's, and
    the skill's.

    The derived number is the share of the window still available divided by the
    hours until it resets: the rate the loop can sustain from here to the reset
    without exhausting it. The alternative -- printing a percentage and letting
    the reader guess -- is what left a coordinator unable to tell 43%-with-an-hour
    -left from 43%-with-four-hours-left.
    """
    if not windows:
        return ("WARN", "budget headroom UNKNOWN - no usage source answered", [])
    detail: list[str] = []
    worst = "PASS"
    for w in windows:
        pct = w.fraction * 100.0
        mins = w.minutes_left(now)
        remaining = max(0.0, 1.0 - w.fraction) * 100.0
        line = f"{w.label}: {pct:.1f}% used, {remaining:.1f}% left"
        if mins is None:
            line += " (no reset time reported)"
        elif mins <= 0:
            line += " (its window has already reset; this figure describes a period that is over)"
        else:
            hours = mins / 60.0
            line += (f", resets in {mins:.0f} min"
                     f" - sustainable from here: {remaining / max(hours, 1e-6):.1f}%/hour")
        detail.append(line)
        if w.fraction >= BUDGET_WARN_FRACTION:
            worst = "WARN"
    if worst == "WARN":
        hot = ", ".join(f"{w.label} at {w.fraction * 100:.0f}%"
                        for w in windows if w.fraction >= BUDGET_WARN_FRACTION)
        return ("WARN", f"budget nearly consumed: {hot}", detail)
    top = max(windows, key=lambda w: w.fraction)
    return ("PASS", f"budget has headroom (highest window: {top.label} at "
                    f"{top.fraction * 100:.0f}% used)", detail)


def overall_exit(results: list[CheckResult], strict: bool = False) -> int:
    if not results:
        return 3
    statuses = {r.status for r in results}
    if "FAIL" in statuses:
        return 1
    if strict and "WARN" in statuses:
        return 2
    return 0


# --------------------------------------------------------------------------
# rendering
# --------------------------------------------------------------------------
_ORDER = {"FAIL": 0, "WARN": 1, "SKIP": 2, "PASS": 3}


def render_report(results: list[CheckResult], *, strict: bool, header: str = "") -> str:
    lines: list[str] = []
    if header:
        lines.append(header)
        lines.append("")
    width = max([len(r.name) for r in results], default=10)
    for r in results:
        lines.append(f"{r.status:<4}  {r.name:<{width}}  {r.summary}")
        for d in r.detail:
            lines.append(f"{'':<6}{'':<{width}}  {d}")
        if r.command:
            lines.append(f"{'':<6}{'':<{width}}  $ {r.command}")
        if r.remedy:
            for i, chunk in enumerate(r.remedy.split("\n")):
                prefix = "-> what to do: " if i == 0 else "               "
                lines.append(f"{'':<6}{'':<{width}}  {prefix}{chunk}")
        lines.append("")
    code = overall_exit(results, strict)
    counts = {s: sum(1 for r in results if r.status == s) for s in ("PASS", "WARN", "FAIL", "SKIP")}
    lines.append(f"{counts['PASS']} passed, {counts['WARN']} warned, "
                 f"{counts['FAIL']} failed, {counts['SKIP']} skipped")
    lines.append(f"exit {code}: {EXIT_MEANING[code]}")
    return "\n".join(lines)


def build_json(results: list[CheckResult], *, strict: bool, extra: Optional[dict] = None) -> dict:
    code = overall_exit(results, strict)
    out = {
        "exit_code": code,
        "exit_code_meaning": EXIT_MEANING[code],
        "exit_codes": {str(k): v for k, v in EXIT_MEANING.items()},
        "strict": strict,
        "generated_at": dt.datetime.now(dt.timezone.utc).isoformat(),
        "checks": [
            {"name": r.name, "status": r.status, "summary": r.summary,
             "command": r.command, "detail": list(r.detail), "remedy": r.remedy,
             "data": r.data}
            for r in results
        ],
    }
    if extra:
        out.update(extra)
    return out


# --------------------------------------------------------------------------
# measuring this box
# --------------------------------------------------------------------------
def dir_size(path: str, budget: int = 400_000) -> tuple[int, bool]:
    """Disk usage of a directory tree, in bytes, with an entry budget.

    st_blocks * 512 rather than st_size, so sparse files and small-file overhead
    are counted the way df counts them -- the point is to compare against a
    filesystem's free space, not to sum logical file lengths. Returns
    (bytes, complete); complete is False when the budget ran out, so the report
    can say "at least" instead of printing a number that quietly undercounts.
    """
    total = 0
    seen = 0
    stack = [path]
    while stack:
        current = stack.pop()
        try:
            with os.scandir(current) as it:
                for entry in it:
                    seen += 1
                    if seen > budget:
                        return total, False
                    try:
                        st = entry.stat(follow_symlinks=False)
                    except OSError:
                        continue
                    if entry.is_dir(follow_symlinks=False):
                        stack.append(entry.path)
                    total += getattr(st, "st_blocks", 0) * 512
        except OSError:
            continue
    return total, True


def git_repo_root(start: str) -> Optional[str]:
    r = run(["git", "-C", start, "rev-parse", "--show-toplevel"])
    return r.out.strip() if r.ok else None


def repo_slug(repo: str) -> Optional[str]:
    r = run(["git", "-C", repo, "remote", "get-url", "origin"])
    if not r.ok:
        return None
    m = re.search(r"github\.com[:/]+([^/]+)/(.+?)(?:\.git)?/?$", r.out.strip())
    return f"{m.group(1)}/{m.group(2)}" if m else None


def check_space(label: str, path: str, mounts: list[Mount], per_worker: int) -> CheckResult:
    mount = mount_for_path(path, mounts)
    if mount is None:
        return CheckResult(name=label, status="FAIL",
                           summary=f"could not determine the filesystem holding {path}",
                           command=f"df -PT -B1 {path}",
                           remedy="Run `df -PT` by hand and check the path exists.")
    status, reason = classify_space(mount.free, mount.used_pct, per_worker)
    detail = [f"filesystem {mount.device} ({mount.fstype}), mounted on {mount.mountpoint}, "
              f"{human(mount.total)} total, {mount.used_pct}% used"]
    if mount.fstype == "tmpfs":
        detail.append("this mount is a tmpfs: it lives in RAM, it is NOT the filesystem "
                      "holding the repository, and filling it breaks every process on the "
                      "box with EDQUOT rather than a clear error.")
    remedy = ""
    if status != "PASS":
        remedy = (f"Free space on {mount.mountpoint}. Agent scratch and per-agent --cache "
                  f"roots are the usual occupants; `tools/preflight.py --reap` removes "
                  f"worktrees whose PR is merged. Check the biggest consumers with "
                  f"`du -xh -d1 {mount.mountpoint} | sort -h | tail -20`.")
    return CheckResult(name=label, status=status, summary=f"{path} -> {mount.mountpoint}: {reason}",
                       command=f"df -PT -B1 {path}", detail=detail, remedy=remedy,
                       data={"mountpoint": mount.mountpoint, "fstype": mount.fstype,
                             "free_bytes": mount.free, "total_bytes": mount.total,
                             "used_pct": mount.used_pct, "path": path})


def check_memory(mem: dict, per_worker: int) -> CheckResult:
    available = mem.get("MemAvailable", 0)
    total = mem.get("MemTotal", 0)
    status, reason = classify_space(available, 0, per_worker)
    detail = [f"MemTotal {human(total)}, MemAvailable {human(available)}, "
              f"SwapFree {human(mem.get('SwapFree'))}",
              "MemAvailable, not MemFree: MemFree ignores reclaimable page cache and "
              "would understate what a new worker can get."]
    remedy = ""
    if status != "PASS":
        remedy = ("Reduce concurrency, or stop idle agents. On any long run set MemoryHigh "
                  "below MemoryMax so a cgroup throttles before the kernel's global OOM "
                  "killer starts choosing victims elsewhere on the machine.")
    return CheckResult(name="memory", status=status, summary=reason,
                       command="cat /proc/meminfo", detail=detail, remedy=remedy,
                       data={"mem_available_bytes": available, "mem_total_bytes": total})


def check_headroom(tmp_dir: str, repo: str, mounts: list[Mount], mem: dict,
                   per_worker: int, per_worker_label: str) -> CheckResult:
    tmp_mount = mount_for_path(tmp_dir, mounts)
    repo_mount = mount_for_path(repo, mounts)
    cpus = os.cpu_count() or 1
    sug = suggest_workers(tmp_free=tmp_mount.free if tmp_mount else 0,
                          repo_free=repo_mount.free if repo_mount else 0,
                          mem_available=mem.get("MemAvailable", 0), cpus=cpus,
                          per_worker=per_worker)
    detail = [f"per-worker footprint assumed: {human(per_worker)} ({per_worker_label})",
              "each resource divided by that footprint: "
              + ", ".join(f"{k} -> {v}" for k, v in sorted(sug.detail.items()))]
    if sug.workers == 0:
        return CheckResult(name="headroom", status="FAIL",
                           summary=f"this box has room for 0 concurrent agents "
                                   f"(limited by {sug.limited_by})",
                           command="df -PT -B1 + /proc/meminfo + nproc",
                           detail=detail,
                           remedy="Do not start a cycle. Free the limiting resource first "
                                  "and re-run this preflight.",
                           data={"workers": 0, "limited_by": sug.limited_by,
                                 "candidates": sug.detail})
    return CheckResult(name="headroom", status="PASS",
                       summary=f"run at most {sug.workers} concurrent agents "
                               f"(limited by {sug.limited_by})",
                       command="df -PT -B1 + /proc/meminfo + nproc", detail=detail,
                       data={"workers": sug.workers, "limited_by": sug.limited_by,
                             "candidates": sug.detail, "per_worker_bytes": per_worker})


def pr_map(slug: Optional[str]) -> tuple[dict, str]:
    """branch -> newest PR record. One API call, not one per branch."""
    if not slug or not shutil.which("gh"):
        return {}, "unavailable"
    r = run_retry(["gh", "pr", "list", "--repo", slug, "--state", "all", "--limit", "300",
                   "--json", "number,headRefName,state,headRefOid,mergedAt"], timeout=90)
    if not r.ok:
        return {}, f"gh pr list failed: {(r.err or r.out).strip()[:200]}"
    try:
        prs = json.loads(r.out)
    except ValueError:
        return {}, "gh pr list returned unparseable JSON"
    out: dict = {}
    for pr in prs:
        branch = pr.get("headRefName")
        if branch and branch not in out:
            out[branch] = pr
    return out, "ok"


def collect_worktrees(repo: str, prs: dict, measure: bool = True) -> list[tuple]:
    r = run(["git", "-C", repo, "worktree", "list", "--porcelain"])
    if not r.ok:
        return []
    cwd = os.path.realpath(os.getcwd())
    rows = []
    for wt in parse_worktree_porcelain(r.out):
        exists = os.path.isdir(wt.path)
        real = os.path.realpath(wt.path) if exists else wt.path
        is_current = exists and (cwd == real or cwd.startswith(real.rstrip("/") + "/"))
        pr = prs.get(wt.branch) if wt.branch else None
        dirty = False
        unpushed: Optional[int] = None
        if exists:
            st = run(["git", "-C", wt.path, "status", "--porcelain"], timeout=60)
            dirty = bool(st.out.strip()) if st.ok else True
            up = run(["git", "-C", wt.path, "rev-list", "--count", "@{u}..HEAD"], timeout=60)
            if up.ok and up.out.strip().isdigit():
                unpushed = int(up.out.strip())
            else:
                # No upstream left to resolve -- GitHub deletes the head branch on
                # merge. The PR's headRefOid is what still records what was pushed.
                unpushed = unpushed_against_pr(wt.head, pr)
            if measure:
                size, complete = dir_size(wt.path)
                wt.size_bytes = size
        d = disposition(wt, pr=pr, dirty=dirty, unpushed=unpushed, is_current=is_current)
        rows.append((wt, pr, dirty, unpushed, d, exists))
    return rows


def check_worktrees(rows: list[tuple], repo: str, pr_status: str) -> CheckResult:
    agents = [r for r in rows if not r[0].is_main]
    reapable = [r for r in agents if r[4].reapable]
    missing = [r for r in agents if not r[5]]
    dirty = [r for r in agents if r[5] and r[2]]
    total = sum((r[0].size_bytes or 0) for r in agents)
    detail = [f"{len(agents)} agent worktree(s), {human(total)} on disk in total"]
    for wt, pr, is_dirty, unpushed, d, exists in sorted(
            agents, key=lambda r: -(r[0].size_bytes or 0)):
        mark = "REAP " if d.reapable else "keep "
        detail.append(f"{mark}{human(wt.size_bytes):>9}  {os.path.basename(wt.path)}  "
                      f"[{wt.branch or 'detached'}] - {d.reason}")
    if pr_status != "ok":
        detail.append(f"pull-request states could not be read ({pr_status}); every worktree "
                      f"is therefore reported as merged-state unknown and kept.")
    if missing:
        detail.append(f"{len(missing)} registration(s) point at a directory that no longer "
                      f"exists")
    status = "PASS"
    remedy = ""
    if pr_status != "ok":
        status = "WARN"
        remedy = ("Authenticate gh (`gh auth status`) so merged worktrees can be identified; "
                  "until then nothing is reapable.")
    elif reapable:
        status = "WARN"
        remedy = (f"{len(reapable)} worktree(s) belong to MERGED pull requests and are clean. "
                  f"Remove them with `tools/preflight.py --reap` (add --dry-run first). "
                  f"Nothing ever deleting these is what left 82 worktrees and 10 GB behind "
                  f"in the impl-69 incident.")
    elif missing:
        status = "WARN"
        remedy = "Run `git worktree prune` to drop registrations for directories that are gone."
    summary = (f"{len(agents)} agent worktree(s), {human(total)}; {len(reapable)} reapable, "
               f"{len(dirty)} with uncommitted work")
    return CheckResult(name="worktrees", status=status, summary=summary,
                       command="git worktree list --porcelain + gh pr list --state all",
                       detail=detail, remedy=remedy,
                       data={"count": len(agents), "total_bytes": total,
                             "reapable": [r[0].path for r in reapable],
                             "dirty": [r[0].path for r in dirty],
                             "missing": [r[0].path for r in missing]})


def check_stale_scratch(tmp_dir: str, rows: list[tuple], stale_hours: float) -> CheckResult:
    now = dt.datetime.now().timestamp()
    entries = []
    try:
        with os.scandir(tmp_dir) as it:
            for entry in it:
                try:
                    st = entry.stat(follow_symlinks=False)
                except OSError:
                    continue
                if st.st_uid != os.getuid() or not entry.is_dir(follow_symlinks=False):
                    continue
                size, complete = dir_size(entry.path, budget=200_000)
                entries.append((size, complete, (now - st.st_mtime) / 3600.0, entry.path))
    except OSError as exc:
        return CheckResult(name="stale-scratch", status="WARN",
                           summary=f"could not scan {tmp_dir}: {exc}",
                           command=f"scan of {tmp_dir}",
                           remedy=f"Check {tmp_dir} exists and is readable.")
    entries.sort(reverse=True)
    stale = [e for e in entries if e[2] >= stale_hours and e[0] > 256 * 1024 * 1024]
    orphan = [r for r in rows if not r[5]]
    total = sum(e[0] for e in entries)
    detail = [f"{len(entries)} directory(ies) owned by this user under {tmp_dir}, "
              f"{human(total)} in total"]
    for size, complete, age_h, path in entries[:8]:
        detail.append(f"{human(size):>9}{'+' if not complete else ' '}  {age_h:6.1f}h old  {path}")
    if orphan:
        detail.append(f"{len(orphan)} git worktree registration(s) point at directories that "
                      f"are gone (a killed run leaves these)")
    status = "PASS"
    remedy = ""
    if stale or orphan:
        status = "WARN"
        bits = []
        if stale:
            bits.append(f"{len(stale)} scratch director(ies) over 256 MiB and idle for "
                        f"{stale_hours:g}h+ ({human(sum(e[0] for e in stale))} total). "
                        f"Confirm no agent owns them, then delete.")
        if orphan:
            bits.append("Run `git worktree prune` to clear dead registrations.")
        remedy = "\n".join(bits)
    return CheckResult(name="stale-scratch", status=status,
                       summary=f"{human(total)} of scratch under {tmp_dir}; "
                               f"{len(stale)} stale, {len(orphan)} dead worktree registration(s)",
                       command=f"stat + recursive size of {tmp_dir}/*", detail=detail,
                       remedy=remedy,
                       data={"scratch_root": tmp_dir, "total_bytes": total,
                             "stale": [e[3] for e in stale],
                             "orphan_registrations": [r[0].path for r in orphan]})


def check_push(repo: str, identity: str) -> CheckResult:
    """`git ls-remote` proves READ. Only a push proves push.

    The skill says push auth fails silently when it is routed through an
    interactive credential agent, and a false PASS here has happened. --dry-run
    performs the full negotiation, including authentication, and transfers
    nothing, so the probe ref is never created.
    """
    ref = f"refs/heads/preflight-probe-{identity}"
    cmd = f"git push --dry-run origin HEAD:{ref}"
    r = run_retry(["git", "-C", repo, "push", "--dry-run", "--porcelain", "origin",
                   f"HEAD:{ref}"], timeout=120, attempts=2)
    if r.timed_out:
        return CheckResult(name="push", status="FAIL",
                           summary="the push probe timed out - credentials are probably "
                                   "waiting on an interactive prompt nobody will answer",
                           command=cmd,
                           remedy="Configure a non-interactive credential helper, or "
                                  "`gh auth setup-git`, then re-run.")
    if not r.ok:
        return CheckResult(name="push", status="FAIL",
                           summary=f"push was refused: {(r.err or r.out).strip().splitlines()[-1] if (r.err or r.out).strip() else 'no message'}",
                           command=cmd,
                           detail=[(r.err or r.out).strip()[:600]],
                           remedy="Re-authenticate (`gh auth login`, then `gh auth setup-git`) "
                                  "and confirm the account has push permission on origin.")
    return CheckResult(name="push", status="PASS",
                       summary="push works (dry-run negotiated with origin; nothing was written)",
                       command=cmd,
                       detail=["git ls-remote would only have proven read access."])


def check_commit(repo: str) -> CheckResult:
    """Either signing succeeds, or signing is off.

    A locked signing agent makes `git commit` hang forever rather than fail, so
    an unattended loop stops there with no error to report. The probe is given a
    timeout precisely so that "hangs" becomes a verdict instead of a symptom.

    Objects are written to a throwaway GIT_OBJECT_DIRECTORY, so the probe leaves
    nothing in the repository's object store.
    """
    ident = run(["git", "-C", repo, "var", "GIT_COMMITTER_IDENT"])
    if not ident.ok:
        return CheckResult(name="commit", status="FAIL",
                           summary="git has no committer identity, so every commit will fail",
                           command="git var GIT_COMMITTER_IDENT",
                           detail=[(ident.err or "").strip()[:300]],
                           remedy="Set `git config --global user.name` and `user.email`.")
    signing = run(["git", "-C", repo, "config", "--get", "commit.gpgsign"]).out.strip().lower()
    if signing not in ("true", "1", "yes", "on"):
        return CheckResult(name="commit", status="PASS",
                           summary=f"commits work; signing is off, so no agent can lock "
                                   f"(committer: {ident.out.strip().split(' 1')[0]})",
                           command="git var GIT_COMMITTER_IDENT; git config --get commit.gpgsign")
    import tempfile
    objdir = tempfile.mkdtemp(prefix="preflight-objects-")
    try:
        env = dict(os.environ, GIT_OBJECT_DIRECTORY=objdir)
        tree = run(["git", "-C", repo, "hash-object", "-t", "tree", "-w", "--stdin"],
                   env=env, stdin=b"")
        if not tree.ok:
            return CheckResult(name="commit", status="FAIL",
                               summary="could not write a probe object to test signing",
                               command="git hash-object -t tree -w --stdin",
                               detail=[(tree.err or "").strip()[:300]],
                               remedy="Check the repository is writable.")
        probe = run(["git", "-C", repo, "commit-tree", "-S", "-m", "preflight signing probe",
                     tree.out.strip()], env=env, timeout=25)
        if probe.timed_out:
            return CheckResult(name="commit", status="FAIL",
                               summary="signing HUNG - `git commit` will hang forever and an "
                                       "unattended loop will simply stop there",
                               command="git commit-tree -S -m 'preflight signing probe' <tree>",
                               remedy="Unlock the signing key (`gpg-connect-agent updatestartuptty "
                                      "/bye`), or turn signing off for the loop with "
                                      "`git config commit.gpgsign false`.")
        if not probe.ok:
            return CheckResult(name="commit", status="FAIL",
                               summary="commit signing is enabled but fails",
                               command="git commit-tree -S -m 'preflight signing probe' <tree>",
                               detail=[(probe.err or probe.out).strip()[:400]],
                               remedy="Fix the signing key, or set `commit.gpgsign false`.")
        return CheckResult(name="commit", status="PASS",
                           summary="commits work and signing succeeds without prompting",
                           command="git commit-tree -S -m 'preflight signing probe' <tree>",
                           detail=["Probe objects were written to a throwaway "
                                   "GIT_OBJECT_DIRECTORY, not to the repository."])
    finally:
        shutil.rmtree(objdir, ignore_errors=True)


def check_github(slug: Optional[str]) -> CheckResult:
    """Report what this account can do. Do not branch on it.

    The skill is explicit: a missing permission is a precondition failure to
    report, not a second mode to implement. The loop behaves identically for
    everyone; it just refuses to start when it cannot do its job.
    """
    if not shutil.which("gh"):
        return CheckResult(name="github", status="WARN",
                           summary="the gh CLI is not installed on this box",
                           command="command -v gh",
                           detail=["Web and remote Claude Code sessions have no gh at all; "
                                   "there the mcp__github__* tools are the interface "
                                   "(.claude/rules/github-access.md). On a local box that "
                                   "runs the unattended loop, gh is expected."],
                           remedy="Install and authenticate gh, or run the loop from a session "
                                  "that has the GitHub MCP tools.")
    auth = run_retry(["gh", "auth", "status"], timeout=45)
    if not auth.ok:
        return CheckResult(name="github", status="FAIL",
                           summary="gh is installed but not authenticated",
                           command="gh auth status",
                           detail=[(auth.err or auth.out).strip()[:400]],
                           remedy="Run `gh auth login`.")
    login = run_retry(["gh", "api", "user", "--jq", ".login"], timeout=45).out.strip()
    if not slug:
        return CheckResult(name="github", status="WARN",
                           summary=f"authenticated as {login or '?'}, but origin is not a "
                                   f"recognisable GitHub remote",
                           command="git remote get-url origin",
                           remedy="Point origin at the GitHub repository.")
    perm = run_retry(["gh", "api", f"repos/{slug}", "--jq", ".permissions"], timeout=45)
    if not perm.ok:
        return CheckResult(name="github", status="FAIL",
                           summary=f"could not read permissions on {slug}",
                           command=f"gh api repos/{slug} --jq .permissions",
                           detail=[(perm.err or perm.out).strip()[:400]],
                           remedy="Check network access and that the token has repo scope.")
    try:
        perms = json.loads(perm.out)
    except ValueError:
        perms = {}
    can_push = bool(perms.get("push") or perms.get("maintain") or perms.get("admin"))
    detail = [f"account: {login or 'unknown'}; repository: {slug}",
              "permissions: " + ", ".join(f"{k}={v}" for k, v in sorted(perms.items())),
              f"merge a PR: {'yes' if can_push else 'no'}; label and assign: "
              f"{'yes' if (can_push or perms.get('triage')) else 'no'} (both need push or triage)"]
    if not can_push:
        return CheckResult(name="github", status="FAIL",
                           summary=f"{login or 'this account'} cannot push to {slug}, so the "
                                   f"loop cannot open or merge anything",
                           command=f"gh api repos/{slug} --jq .permissions", detail=detail,
                           remedy="Grant push access, or run the loop as an account that has it. "
                                  "This is a precondition to report, not a second mode to "
                                  "implement.")
    return CheckResult(name="github", status="PASS",
                       summary=f"authenticated as {login} with push access to {slug}",
                       command=f"gh auth status; gh api repos/{slug} --jq .permissions",
                       detail=detail, data={"login": login, "permissions": perms})


def check_budget(skip_fallback: bool = False) -> CheckResult:
    """Budget headroom: a resource that, exhausted, strands work mid-task.

    Layered, and a missing tool is never read as "no constraint":
      1. omarchy-agent-usage-claude --limits-only - the authoritative percent and
         reset time, straight from Anthropic's OAuth usage endpoint.
      2. ccusage blocks --json - absolute tokens and cost for the active 5-hour
         block. Never its table's % column, which is measured against the largest
         block ccusage has seen rather than the plan cap.
      3. UNKNOWN, reported as a precondition.

    Omarchy-specific, so the binary is probed rather than assumed.
    """
    now = dt.datetime.now(dt.timezone.utc)
    binary = OMARCHY_USAGE_BIN if os.path.exists(OMARCHY_USAGE_BIN) \
        else shutil.which("omarchy-agent-usage-claude")
    if binary:
        r = run([binary, "--limits-only"], timeout=60)
        if r.ok:
            windows, meta = parse_omarchy_limits(r.out)
            status, summary, detail = classify_budget(windows, now)
            detail = list(detail)
            if meta.get("tierLabel"):
                detail.append(f"plan: {meta['tierLabel']}; today: "
                              f"{meta.get('todayTotalTokens', '?')} tokens across "
                              f"{meta.get('todayPrompts', '?')} prompts, "
                              f"{meta.get('todaySessions', '?')} session(s)")
            updated = _iso(meta.get("updatedAt"))
            if updated is not None:
                age_min = (now - updated).total_seconds() / 60.0
                detail.append(f"figures measured {age_min:.0f} min ago (updatedAt "
                              f"{meta['updatedAt']})")
                if age_min > BUDGET_STALE_MINUTES and status == "PASS":
                    status = "WARN"
                    summary += f" - but the reading is {age_min:.0f} min old"
            remedy = ""
            if status == "WARN":
                remedy = ("Pace the cycle, or wait for the window to reset before starting a "
                          "long unit of work. Never leave an agent mid-task when a budget "
                          "boundary is near.")
            return CheckResult(name="budget", status=status, summary=summary,
                               command=f"{binary} --limits-only", detail=detail, remedy=remedy,
                               data={"source": "omarchy",
                                     "limits": [{"label": w.label, "fraction": w.fraction,
                                                 "percent_used": round(w.fraction * 100, 2),
                                                 "resets_at": w.resets_at.isoformat()
                                                 if w.resets_at else None,
                                                 "minutes_left": w.minutes_left(now)}
                                                for w in windows],
                                     "meta": meta})
    fallback_note = ("omarchy-agent-usage-claude is not on this box, so the authoritative "
                     "percent-of-cap is unavailable.")
    if not skip_fallback and shutil.which("npx"):
        r = run(["npx", "--yes", "ccusage@latest", "blocks", "--json"], timeout=180)
        block = parse_ccusage_blocks(r.out) if r.ok else None
        if block:
            detail = [fallback_note,
                      f"active 5-hour block started {block['startTime']}, "
                      f"{block.get('remainingMinutes', '?')} min remaining",
                      f"{block.get('totalTokens', '?')} tokens, "
                      f"${block.get('costUSD', 0):.2f} so far, "
                      f"{block.get('tokensPerMinute') or 0:.0f} tokens/min",
                      "ccusage reports absolute usage, not a share of the plan cap. Its "
                      "table's % column is measured against the largest block ccusage has "
                      "ever seen, so it is not a cap and is deliberately not read here."]
            return CheckResult(name="budget", status="WARN",
                               summary="budget headroom UNKNOWN as a share of the cap; "
                                       "absolute usage measured instead",
                               command="npx --yes ccusage@latest blocks --json",
                               detail=detail,
                               remedy="Install the omarchy usage plugin for an authoritative "
                                      "percent, or pace from the absolute figures above.",
                               data={"source": "ccusage", "active_block": block})
    return CheckResult(name="budget", status="WARN",
                       summary="budget headroom UNKNOWN - no usage source answered",
                       command=f"{OMARCHY_USAGE_BIN} --limits-only; npx ccusage@latest blocks --json",
                       detail=[fallback_note,
                               "A missing tool is not the same as no constraint: the budget "
                               "still exists, it just cannot be read here."],
                       remedy="Install the omarchy Claude usage plugin, or ccusage, so a cycle "
                              "can be paced against a measured figure rather than a guess.")


def check_corpus(repo: str, enabled: bool) -> CheckResult:
    """The skill's step 1: the corpus is the known-good baseline, and its expected
    count is checked in at tests/expectations/count-baseline/.

    Off by default because it is a multi-minute run and this script is meant to be
    run before every cycle; --with-corpus turns it on. A SKIP is reported as a
    SKIP, never folded into the passing count, so nobody reads a green preflight
    as "the baseline reproduced".
    """
    baseline = os.path.join(repo, "tests/expectations/count-baseline/test-count-baseline.json")
    if not enabled:
        return CheckResult(name="corpus-baseline", status="SKIP",
                           summary="not run (multi-minute); pass --with-corpus to run it",
                           command="tools/preflight.py --with-corpus",
                           detail=[f"expected counts live in {os.path.relpath(baseline, repo)}",
                                   "A box that cannot reproduce the corpus baseline produces "
                                   "results that cannot be trusted - notably a shared cache "
                                   "left inconsistent by a killed run, which once cost 76% of "
                                   "passing tests with no error and an unchanged exit code."],
                           remedy="Run it at least once on a fresh or drifted box, against the "
                                  "SHARED cache the work will actually use - a private cache is "
                                  "blind to exactly the failure this catches.")
    if not os.path.exists(baseline):
        return CheckResult(name="corpus-baseline", status="FAIL",
                           summary=f"no baseline file at {baseline}",
                           command=f"ls {baseline}",
                           remedy="Check out the repository fully, including tests/expectations.")
    r = run(["dotnet", "run", "--project", "AlRunner", "--", "test",
             "--bundle", "tests/al-language", "--package-cache",
             os.path.expanduser("~/.al-runner/platform-apps")],
            cwd=repo, timeout=3600)
    if r.ok:
        return CheckResult(name="corpus-baseline", status="PASS",
                           summary="the corpus baseline reproduced on this box",
                           command="dotnet run --project AlRunner -- test --bundle "
                                   "tests/al-language --package-cache ~/.al-runner/platform-apps")
    tail = "\n".join((r.out + r.err).strip().splitlines()[-15:])
    return CheckResult(name="corpus-baseline", status="FAIL",
                       summary="the corpus baseline did NOT reproduce - everything downstream "
                               "is untrusted until it does",
                       command="dotnet run --project AlRunner -- test --bundle tests/al-language",
                       detail=[tail],
                       remedy="Stop, notify, and open an issue. Do not start a cycle on a box "
                              "whose baseline does not reproduce.")


# --------------------------------------------------------------------------
# the reaper
# --------------------------------------------------------------------------
def reap(repo: str, rows: list[tuple], dry_run: bool) -> list[str]:
    """Remove worktrees whose PR is MERGED and whose tree is clean.

    Two traps, both hit for real:

    1. `git worktree remove` refuses with "working trees containing submodules
       cannot be moved or removed" once anyone has run `git submodule update
       --init` inside it. The fix is NOT `git submodule deinit`: that rewrites
       submodule.*.url in the SHARED .git/config and would disturb the main
       checkout and every other live worktree. Deleting the submodule directory
       and then --force is what works, and it touches only this worktree.
    2. Never remove a worktree carrying uncommitted work or unpushed commits.
       disposition() has already refused those; this re-checks immediately before
       deleting, because the census may be minutes old by now.
    """
    log: list[str] = []
    for wt, pr, dirty, unpushed, d, exists in rows:
        if not d.reapable:
            continue
        st = run(["git", "-C", wt.path, "status", "--porcelain"], timeout=60)
        if not st.ok or st.out.strip():
            log.append(f"SKIP  {wt.path} - it became dirty since the census")
            continue
        if dry_run:
            log.append(f"WOULD REMOVE  {wt.path}  [{wt.branch}]  {d.reason}")
            continue
        r = run(["git", "-C", repo, "worktree", "remove", wt.path], timeout=120)
        if r.ok:
            log.append(f"REMOVED  {wt.path}  [{wt.branch}]")
            continue
        if "submodule" in (r.err + r.out).lower():
            removed_any = False
            for sub in submodule_paths(wt.path):
                target = os.path.join(wt.path, sub)
                if os.path.isdir(target):
                    shutil.rmtree(target, ignore_errors=True)
                    removed_any = True
            r2 = run(["git", "-C", repo, "worktree", "remove", "--force", wt.path], timeout=120)
            if r2.ok:
                log.append(f"REMOVED  {wt.path}  [{wt.branch}] (after clearing "
                           f"{'submodule directories' if removed_any else 'nothing'}; "
                           f"submodule deinit deliberately NOT used - it edits the shared "
                           f".git/config)")
            else:
                log.append(f"FAILED   {wt.path} - {(r2.err or r2.out).strip()[:200]}")
        else:
            log.append(f"FAILED   {wt.path} - {(r.err or r.out).strip()[:200]}")
    if not log:
        log.append("nothing to reap")
    return log


def submodule_paths(worktree: str) -> list[str]:
    gitmodules = os.path.join(worktree, ".gitmodules")
    if not os.path.exists(gitmodules):
        return []
    r = run(["git", "config", "--file", gitmodules, "--get-regexp", r"^submodule\..*\.path$"])
    if not r.ok:
        return []
    return [line.split(" ", 1)[1].strip() for line in r.out.splitlines() if " " in line]


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------
def default_identity() -> str:
    r = run(["gh", "api", "user", "--jq", ".login"], timeout=20) if shutil.which("gh") \
        else Ran(rc=1, out="", err="")
    login = r.out.strip() if r.ok else ""
    return re.sub(r"[^A-Za-z0-9._-]", "-", login or os.environ.get("USER", "unknown"))


def main(argv: Optional[list[str]] = None) -> int:
    epilog = "exit codes:\n" + "\n".join(f"  {k}  {v}" for k, v in sorted(EXIT_MEANING.items()))
    ap = argparse.ArgumentParser(
        description=__doc__.split("\n\n")[0],
        epilog=epilog, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--json", action="store_true", help="machine-readable output")
    ap.add_argument("--strict", action="store_true",
                    help="exit 2 when a check WARNs, even if nothing failed")
    ap.add_argument("--with-test-data", action="store_true",
                    help="size workers for runs that use --test-data (a larger per-worker "
                         "footprint, so a smaller suggested worker count)")
    ap.add_argument("--with-corpus", action="store_true",
                    help="also run the corpus baseline (minutes, not seconds)")
    ap.add_argument("--reap", action="store_true",
                    help="remove worktrees whose PR is MERGED and whose tree is clean")
    ap.add_argument("--dry-run", action="store_true", help="with --reap, only say what it would do")
    ap.add_argument("--identity", default=None, help="agent identity used for the push probe ref")
    ap.add_argument("--scratch-root", default=None,
                    help="directory to scan for stale scratch (default: the system temp dir)")
    ap.add_argument("--stale-hours", type=float, default=6.0,
                    help="idle hours before a large scratch directory is called stale "
                         "(default: 6)")
    ap.add_argument("--skip-budget-fallback", action="store_true",
                    help="do not fall back to `npx ccusage` when the omarchy usage tool "
                         "is missing (it needs the network)")
    ap.add_argument("--no-sizes", action="store_true",
                    help="skip recursive size measurement (faster, less useful)")
    args = ap.parse_args(argv)

    here = os.path.dirname(os.path.abspath(__file__))
    repo = git_repo_root(here)
    if not repo:
        print("preflight: not inside a git repository", file=sys.stderr)
        return 3
    # The MAIN checkout, not whichever worktree this copy of the script lives in:
    # the worktree census and the reaper both have to see every worktree.
    common = run(["git", "-C", repo, "rev-parse", "--path-format=absolute",
                  "--git-common-dir"]).out.strip()
    if common.endswith("/.git"):
        repo = common[:-len("/.git")]

    import tempfile
    scratch_root = args.scratch_root or tempfile.gettempdir()
    identity = args.identity or default_identity()
    per_worker = (PER_WORKER_BYTES_WITH_TEST_DATA if args.with_test_data
                  else PER_WORKER_BYTES_NO_TEST_DATA)
    per_worker_label = ("with --test-data" if args.with_test_data else "without test data")

    df = run(["df", "-PT", "-B1", scratch_root, repo])
    mounts = parse_df(df.out)
    try:
        with open("/proc/meminfo") as fh:
            mem = parse_meminfo(fh.read())
    except OSError:
        mem = {}

    slug = repo_slug(repo)
    prs, pr_status = pr_map(slug)
    rows = collect_worktrees(repo, prs, measure=not args.no_sizes)

    results = [
        check_space("disk-scratch", scratch_root, mounts, per_worker),
        check_space("disk-repo", repo, mounts, per_worker),
        check_memory(mem, per_worker),
        check_headroom(scratch_root, repo, mounts, mem, per_worker, per_worker_label),
        check_budget(skip_fallback=args.skip_budget_fallback),
        check_worktrees(rows, repo, pr_status),
        check_stale_scratch(scratch_root, rows, args.stale_hours),
        check_push(repo, identity),
        check_commit(repo),
        check_github(slug),
        check_corpus(repo, args.with_corpus),
    ]
    results.sort(key=lambda r: _ORDER.get(r.status, 9))

    reap_log: list[str] = []
    if args.reap:
        reap_log = reap(repo, rows, args.dry_run)

    if args.json:
        extra = {"repo": repo, "identity": identity, "scratch_root": scratch_root,
                 "per_worker_bytes": per_worker}
        if args.reap:
            extra["reap"] = reap_log
        print(json.dumps(build_json(results, strict=args.strict, extra=extra), indent=2))
    else:
        now = dt.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        header = (f"AL Runner preflight - is this box fit to run an autonomous cycle?\n"
                  f"{now}   repo {repo}   identity {identity}   scratch {scratch_root}")
        print(render_report(results, strict=args.strict, header=header))
        if args.reap:
            print()
            print("--reap:" + (" (dry run)" if args.dry_run else ""))
            for line in reap_log:
                print(f"  {line}")
    return overall_exit(results, args.strict)


if __name__ == "__main__":
    sys.exit(main())
