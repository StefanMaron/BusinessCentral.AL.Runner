#!/usr/bin/env python3
"""Unit tests for tools/preflight.py's parsing, threshold and disposition logic.

preflight.py decides whether a box is fit to run an autonomous cycle, so its
verdicts are proven here against *captured* `df` / `/proc/meminfo` /
`git worktree list --porcelain` output rather than against whatever the machine
running the tests happens to look like. A threshold suite that reads the live box
passes for the wrong reason on a healthy one and cannot be made to fail on demand.

The one test that does touch a real git repository builds its own throwaway one:
`test_squash_merge_trap` squash-merges a branch and proves that
`git merge-base --is-ancestor` -- the merged-test a reaper reaches for first --
reports the merged branch as unmerged. That has to be real git, because the whole
point is that the trap is invisible in a hand-written fixture.

Run: python3 tools/test_preflight.py
"""
from __future__ import annotations

import datetime as dt
import importlib.util
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import scratch_git_config  # noqa: E402
scratch_git_config.isolate()  # scratch commits must not reach the user's signer (#4001)
_spec = importlib.util.spec_from_file_location("preflight", os.path.join(HERE, "preflight.py"))
pf = importlib.util.module_from_spec(_spec)
# Registered before exec: @dataclass resolves its own module through
# sys.modules[cls.__module__], and on Python 3.14 an unregistered module makes
# that lookup return None and the decorator raise.
sys.modules["preflight"] = pf
_spec.loader.exec_module(pf)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


GIB = 1024 ** 3

# -------------------------------------------------- shim banner on stdout
# mise prints an activation banner on STDOUT for every command it shims. It does
# not change the exit code, so a capture that keeps it fails later, somewhere
# else, as a wrong answer. This is the real payload that made the github check
# report a healthy account as unable to push.
BANNER = "mise ~/.config/mise/config.toml tools: gh@2.100.0\n"

check("the banner is stripped from a one-line capture",
      pf.strip_shim_banner(BANNER + "StefanMaron\n") == "StefanMaron\n",
      repr(pf.strip_shim_banner(BANNER + "StefanMaron\n")))

perms = '{"admin":true,"push":true,"pull":true}'
check("a JSON body behind the banner parses after stripping",
      json.loads(pf.strip_shim_banner(BANNER + perms))["push"] is True)

check("two banner lines are both dropped",
      pf.strip_shim_banner(BANNER + BANNER + "x\n") == "x\n",
      repr(pf.strip_shim_banner(BANNER + BANNER + "x\n")))

check("output with no banner is returned byte-for-byte",
      pf.strip_shim_banner(perms) == perms)

check("a real line that merely starts with 'mise' is NOT eaten",
      pf.strip_shim_banner("mise is the tool we use\n") == "mise is the tool we use\n",
      repr(pf.strip_shim_banner("mise is the tool we use\n")))

check("a banner appearing after real output is left alone -- only the leader is a banner",
      pf.strip_shim_banner("StefanMaron\n" + BANNER) == "StefanMaron\n" + BANNER)

check("an empty capture stays empty rather than becoming a false answer",
      pf.strip_shim_banner("") == "")

# ---------------------------------------------------------------- df parsing
# Captured verbatim from the box the incident happened on:
#   df -PT -B1 /tmp /home/stefan/Documents/Repos/Comunity/BusinessCentral.AL.Runner
DF_HEALTHY = """\
Filesystem       Type      1-blocks         Used    Available Capacity Mounted on
tmpfs            tmpfs    8222150656   1179975680   7042174976      15% /tmp
/dev/mapper/root btrfs  509943480320  82476822528 426615525376      17% /
"""

# The same box mid-incident: /tmp at 80% before cleanup, and then the state that
# actually broke every shell on the machine (EDQUOT on a full tmpfs).
DF_FULL_TMP = """\
Filesystem       Type      1-blocks         Used    Available Capacity Mounted on
tmpfs            tmpfs    8222150656   8140000000     82150656      99% /tmp
/dev/mapper/root btrfs  509943480320  82476822528 426615525376      17% /
"""

print("preflight.py -- df parsing")

mounts = pf.parse_df(DF_HEALTHY)
by_mp = {m.mountpoint: m for m in mounts}
check("both mounts parsed", set(by_mp) == {"/tmp", "/"}, str(sorted(by_mp)))
check("/tmp is recognised as tmpfs", by_mp["/tmp"].fstype == "tmpfs", by_mp["/tmp"].fstype)
check("/tmp free bytes are exact", by_mp["/tmp"].free == 7042174976, str(by_mp["/tmp"].free))
check("/tmp total bytes are exact", by_mp["/tmp"].total == 8222150656, str(by_mp["/tmp"].total))
check("/tmp used percent is exact", by_mp["/tmp"].used_pct == 15, str(by_mp["/tmp"].used_pct))
check("the repo filesystem is not tmpfs", by_mp["/"].fstype == "btrfs", by_mp["/"].fstype)
check("a device name with a slash does not break the split",
      by_mp["/"].device == "/dev/mapper/root", by_mp["/"].device)

# A df line for a mountpoint containing a space must keep the whole path.
spaced = pf.parse_df(
    "Filesystem Type 1-blocks Used Available Capacity Mounted on\n"
    "tmpfs tmpfs 100 10 90 10% /mnt/my disk\n")
check("a mountpoint containing a space survives parsing",
      spaced[0].mountpoint == "/mnt/my disk", spaced[0].mountpoint)

# ------------------------------------------------------- mount for a path
print()
print("preflight.py -- which filesystem holds a path")
check("/tmp/x resolves to the tmpfs, not to /",
      pf.mount_for_path("/tmp/x/y", mounts).mountpoint == "/tmp")
check("a repo path under /home resolves to /",
      pf.mount_for_path("/home/stefan/repo", mounts).mountpoint == "/")
check("the longest matching mountpoint wins, not the first",
      pf.mount_for_path("/tmp", mounts).mountpoint == "/tmp")
check("a path on no known mount returns None", pf.mount_for_path("relative/path", []) is None)

# ------------------------------------------------------------ disk thresholds
print()
print("preflight.py -- disk thresholds")
PW = pf.PER_WORKER_BYTES_NO_TEST_DATA

check("a 7 GiB-free tmpfs at 15% passes",
      pf.classify_space(by_mp["/tmp"].free, by_mp["/tmp"].used_pct, PW)[0] == "PASS")
check("a 400 GiB-free root at 17% passes",
      pf.classify_space(by_mp["/"].free, by_mp["/"].used_pct, PW)[0] == "PASS")

full = {m.mountpoint: m for m in pf.parse_df(DF_FULL_TMP)}
st, why = pf.classify_space(full["/tmp"].free, full["/tmp"].used_pct, PW)
check("the tmpfs that broke the box FAILs", st == "FAIL", f"{st} {why}")
check("...and the reason names the shortfall, not just a percentage",
      "worker" in why.lower(), why)

# A big disk at 99% still has room for many workers: percentage alone must not
# hard-fail, or every large half-full-of-something disk halts the loop.
st, why = pf.classify_space(free=50 * GIB, used_pct=99, per_worker=PW)
check("a 99%-used disk with 50 GiB free WARNs rather than FAILs", st == "WARN", f"{st} {why}")
check("...and says why the percentage alone is not fatal", "50" in why or "GiB" in why, why)

st, _ = pf.classify_space(free=int(1.5 * PW), used_pct=40, per_worker=PW)
check("room for only one worker WARNs", st == "WARN", st)
st, _ = pf.classify_space(free=int(0.9 * PW), used_pct=40, per_worker=PW)
check("room for less than one worker FAILs", st == "FAIL", st)
st, _ = pf.classify_space(free=int(4 * PW), used_pct=60, per_worker=PW)
check("60% used with room for four workers passes -- 60% is not 99%", st == "PASS", st)

# ---------------------------------------------------------------- meminfo
print()
print("preflight.py -- meminfo parsing")
MEMINFO = """\
MemTotal:       16058888 kB
MemFree:         2573664 kB
MemAvailable:    7364856 kB
SwapTotal:      16058364 kB
SwapFree:       14297236 kB
"""
mem = pf.parse_meminfo(MEMINFO)
check("MemAvailable is converted from kB to bytes",
      mem["MemAvailable"] == 7364856 * 1024, str(mem.get("MemAvailable")))
check("MemTotal is converted from kB to bytes", mem["MemTotal"] == 16058888 * 1024)
check("MemFree is not mistaken for MemAvailable", mem["MemFree"] != mem["MemAvailable"])

# ------------------------------------------------------- derived worker count
print()
print("preflight.py -- derived worker count")
sug = pf.suggest_workers(tmp_free=by_mp["/tmp"].free, repo_free=by_mp["/"].free,
                         mem_available=mem["MemAvailable"], cpus=12, per_worker=PW)
check("the suggestion is derived, not hardcoded", sug.workers == min(
    int(by_mp["/tmp"].free // PW), int(by_mp["/"].free // PW),
    int(mem["MemAvailable"] // PW), 12), str(sug.workers))
check("the smallest resource is named as the limit", sug.limited_by == "/tmp free space",
      sug.limited_by)

# Same box, but with test data: the per-worker cost roughly doubles, so the
# suggestion must fall. A hardcoded number would not move.
sug_td = pf.suggest_workers(tmp_free=by_mp["/tmp"].free, repo_free=by_mp["/"].free,
                            mem_available=mem["MemAvailable"], cpus=12,
                            per_worker=pf.PER_WORKER_BYTES_WITH_TEST_DATA)
check("a bigger per-worker footprint yields fewer workers", sug_td.workers < sug.workers,
      f"{sug_td.workers} vs {sug.workers}")

sug_cpu = pf.suggest_workers(tmp_free=900 * GIB, repo_free=900 * GIB,
                             mem_available=900 * GIB, cpus=4, per_worker=PW)
check("with unlimited disk and RAM the CPU count is the limit",
      sug_cpu.workers == 4 and sug_cpu.limited_by == "CPU count", str(sug_cpu))

sug_none = pf.suggest_workers(tmp_free=full["/tmp"].free, repo_free=by_mp["/"].free,
                              mem_available=mem["MemAvailable"], cpus=12, per_worker=PW)
check("a box with no room for one worker suggests zero", sug_none.workers == 0, str(sug_none))

# The limiting resource is named after the scratch root actually measured. A
# report that says "/tmp" while measuring somewhere else points the reader at the
# wrong filesystem -- the same mistake as not naming the mount at all.
sug_named = pf.suggest_workers(tmp_free=1 * GIB, repo_free=900 * GIB,
                               mem_available=900 * GIB, cpus=64, per_worker=PW,
                               scratch_label="/mnt/agent-scratch")
check("the scratch mount is named, not hardcoded as /tmp",
      sug_named.limited_by == "/mnt/agent-scratch free space", sug_named.limited_by)

# --------------------------------------------------- worktree list parsing
print()
print("preflight.py -- git worktree list parsing")
WORKTREES = """\
worktree /home/stefan/repo
HEAD 1a60f505ef03ae56b8172b0563c22fd0b4ab7356
branch refs/heads/main

worktree /home/stefan/repo/.claude/worktrees/stma-auto-1-issue-2858
HEAD c4a6fa86a6c750952af9dd0d61935124dda5a5b4
branch refs/heads/agent/stma-auto-1/issue-2858

worktree /home/stefan/repo/.claude/worktrees/stma-auto-1-issue-2907
HEAD 55a16035000000000000000000000000000000aa
branch refs/heads/agent/stma-auto-1/issue-2907

worktree /tmp/scratch/rev-a
HEAD ec02626700000000000000000000000000000bbb
detached

"""
wts = pf.parse_worktree_porcelain(WORKTREES)
check("every worktree is parsed", len(wts) == 4, str(len(wts)))
check("the first entry is the main checkout", wts[0].is_main and wts[0].branch == "main")
check("agent worktrees are not main", not any(w.is_main for w in wts[1:]))
check("a detached worktree has no branch", wts[3].branch is None and wts[3].detached)
check("branch refs are shortened",
      wts[1].branch == "agent/stma-auto-1/issue-2858", str(wts[1].branch))
check("HEAD sha is captured", wts[1].head == "c4a6fa86a6c750952af9dd0d61935124dda5a5b4")

# ------------------------------------------------- worktree disposition
print()
print("preflight.py -- worktree disposition (what may be reaped)")


def wt(path="/w/x", branch="agent/a/issue-1", head="a" * 40, is_main=False):
    return pf.Worktree(path=path, head=head, branch=branch, detached=False, is_main=is_main)


MERGED = {"number": 2859, "state": "MERGED", "headRefOid": "a" * 40}
OPEN = {"number": 2860, "state": "OPEN", "headRefOid": "a" * 40}
CLOSED = {"number": 2861, "state": "CLOSED", "headRefOid": "a" * 40}

d = pf.disposition(wt(), pr=MERGED, dirty=False, unpushed=0)
check("merged PR + clean tree is reapable", d.reapable, d.reason)
check("...and the reason names the PR", "2859" in d.reason, d.reason)

d = pf.disposition(wt(), pr=OPEN, dirty=False, unpushed=0)
check("an open PR is never reaped", not d.reapable, d.reason)

d = pf.disposition(wt(), pr=CLOSED, dirty=False, unpushed=0)
check("a closed-unmerged PR is never reaped", not d.reapable, d.reason)

d = pf.disposition(wt(), pr=None, dirty=False, unpushed=0)
check("no PR at all is never reaped", not d.reapable, d.reason)
check("...and says the PR is unknown rather than implying it is unmerged",
      "no pull request" in d.reason.lower() or "unknown" in d.reason.lower(), d.reason)

d = pf.disposition(wt(), pr=MERGED, dirty=True, unpushed=0)
check("uncommitted work blocks the reap even on a merged PR", not d.reapable, d.reason)
check("...and the reason says uncommitted", "uncommitted" in d.reason.lower(), d.reason)

d = pf.disposition(wt(), pr=MERGED, dirty=False, unpushed=3)
check("unpushed commits block the reap even on a merged PR", not d.reapable, d.reason)
check("...and the reason counts them", "3" in d.reason, d.reason)

d = pf.disposition(wt(is_main=True, branch="main"), pr=MERGED, dirty=False, unpushed=0)
check("the main checkout is never reapable", not d.reapable, d.reason)

d = pf.disposition(wt(), pr=MERGED, dirty=False, unpushed=0, is_current=True)
check("the worktree you are standing in is never reapable", not d.reapable, d.reason)

d = pf.disposition(pf.Worktree(path="/w/d", head="b" * 40, branch=None, detached=True,
                               is_main=False), pr=None, dirty=False, unpushed=0)
check("a detached worktree has no PR to consult and is kept", not d.reapable, d.reason)

# --------------------------------------------- the squash-merge trap, for real
print()
print("preflight.py -- the squash-merge trap (real git)")


def git(*args, cwd, **kw):
    return subprocess.run(["git", *args], cwd=cwd, check=True,
                          capture_output=True, text=True, **kw)


tmp = tempfile.mkdtemp(prefix="preflight-squash-")
try:
    env = dict(os.environ,
               GIT_AUTHOR_NAME="t", GIT_AUTHOR_EMAIL="t@e",
               GIT_COMMITTER_NAME="t", GIT_COMMITTER_EMAIL="t@e",
               GIT_CONFIG_GLOBAL=os.path.join(tmp, "nogitconfig"),
               GIT_CONFIG_SYSTEM=os.path.join(tmp, "nogitconfig"))
    repo = os.path.join(tmp, "r")
    os.makedirs(repo)
    git("init", "-q", "-b", "main", cwd=repo, env=env)
    open(os.path.join(repo, "a.txt"), "w").write("one\n")
    git("add", "a.txt", cwd=repo, env=env)
    git("commit", "-qm", "base", cwd=repo, env=env)

    git("checkout", "-qb", "feature", cwd=repo, env=env)
    open(os.path.join(repo, "b.txt"), "w").write("two\n")
    git("add", "b.txt", cwd=repo, env=env)
    git("commit", "-qm", "feature work", cwd=repo, env=env)
    feature_head = git("rev-parse", "feature", cwd=repo, env=env).stdout.strip()

    # Squash-merge, exactly as this repository is configured to merge:
    #   squash_merge_commit_title=COMMIT_OR_PR_TITLE
    #   squash_merge_commit_message=COMMIT_MESSAGES
    git("checkout", "-q", "main", cwd=repo, env=env)
    git("merge", "--squash", "feature", cwd=repo, env=env)
    git("commit", "-qm", "feature work (#1)", cwd=repo, env=env)

    ancestor = subprocess.run(["git", "merge-base", "--is-ancestor", feature_head, "main"],
                              cwd=repo, env=env, capture_output=True)
    check("git merge-base --is-ancestor reports a SQUASH-MERGED branch as unmerged",
          ancestor.returncode != 0,
          "the trap did not reproduce -- if git changed, the disposition test below is moot")

    content = open(os.path.join(repo, "b.txt")).read()
    check("...even though its content is demonstrably on main", content == "two\n", content)

    d = pf.disposition(wt(branch="feature", head=feature_head),
                       pr={"number": 1, "state": "MERGED", "headRefOid": feature_head},
                       dirty=False, unpushed=0)
    check("disposition() calls it merged anyway, because it asks the PR", d.reapable, d.reason)

    # And the mirror: the PR is the authority in BOTH directions. A branch whose
    # head IS an ancestor of main but whose PR is still open must not be reaped.
    d = pf.disposition(wt(branch="main-ish", head=feature_head),
                       pr={"number": 2, "state": "OPEN", "headRefOid": feature_head},
                       dirty=False, unpushed=0)
    check("an open PR is kept even when ancestry would say merged", not d.reapable, d.reason)
finally:
    shutil.rmtree(tmp, ignore_errors=True)

# ------------------------------------------------------------ unpushed logic
print()
print("preflight.py -- unpushed detection without a remote-tracking branch")
# After a squash merge GitHub deletes the head branch, so `git log @{u}..HEAD`
# has no upstream to resolve. The PR's headRefOid is what still records what was
# pushed.
check("head == the PR's headRefOid means nothing is unpushed",
      pf.unpushed_against_pr("a" * 40, {"headRefOid": "a" * 40}) == 0)
check("a local head the PR never saw counts as unpushed",
      pf.unpushed_against_pr("b" * 40, {"headRefOid": "a" * 40}) == 1)
check("no PR record cannot prove anything was pushed",
      pf.unpushed_against_pr("b" * 40, None) is None)

# ------------------------------------------------------------------- budget
print()
print("preflight.py -- budget headroom")

# Captured from `omarchy-agent-usage-claude --limits-only` on this box, trimmed
# to the fields the check reads. `percent` is a FRACTION in 0..1: the producer's
# normalize_utilization() divides by 100 when the upstream payload is
# percent-scaled and clamps to 1.0. Measured confirmation: the session window
# read 0.43 and then 0.48 twenty minutes later under 9-12 concurrent agents,
# i.e. ~15 percentage points/hour.
OMARCHY = """{"limits":[
  {"label":"Session (5-hour)","percent":0.48,"resetsAt":"2026-09-05T21:30:00.037631+00:00"},
  {"label":"Weekly (7-day)","percent":0.17,"resetsAt":"2026-09-07T19:00:00.037651+00:00"},
  {"label":"Fable Weekly","percent":0.0,"resetsAt":""}],
  "tierLabel":"Max 20x","todayTotalTokens":813800459,"todayPrompts":4998,"todaySessions":3,
  "updatedAt":"2026-09-05T20:27:46Z"}"""

NOW = pf._iso("2026-09-05T20:30:00+00:00")
windows, meta = pf.parse_omarchy_limits(OMARCHY)
by_label = {w.label: w for w in windows}
check("every limit window is parsed", len(windows) == 3, str(len(windows)))
check("0.48 is read as 48% used, not 0.48% and not 4800%",
      abs(by_label["Session (5-hour)"].fraction - 0.48) < 1e-9,
      str(by_label["Session (5-hour)"].fraction))
check("the reset time is parsed as an aware datetime",
      by_label["Session (5-hour)"].resets_at is not None
      and by_label["Session (5-hour)"].resets_at.tzinfo is not None)
check("minutes-to-reset is computed, not just the percentage",
      abs(by_label["Session (5-hour)"].minutes_left(NOW) - 60.0) < 1.0,
      str(by_label["Session (5-hour)"].minutes_left(NOW)))
check("an empty resetsAt yields no reset time rather than a crash",
      by_label["Fable Weekly"].resets_at is None)
check("the plan tier is carried through for the box profile",
      meta.get("tierLabel") == "Max 20x", str(meta))

# If the producer ever switches to emitting 48.0 for 48%, that must not render
# as 4800% used and trip the warning.
alt, _ = pf.parse_omarchy_limits('{"limits":[{"label":"S","percent":48.0,"resetsAt":""}]}')
check("a value above 1.0 is read as an already-scaled percentage",
      abs(alt[0].fraction - 0.48) < 1e-9, str(alt[0].fraction))

st, summary, detail = pf.classify_budget(windows, NOW)
check("48% used with an hour left is not a failure", st == "PASS", f"{st} {summary}")
check("budget is never a HARD fail -- an exhausted budget yields no answers, "
      "not wrong ones", st != "FAIL", st)
check("the report gives wall-clock, not only a percentage",
      any("resets in" in d for d in detail), str(detail))
check("...and derives a sustainable burn rate from the two together",
      any("%/hour" in d for d in detail), str(detail))

# The derivation has to move with the clock: the same 48% is a different
# situation with four hours left than with one.
LATER = pf._iso("2026-09-05T21:15:00+00:00")
_, _, d_soon = pf.classify_budget([by_label["Session (5-hour)"]], LATER)
_, _, d_far = pf.classify_budget([by_label["Session (5-hour)"]], NOW)


def burn(lines):
    return float(lines[0].split("sustainable from here: ")[1].split("%/hour")[0])


check("less wall-clock left means a higher sustainable burn is required",
      burn(d_soon) > burn(d_far), f"{burn(d_soon)} vs {burn(d_far)}")

hot = [pf.BudgetWindow("Session (5-hour)", 0.93, NOW + dt.timedelta(minutes=90))]
st, summary, detail = pf.classify_budget(hot, NOW)
check("a nearly-consumed window WARNs", st == "WARN", f"{st} {summary}")
check("...and names the window in the summary", "Session" in summary, summary)

st, summary, _ = pf.classify_budget([], NOW)
check("no usage source is UNKNOWN, and UNKNOWN is not PASS", st == "WARN", st)
check("...and says so rather than implying there is no constraint",
      "UNKNOWN" in summary, summary)

# --- ccusage fallback. Captured from a real `npx ccusage@latest blocks --json`.
# Note there is no percentage field anywhere in the JSON: the misleading `%`
# column exists only in the table rendering, so parsing --json cannot repeat the
# mistake of reading it as the plan cap.
CCUSAGE = """{"blocks":[
 {"id":"a","isActive":false,"isGap":false,"totalTokens":349104370,"costUSD":227.018,
  "startTime":"2026-09-05T11:00:00.000Z","endTime":"2026-09-05T16:00:00.000Z",
  "projection":null,"burnRate":null},
 {"id":"b","isActive":true,"isGap":false,"totalTokens":513488037,"costUSD":332.137,
  "startTime":"2026-09-05T16:00:00.000Z","endTime":"2026-09-05T21:00:00.000Z",
  "projection":{"remainingMinutes":28,"totalCost":366.4,"totalTokens":566459665},
  "burnRate":{"costPerHour":73.42,"tokensPerMinute":1891847.4}}]}"""

block = pf.parse_ccusage_blocks(CCUSAGE)
check("the ACTIVE block is selected, not the first", block["totalTokens"] == 513488037, str(block))
check("absolute tokens are read", block["totalTokens"] == 513488037)
check("cost is read", abs(block["costUSD"] - 332.137) < 1e-6)
check("remaining minutes come from the projection", block["remainingMinutes"] == 28)
check("no percentage is extracted from ccusage at all -- its %% column is a guess",
      not any("percent" in k.lower() or k == "%" for k in block), str(list(block)))

check("a gap block is never treated as the active block",
      pf.parse_ccusage_blocks('{"blocks":[{"isActive":true,"isGap":true}]}') is None)
check("no active block returns None rather than a fabricated zero",
      pf.parse_ccusage_blocks('{"blocks":[{"isActive":false}]}') is None)
check("unparseable ccusage output returns None", pf.parse_ccusage_blocks("not json") is None)
check("unparseable omarchy output yields no windows, not a fake zero-usage one",
      pf.parse_omarchy_limits("not json") == ([], {}))

# ------------------------------------------------- the census a person reads
print()
print("preflight.py -- the worktree census")


def row(path, branch, pr, dirty=False, unpushed=0, size=100 * 1024 * 1024,
        is_main=False, exists=True, is_current=False):
    w = pf.Worktree(path=path, head="a" * 40, branch=branch, detached=branch is None,
                    is_main=is_main, size_bytes=size)
    d = pf.disposition(w, pr=pr, dirty=dirty, unpushed=unpushed, is_current=is_current)
    return (w, pr, dirty, unpushed, d, exists)


rows = [
    row("/r", "main", None, is_main=True),
    row("/r/.claude/worktrees/a", "agent/x/issue-1", MERGED),
    row("/r/.claude/worktrees/b", "agent/x/issue-2", OPEN, dirty=True),
    row("/r/.claude/worktrees/c", "agent/x/issue-3", MERGED, unpushed=2),
]
r = pf.check_worktrees(rows, "/r", "ok")
body = "\n".join([r.summary] + r.detail + [r.remedy])
check("the main checkout is excluded from the agent count", "3 agent worktree" in r.summary,
      r.summary)
check("a reapable worktree is counted", "1 reapable" in r.summary, r.summary)
check("uncommitted work is counted in the summary", "1 with uncommitted work" in r.summary,
      r.summary)
check("the reapable one is marked REAP", "REAP" in body, body)
# A worktree kept for an unrelated reason (its PR is open) must still show that
# it holds unsaved work: disposition() stops at the first reason it finds, and
# unsaved work is what a person most needs out of this census.
check("uncommitted work is visible even when the worktree is kept for another reason",
      "UNCOMMITTED CHANGES" in body, body)
check("unpushed commits are visible on their own line", "UNPUSHED COMMIT" in body, body)
check("having something to reap is a WARN, not silence", r.status == "WARN", r.status)
check("...and the remedy names the reaper", "--reap" in r.remedy, r.remedy)
check("...and cites why nothing ever deleting these matters", "82 worktrees" in r.remedy,
      r.remedy)

r = pf.check_worktrees(rows, "/r", "gh pr list failed: boom")
check("unreadable PR states make the census WARN rather than silently reap",
      r.status == "WARN", r.status)
check("...and say the merged state is unknown",
      any("could not be read" in d for d in r.detail), str(r.detail))

r = pf.check_worktrees([row("/r", "main", None, is_main=True),
                        row("/r/.claude/worktrees/b", "agent/x/issue-2", OPEN)], "/r", "ok")
check("a tidy box with only live worktrees passes", r.status == "PASS", r.status)

# ------------------------------------------------------ the reaper (real git)
print()
print("preflight.py -- the reaper (real git, including the submodule refusal)")

tmp = tempfile.mkdtemp(prefix="preflight-reap-")
try:
    env = dict(os.environ,
               GIT_AUTHOR_NAME="t", GIT_AUTHOR_EMAIL="t@e",
               GIT_COMMITTER_NAME="t", GIT_COMMITTER_EMAIL="t@e",
               GIT_CONFIG_GLOBAL=os.path.join(tmp, "nogitconfig"),
               GIT_CONFIG_SYSTEM=os.path.join(tmp, "nogitconfig"))

    sub = os.path.join(tmp, "sub")
    os.makedirs(sub)
    git("init", "-q", "-b", "master", cwd=sub, env=env)
    open(os.path.join(sub, "s.txt"), "w").write("sub\n")
    git("add", "s.txt", cwd=sub, env=env)
    git("commit", "-qm", "sub", cwd=sub, env=env)

    repo = os.path.join(tmp, "main")
    os.makedirs(repo)
    git("init", "-q", "-b", "main", cwd=repo, env=env)
    open(os.path.join(repo, "a.txt"), "w").write("one\n")
    git("add", "a.txt", cwd=repo, env=env)
    git("commit", "-qm", "base", cwd=repo, env=env)
    # file:// submodules are refused by default since the CVE-2022-39253 fix.
    git("-c", "protocol.file.allow=always", "submodule", "add", "-q", sub, "tests/al-language",
        cwd=repo, env=env)
    git("commit", "-qm", "add submodule", cwd=repo, env=env)

    git("branch", "feature", cwd=repo, env=env)
    wt_path = os.path.join(tmp, "wt")
    git("worktree", "add", "-q", wt_path, "feature", cwd=repo, env=env)
    head = git("rev-parse", "HEAD", cwd=wt_path, env=env).stdout.strip()
    # This is the step that makes the worktree unremovable.
    git("-c", "protocol.file.allow=always", "submodule", "update", "--init", "-q",
        cwd=wt_path, env=env)

    refusal = subprocess.run(["git", "worktree", "remove", wt_path], cwd=repo, env=env,
                             capture_output=True, text=True)
    check("plain `git worktree remove` refuses a worktree with an initialised submodule",
          refusal.returncode != 0 and "submodule" in (refusal.stderr + refusal.stdout).lower(),
          f"rc={refusal.returncode} {refusal.stderr.strip()[:200]}")

    check("submodule_paths reads .gitmodules",
          pf.submodule_paths(wt_path) == ["tests/al-language"], str(pf.submodule_paths(wt_path)))

    w = pf.Worktree(path=wt_path, head=head, branch="feature", detached=False, is_main=False)
    pr = {"number": 7, "state": "MERGED", "headRefOid": head}
    d = pf.disposition(w, pr=pr, dirty=False, unpushed=0)
    rows = [(w, pr, False, 0, d, True)]

    dry = pf.reap(repo, rows, dry_run=True)
    check("--dry-run removes nothing", os.path.isdir(wt_path), "the worktree is gone")
    check("...and says what it would do", any("WOULD REMOVE" in l for l in dry), str(dry))

    # The shared .git/config must be untouched: `git submodule deinit` rewrites
    # submodule.*.url there, which would disturb the main checkout and every
    # other live worktree of the same repository.
    shared_config = open(os.path.join(repo, ".git", "config")).read()

    log = pf.reap(repo, rows, dry_run=False)
    check("the reaper removes it anyway, working around the submodule refusal",
          not os.path.isdir(wt_path), str(log))
    check("...and says so", any("REMOVED" in l for l in log), str(log))
    check("...and records that submodule deinit was deliberately not used",
          any("deinit" in l for l in log), str(log))
    check("the SHARED .git/config is byte-for-byte unchanged",
          open(os.path.join(repo, ".git", "config")).read() == shared_config,
          "the reaper edited config other worktrees share")
    check("the submodule source repository is untouched",
          os.path.exists(os.path.join(sub, "s.txt")))

    # And it must refuse work that appeared since the census was taken.
    wt2 = os.path.join(tmp, "wt2")
    git("branch", "feature2", cwd=repo, env=env)
    git("worktree", "add", "-q", wt2, "feature2", cwd=repo, env=env)
    w2 = pf.Worktree(path=wt2, head=head, branch="feature2", detached=False, is_main=False)
    pr2 = {"number": 8, "state": "MERGED", "headRefOid": head}
    rows2 = [(w2, pr2, False, 0, pf.disposition(w2, pr=pr2, dirty=False, unpushed=0), True)]
    open(os.path.join(wt2, "scratch.txt"), "w").write("work done after the census\n")
    log2 = pf.reap(repo, rows2, dry_run=False)
    check("a worktree that became dirty since the census is NOT removed",
          os.path.isdir(wt2), str(log2))
    check("...and the reason says why", any("became dirty" in l for l in log2), str(log2))

    check("nothing to reap says so rather than printing an empty list",
          pf.reap(repo, [], dry_run=False) == ["nothing to reap"])
finally:
    shutil.rmtree(tmp, ignore_errors=True)


# --------------------------------- the retired corpus gitlink (#3335)
print()
print("preflight.py -- a retired corpus gitlink is not uncommitted work")

# #3737 deleted the tests/al-language submodule. A worktree branched BEFORE that
# still carries the 160000 gitlink in its own HEAD, so its checkout drifts from
# a pin nothing updates any more: `git status` prints ` M tests/al-language`
# forever, and there is no `git submodule update` left to silence it. Measured
# on this box 2026-09-11: 7 of 7 held-back worktrees, every one a MERGED PR,
# every one with that single line and nothing else.

check("the retired corpus gitlink is recognised",
      pf.is_retired_corpus_gitlink(" M tests/al-language", {"tests/al-language": "160000"}),
      "the exact shape that held back 7 merged worktrees")
check("...and so is the staged spelling",
      pf.is_retired_corpus_gitlink("M  tests/al-language", {"tests/al-language": "160000"}))

# The discriminator is the gitlink MODE, not the path. A post-#3737 worktree has
# an ordinary directory there, and real files in it are real work.
check("a tracked FILE under the corpus path is real work, not a gitlink",
      not pf.is_retired_corpus_gitlink(" M tests/al-language/x.al",
                                       {"tests/al-language/x.al": "100644"}))
check("an UNTRACKED file under the corpus path is real work",
      not pf.is_retired_corpus_gitlink("?? tests/al-language/RreLayout.rdl", {}),
      "corpus-stma-auto-32-issue-2943 on this box holds 5 such files")
check("a path the index does not call a gitlink is never discounted",
      not pf.is_retired_corpus_gitlink(" M tests/al-language", {"tests/al-language": "100644"}))
check("a gitlink at some OTHER path is not discounted",
      not pf.is_retired_corpus_gitlink(" M vendor/thing", {"vendor/thing": "160000"}))
check("a DELETED gitlink is not discounted -- that is a real edit",
      not pf.is_retired_corpus_gitlink(" D tests/al-language", {"tests/al-language": "160000"}))

# classify_dirt(): the three-state answer disposition() consumes.
GL = {"tests/al-language": "160000"}
check("a tree dirty ONLY by the retired gitlink is not dirty",
      pf.classify_dirt(" M tests/al-language\n", GL, gitlink_workless=True)
      == (False, "the retired tests/al-language gitlink only"))
check("a tree with the gitlink AND real work stays dirty",
      pf.classify_dirt(" M tests/al-language\n M AlRunner/X.cs\n", GL,
                       gitlink_workless=True)[0])

# The superproject line is AMBIGUOUS: git prints ` M tests/al-language` for a
# moved pin, for untracked files inside the checkout, and for both. Only the
# submodule's own status separates them, so an unproven checkout stays dirt.
check("the gitlink stays dirt until the checkout under it is proven empty",
      pf.classify_dirt(" M tests/al-language\n", GL)[0],
      "gitlink_workless defaults False -- a forgetful caller gets the safe answer")
check("...and says the checkout holds files of its own",
      "holds files of its own" in pf.classify_dirt(" M tests/al-language\n", GL)[1],
      pf.classify_dirt(" M tests/al-language\n", GL)[1])
check("a clean tree is clean", pf.classify_dirt("", {}) == (False, ""))
check("real work alone is dirty", pf.classify_dirt(" M AlRunner/X.cs\n", {})[0])

# guards-need-a-third-state: a line we cannot parse is dirt, never a free pass.
check("an unparseable status line keeps the tree dirty",
      pf.classify_dirt("garbage\n", {})[0],
      "could not classify must not read as safe to delete")
check("...and says it could not classify it",
      "could not" in pf.classify_dirt("garbage\n", {})[1].lower(),
      pf.classify_dirt("garbage\n", {})[1])
check("a rename line is dirt rather than silently discounted",
      pf.classify_dirt("R  a -> b\n", {})[0])


# ------------------------------- the gitlink reap, end to end (real git, #3335)
print()
print("preflight.py -- a merged worktree held back ONLY by the retired gitlink")

# The classifier above is only worth anything if collect_worktrees() and reap()
# both consult it. reap() re-reads status immediately before deleting, so an
# unwired reap() refuses every worktree the census just cleared -- the failure
# that makes the fix look applied while nothing is reaped.

tmp2 = tempfile.mkdtemp(prefix="preflight-gitlink-")
try:
    env = dict(os.environ,
               GIT_AUTHOR_NAME="t", GIT_AUTHOR_EMAIL="t@e",
               GIT_COMMITTER_NAME="t", GIT_COMMITTER_EMAIL="t@e",
               GIT_CONFIG_GLOBAL=os.path.join(tmp2, "nogitconfig"),
               GIT_CONFIG_SYSTEM=os.path.join(tmp2, "nogitconfig"))

    sub2 = os.path.join(tmp2, "corpus")
    os.makedirs(sub2)
    git("init", "-q", "-b", "master", cwd=sub2, env=env)
    open(os.path.join(sub2, "c.txt"), "w").write("one\n")
    git("add", "c.txt", cwd=sub2, env=env)
    git("commit", "-qm", "c1", cwd=sub2, env=env)

    repo2 = os.path.join(tmp2, "main")
    os.makedirs(repo2)
    git("init", "-q", "-b", "main", cwd=repo2, env=env)
    open(os.path.join(repo2, "a.txt"), "w").write("one\n")
    git("add", "a.txt", cwd=repo2, env=env)
    git("commit", "-qm", "base", cwd=repo2, env=env)
    git("-c", "protocol.file.allow=always", "submodule", "add", "-q", sub2,
        "tests/al-language", cwd=repo2, env=env)
    git("commit", "-qm", "pre-#3737: the corpus is a submodule", cwd=repo2, env=env)

    def gitlink_worktree(name, branch):
        """A worktree whose ONLY dirt is the retired gitlink -- the #3335 state."""
        path = os.path.join(tmp2, name)
        git("branch", branch, cwd=repo2, env=env)
        git("worktree", "add", "-q", path, branch, cwd=repo2, env=env)
        git("-c", "protocol.file.allow=always", "submodule", "update", "--init", "-q",
            cwd=path, env=env)
        # Move the checkout off the pin, exactly as a corpus resolve does now.
        open(os.path.join(sub2, "c.txt"), "w").write(f"moved for {name}\n")
        git("commit", "-qam", f"corpus moves on: {name}", cwd=sub2, env=env)
        git("fetch", "-q", "origin", cwd=os.path.join(path, "tests/al-language"), env=env)
        git("checkout", "-q", "FETCH_HEAD",
            cwd=os.path.join(path, "tests/al-language"), env=env)
        return path

    wt3 = gitlink_worktree("wt3", "gitlink-only")
    st3 = subprocess.run(["git", "status", "--porcelain"], cwd=wt3, env=env,
                         capture_output=True, text=True).stdout
    check("the fixture reproduces the #3335 state exactly",
          st3.strip() == "M tests/al-language" or st3.rstrip("\n") == " M tests/al-language",
          repr(st3))

    modes = pf.index_gitlink_modes(wt3)
    check("index_gitlink_modes reads the 160000 mode off a real worktree",
          modes.get("tests/al-language") == "160000", str(modes))
    check("the submodule checkout under it is proven empty",
          pf.corpus_gitlink_is_workless(wt3))
    _cd = pf.classify_dirt(st3, modes, gitlink_workless=True)
    check("...and classify_dirt then calls the tree clean",
          _cd == (False, "the retired tests/al-language gitlink only"), str(_cd))
    check("worktree_dirt agrees", pf.worktree_dirt(wt3)[0] is False, str(pf.worktree_dirt(wt3)))

    head3 = git("rev-parse", "HEAD", cwd=wt3, env=env).stdout.strip()
    prs = {"gitlink-only": {"number": 3266, "state": "MERGED", "headRefOid": head3}}
    rows3 = pf.collect_worktrees(repo2, prs, measure=False)
    mine = [r for r in rows3 if os.path.realpath(r[0].path) == os.path.realpath(wt3)]
    check("collect_worktrees sees the worktree", len(mine) == 1, str([r[0].path for r in rows3]))
    check("collect_worktrees no longer calls it dirty", not mine[0][2], "still dirty")
    check("...so its merged worktree is reapable", mine[0][4].reapable, mine[0][4].reason)

    log3 = pf.reap(repo2, mine, dry_run=False)
    check("reap() actually removes it -- its own re-read agrees with the census",
          not os.path.isdir(wt3), str(log3))
    check("...and says REMOVED", any("REMOVED" in l for l in log3), str(log3))

    # The direction that must NOT change: a real edit still keeps the tree.
    wt4 = gitlink_worktree("wt4", "gitlink-plus-work")
    open(os.path.join(wt4, "a.txt"), "w").write("edited by an agent\n")
    head4 = git("rev-parse", "HEAD", cwd=wt4, env=env).stdout.strip()
    prs4 = {"gitlink-plus-work": {"number": 3267, "state": "MERGED", "headRefOid": head4}}
    rows4 = [r for r in pf.collect_worktrees(repo2, prs4, measure=False)
             if os.path.realpath(r[0].path) == os.path.realpath(wt4)]
    check("a worktree with the gitlink AND a real edit is still dirty", rows4[0][2])
    check("...and is NOT reapable", not rows4[0][4].reapable, rows4[0][4].reason)
    pf.reap(repo2, rows4, dry_run=False)
    check("...and reap() leaves it on disk", os.path.isdir(wt4))
    check("...with the agent's edit intact",
          open(os.path.join(wt4, "a.txt")).read() == "edited by an agent\n")

    # An UNTRACKED file under the corpus path is real work too -- the post-#3737
    # shape, live on this box as corpus-stma-auto-32-issue-2943.
    wt5 = gitlink_worktree("wt5", "gitlink-plus-untracked")
    open(os.path.join(wt5, "tests/al-language", "RreLayout.rdl"), "w").write("layout\n")
    head5 = git("rev-parse", "HEAD", cwd=wt5, env=env).stdout.strip()
    prs5 = {"gitlink-plus-untracked": {"number": 3268, "state": "MERGED",
                                       "headRefOid": head5}}
    st5 = subprocess.run(["git", "status", "--porcelain"], cwd=wt5, env=env,
                         capture_output=True, text=True).stdout
    check("git prints the SAME line for pin drift and for work inside the checkout",
          st5.rstrip("\n").strip() == "M tests/al-language", repr(st5))
    check("...so the superproject line alone cannot decide, and the probe does",
          not pf.corpus_gitlink_is_workless(wt5),
          "the checkout holds RreLayout.rdl")
    check("...and worktree_dirt therefore keeps it dirty", pf.worktree_dirt(wt5)[0],
          str(pf.worktree_dirt(wt5)))
    rows5 = [r for r in pf.collect_worktrees(repo2, prs5, measure=False)
             if os.path.realpath(r[0].path) == os.path.realpath(wt5)]
    check("...and it is not reapable", not rows5[0][4].reapable, rows5[0][4].reason)
    pf.reap(repo2, rows5, dry_run=False)
    check("an untracked file inside the corpus directory keeps the worktree",
          os.path.isdir(wt5) and os.path.exists(
              os.path.join(wt5, "tests/al-language", "RreLayout.rdl")))
finally:
    shutil.rmtree(tmp2, ignore_errors=True)


# ------------------------------------ check_stale_scratch on Windows (#3759)
print()
print("preflight.py -- the scratch scan must not need os.getuid()")

# os.getuid does not exist on Windows, and check_stale_scratch reached it
# unconditionally -- so EVERY check preflight would otherwise report was lost on
# a Windows box, branch-ownership included, which is the one an agent there most
# needs. Reproduced on all three invocations tried in #3759.
_real_getuid = getattr(os, "getuid", None)
try:
    if hasattr(os, "getuid"):
        del os.getuid
    check("os.getuid is absent for this check", not hasattr(os, "getuid"))
    _scratch = tempfile.mkdtemp(prefix="preflight-nouid-")
    try:
        os.makedirs(os.path.join(_scratch, "a-dir"))
        open(os.path.join(_scratch, "a-file"), "w").write("x")
        r = pf.check_stale_scratch(_scratch, [], 24.0)
        check("check_stale_scratch still returns a result without os.getuid",
              isinstance(r, pf.CheckResult), repr(r))
        check("...and it is a real verdict, not an error-shaped WARN",
              r.status in ("PASS", "WARN"), f"{r.status} {r.summary}")
        check("...and it counted the directory rather than skipping everything",
              "1 directory" in " ".join([r.summary] + list(r.detail)),
              " ".join([r.summary] + list(r.detail)))
    finally:
        shutil.rmtree(_scratch, ignore_errors=True)
finally:
    if _real_getuid is not None:
        os.getuid = _real_getuid

check("os.getuid is restored for the rest of the run",
      _real_getuid is None or hasattr(os, "getuid"))

# ------------------------------- a truncated scratch walk (#4519)
print()
print("preflight.py -- an incomplete scratch measurement must not read as a total")

# dir_size() returns (bytes, complete) and its docstring is explicit that
# `complete` exists "so the report can say 'at least' instead of printing a
# number that quietly undercounts". check_stale_scratch summed the bytes and
# dropped the flag, so a walk that ran out of budget was reported as a measured
# total. Measured on the loop box at filing: the summary said 11.7 GiB where du
# said 63.3 GiB -- a 5.4x undercount from ONE truncated row, with no `+`
# anywhere in the summary and none in data["total_bytes"].
#
# The per-row detail already carried the marker, which is why this survived: a
# reader scanning details sees `8.8 GiB+` while the headline above it lies.
_trunc = tempfile.mkdtemp(prefix="preflight-trunc-")
try:
    _big = os.path.join(_trunc, "big")
    os.makedirs(_big)
    for _i in range(60):
        with open(os.path.join(_big, f"f{_i}"), "w") as _fh:
            _fh.write("x" * 100)

    # A budget far below the entry count forces the truncation deterministically;
    # the real check uses 200_000, which this box exceeds with 82k directories.
    r_t = pf.check_stale_scratch(_trunc, [], 24.0, budget=10)
    _all_t = " ".join([r_t.summary] + list(r_t.detail))
    check("a truncated walk says so in the SUMMARY, not only in the per-row detail",
          "+" in r_t.summary or "at least" in r_t.summary.lower(), r_t.summary)
    check("...and the machine-readable total is flagged incomplete too",
          r_t.data.get("total_complete") is False, repr(r_t.data))

    # The control. Without it every assertion above is satisfied by a check that
    # marks EVERY total incomplete, which is the same defect pointing the other
    # way and would make the marker meaningless.
    r_c = pf.check_stale_scratch(_trunc, [], 24.0, budget=10_000)
    check("a COMPLETE walk does not claim to be truncated",
          "+" not in r_c.summary and "at least" not in r_c.summary.lower(), r_c.summary)
    check("...and reports its total as complete",
          r_c.data.get("total_complete") is True, repr(r_c.data))
    # The two must also disagree on the number itself, or the fixture is not
    # actually exercising truncation and both assertions above are vacuous.
    check("...and the truncated total really is smaller, so the fixture truncates",
          r_t.data["total_bytes"] < r_c.data["total_bytes"],
          f"truncated={r_t.data['total_bytes']} complete={r_c.data['total_bytes']}")
finally:
    shutil.rmtree(_trunc, ignore_errors=True)

# On a POSIX box the ownership filter must still work: a directory owned by
# somebody else is not this user's scratch and is not counted.
if hasattr(os, "getuid"):
    _scratch2 = tempfile.mkdtemp(prefix="preflight-uid-")
    try:
        os.makedirs(os.path.join(_scratch2, "mine"))
        r2 = pf.check_stale_scratch(_scratch2, [], 24.0)
        check("a directory this user owns is still counted on POSIX",
              "1 directory" in " ".join([r2.summary] + list(r2.detail)),
              " ".join([r2.summary] + list(r2.detail)))
    finally:
        shutil.rmtree(_scratch2, ignore_errors=True)


# ---------------------------------------------------------------- exit codes
print()
print("preflight.py -- exit codes")


def res(status, name="x"):
    return pf.CheckResult(name=name, status=status, summary="s", command="c")


check("all PASS exits 0", pf.overall_exit([res("PASS"), res("PASS")]) == 0)
check("a SKIP does not fail the run", pf.overall_exit([res("PASS"), res("SKIP")]) == 0)
check("a WARN alone exits 0 by default", pf.overall_exit([res("PASS"), res("WARN")]) == 0)
check("a WARN alone exits 2 under --strict",
      pf.overall_exit([res("PASS"), res("WARN")], strict=True) == 2)
check("any FAIL exits 1", pf.overall_exit([res("PASS"), res("WARN"), res("FAIL")]) == 1)
check("FAIL outranks WARN under --strict too",
      pf.overall_exit([res("WARN"), res("FAIL")], strict=True) == 1)
check("an empty result set is not silently green", pf.overall_exit([]) == 3)

# ---------------------------------------------------------------- the report
print()
print("preflight.py -- the report a person reads")
disk = pf.CheckResult(
    name="disk-tmp", status="PASS",
    summary="/tmp: 6.6 GiB free of 7.7 GiB (15% used), fstype tmpfs",
    command="df -PT -B1 /tmp",
    detail=["/tmp is a tmpfs: it is RAM, and it is NOT the filesystem holding the repo."])
text = pf.render_report([disk], strict=False)
check("the report names the mount point", "/tmp" in text, text)
check("the report names the filesystem type -- the fact the incident turned on",
      "tmpfs" in text, text)
check("the report shows the command that produced the number", "df -PT" in text, text)

failing = pf.CheckResult(name="push", status="FAIL", summary="push was refused",
                         command="git push --dry-run origin HEAD:refs/heads/probe",
                         remedy="Re-authenticate: gh auth login, or fix the credential helper.")
text = pf.render_report([failing], strict=False)
check("a FAIL is rendered as FAIL", "FAIL" in text)
check("a FAIL tells the reader what to do", "gh auth login" in text, text)
check("the summary of a failure survives into the report", "refused" in text, text)

check("every non-PASS result carries a remedy in the checks preflight builds",
      all(r.remedy for r in [failing]), "")

j = pf.build_json([disk, failing], strict=False)
check("--json reports the exit code it would return", j["exit_code"] == 1, str(j))
check("--json lists every check by name",
      [c["name"] for c in j["checks"]] == ["disk-tmp", "push"], str(j))
check("--json keeps the machine-readable status", j["checks"][1]["status"] == "FAIL")
check("--json documents what the exit code means",
      "exit_code_meaning" in j and j["exit_code_meaning"], str(j.get("exit_code_meaning")))


# -------------------------------------------------- code-navigation tooling (#3087)
# These three checks exist because each tool fails in a way that produces a
# confidently wrong answer rather than an error, so the tests below are mostly the
# NEGATIVE direction: a deliberately broken state must produce a non-PASS with a
# remedy. A check that only passes when everything is fine has not been tested.
#
# The verdicts are pure functions of a state dataclass, so every broken state is
# constructed here rather than arranged on the box -- the same reason the disk and
# memory thresholds are proven against captured `df` output.

REPO_ROOT = os.path.dirname(HERE)
# The expected decompiler contexts come from .github/bc-versions.txt, read from this
# checkout (#3264). Read once so every healthy state below uses the same set.
_EXPECTED_ALIASES, _EXPECTED_ERR = pf.read_expected_decompiler_aliases(REPO_ROOT)


def nav_states():
    """A healthy state for each of the three checks, to mutate one field at a time."""
    lsp = pf.LspState(script=True, server_on_path=True, fixture_ok=True, rc=0,
                      out=f"{pf.LSP_PROBE_FILE}:79:21  {pf.LSP_PROBE_SYMBOL}\n")
    graph = pf.GraphifyState(binary="/usr/bin/graphify", graph="/repo/AlRunner/graphify-out/graph.json",
                             graph_mtime=2000.0, newest_source=1000.0,
                             newest_source_path="/repo/AlRunner/X.cs",
                             query_rc=0, query_out=f"NODE {pf.LSP_PROBE_SYMBOL} [src=...]")
    # Healthy = nothing stray, nothing rebuilt. probe_graphify repairs both before
    # the classifier ever sees them, so a healthy state carries no trace of either.
    dec = pf.DecompilerState(config="/repo/.mcp.json", registered=True,
                             target="/opt/DecompilerServer.dll", target_exists=True,
                             probe_rc=0, aliases=list(_EXPECTED_ALIASES),
                             expected=list(_EXPECTED_ALIASES))
    return lsp, graph, dec


_lsp, _graph, _dec = nav_states()

# ---- the healthy direction, so the broken ones below mean something
check("nav-lsp passes when the server returns the known-good answer",
      pf.classify_lsp(_lsp).status == "PASS", pf.classify_lsp(_lsp).summary)
check("nav-graphify passes on a current graph that resolves the probe node",
      pf.classify_graphify(_graph).status == "PASS", pf.classify_graphify(_graph).summary)
check("nav-bc-decompiler passes when the server answers with every BC context",
      pf.classify_decompiler(_dec).status == "PASS", pf.classify_decompiler(_dec).summary)

# ---- nav-lsp: the ways a language server lies
import dataclasses as _dc


def lsp_with(**kw):
    return pf.classify_lsp(_dc.replace(_lsp, **kw))


r = lsp_with(rc=2, out="csharp-ls: not found")
check("exit 2 (server never answered) is not reported as PASS", r.status == "WARN", r.summary)
check("exit 2 is spelled out as NOT a negative result",
      "NOT a negative" in " ".join(r.detail), str(r.detail))

r = lsp_with(rc=1, out="")
check("a real not-found for a symbol this checkout DOES declare is reported",
      r.status == "WARN", r.summary)

# The one that matters most: the server answered, exit 0, and the answer is wrong.
# Without a known-good expected answer this state is indistinguishable from health.
r = lsp_with(rc=0, out="SomeOther/File.cs:1:1  Unrelated\n")
check("exit 0 with the WRONG answer is not reported as PASS", r.status == "WARN", r.summary)
check("the wrong-answer case names the answer it expected",
      pf.LSP_PROBE_FILE in " ".join(r.detail), str(r.detail))

r = lsp_with(fixture_ok=False)
check("a drifted probe fixture blames the probe, not the server", r.status == "WARN", r.summary)
check("a drifted probe fixture says the server was not tested",
      "NOT tested" in " ".join(r.detail), str(r.detail))
check("a drifted probe fixture says which constants to update",
      "LSP_PROBE_SYMBOL" in r.remedy, r.remedy)

r = lsp_with(server_on_path=False)
check("a missing csharp-ls is reported with the install command",
      r.status == "WARN" and "mise use -g dotnet:csharp-ls" in r.remedy, r.remedy)

r = lsp_with(timed_out=True, rc=124)
check("a language server that never answers is reported as a timeout", r.status == "WARN", r.summary)

r = pf.classify_lsp(pf.LspState(script=False))
check("a checkout without tools/lsp-query.py is reported", r.status == "WARN", r.summary)

# ---- nav-graphify: the stray copy FAILs, staleness is repaired rather than reported
def graph_with(**kw):
    return pf.classify_graphify(_dc.replace(_graph, **kw))


# The stray copy is the one navigation condition that can halt a cycle, because it
# is a tool answering WRONG rather than a tool being absent, and no rebuild fixes it.
r = graph_with(stray_kept=["/repo/graphify-out"], stray_error="Permission denied")
check("a stray graph that could NOT be removed is a FAIL", r.status == "FAIL", r.summary)
check("the un-removable stray is named so it can be deleted by hand",
      "/repo/graphify-out" in " ".join(r.detail), str(r.detail))
check("the un-removable stray reports why removal failed",
      "Permission denied" in " ".join(r.detail), str(r.detail))
check("the un-removable stray explains the false negative it causes",
      "No matching nodes found" in " ".join(r.detail), str(r.detail))
check("a FAILing stray halts the cycle", pf.overall_exit([r], strict=False) == 1)

# Removed: the box is repaired, so it must not halt -- but it must not read as a
# clean PASS either, or nobody learns the root copy keeps coming back.
r = graph_with(stray_removed=["/repo/graphify-out"])
check("a stray graph preflight REMOVED does not halt the cycle",
      r.status == "WARN" and pf.overall_exit([r], strict=False) == 0, r.status)
check("the removed stray is named in the report",
      "/repo/graphify-out" in " ".join(r.detail), str(r.detail))

# Staleness: no longer a verdict at all. probe_graphify rebuilds; this reports it.
r = graph_with(rebuilt=True, rebuild_rc=0, rebuild_secs=1.9)
check("a graph preflight rebuilt is a PASS, not a staleness warning",
      r.status == "PASS", r.summary)
check("the PASS says it rebuilt rather than hiding the repair",
      "rebuilt" in r.summary, r.summary)
check("the rebuild duration is reported",
      "1.9s" in " ".join(r.detail), str(r.detail))
check("no verdict mentions the word stale any more - staleness is fixed, not classified",
      "stale" not in (r.summary + " ".join(r.detail)).lower(), r.summary + str(r.detail))

r = graph_with(rebuilt=True, rebuild_rc=1, rebuild_out="graphify: boom")
check("a rebuild that FAILED is reported", r.status == "WARN", r.summary)
check("the failed rebuild shows graphify's output",
      "boom" in " ".join(r.detail), str(r.detail))

r = graph_with(graph=None, graph_mtime=None)
check("no graph, and preflight could not build one, is reported",
      r.status == "WARN" and "graphify update ." in r.remedy, r.remedy)

r = graph_with(query_rc=1, query_out="boom")
check("a graph that exists but cannot be queried is reported", r.status == "WARN", r.summary)

r = graph_with(query_out="NODE SomethingElse [src=...]")
check("a query that answers WITHOUT the known-good node is not a PASS",
      r.status == "WARN", r.summary)

r = graph_with(binary=None)
check("graphify missing from PATH is reported", r.status == "WARN", r.summary)

check("the passing graphify report warns about English-question queries",
      "English question" in " ".join(pf.classify_graphify(_graph).detail),
      str(pf.classify_graphify(_graph).detail))

# stray_graph_dirs: the root copy is a stray, AlRunner/'s own is never one.
_g = tempfile.mkdtemp(prefix="preflight-stray-")
os.makedirs(os.path.join(_g, "AlRunner", "graphify-out"), exist_ok=True)
check("the canonical graph under AlRunner/ is not treated as a stray",
      pf.stray_graph_dirs(_g) == [], str(pf.stray_graph_dirs(_g)))
os.makedirs(os.path.join(_g, "graphify-out"), exist_ok=True)
check("a graphify-out at the repository root IS a stray",
      pf.stray_graph_dirs(_g) == [os.path.join(_g, "graphify-out")],
      str(pf.stray_graph_dirs(_g)))
shutil.rmtree(_g, ignore_errors=True)

# probe_graphify REMOVES a stray rather than reporting it, and only reports the ones
# it could not remove. Both halves against a real directory, because "did it actually
# delete the thing" is not a claim a constructed state can make.
_g = tempfile.mkdtemp(prefix="preflight-stray-rm-")
os.makedirs(os.path.join(_g, "AlRunner"))
os.makedirs(os.path.join(_g, "graphify-out"))
with open(os.path.join(_g, "graphify-out", "graph.json"), "w") as fh:
    fh.write("{}")
_st = pf.probe_graphify(_g, timeout=10)
check("probe_graphify deletes a stray graph directory rather than reporting it",
      not os.path.exists(os.path.join(_g, "graphify-out")), str(_st.stray_removed))
check("the deletion is reported, not silent",
      _st.stray_removed == [os.path.join(_g, "graphify-out")], str(_st.stray_removed))
shutil.rmtree(_g, ignore_errors=True)

# The FAIL path has to be reachable, not merely classifiable: a directory whose parent
# denies unlink. Skipped as root, which ignores the permission bits.
#
# The FAIL needs BOTH halves of its precondition, and the test has to build both. An
# un-deletable stray is one half; `graphify` being installed is the other, because a
# stray is only dangerous while something can query it, so classify_graphify answers
# "not on PATH" first on a machine without the binary. An earlier version of this block
# built the stray and read the binary off the machine running the tests, which passes on
# a developer box and fails on a CI runner -- red on three unrelated PRs at once (#3140).
# `graphify` is never executed here: probe_graphify guards both of its `run` calls on
# `not st.stray_kept`, so a kept stray means no subprocess, and the path only has to
# exist as a string.
if os.geteuid() != 0:
    import stat as _stat
    _g = tempfile.mkdtemp(prefix="preflight-stray-keep-")
    os.makedirs(os.path.join(_g, "AlRunner"))
    os.makedirs(os.path.join(_g, "graphify-out"))
    with open(os.path.join(_g, "graphify-out", "graph.json"), "w") as fh:
        fh.write("{}")
    os.chmod(_g, _stat.S_IRUSR | _stat.S_IXUSR)
    _real_which = shutil.which
    pf.shutil.which = lambda c, *a, **k: ("/fake/bin/graphify" if c == "graphify"
                                          else _real_which(c, *a, **k))
    try:
        _st = pf.probe_graphify(_g, timeout=10)
        _r = pf.classify_graphify(_st)
        check("the test supplies the graphify binary instead of reading the machine",
              _st.binary == "/fake/bin/graphify", str(_st.binary))
        check("a stray that cannot be deleted is kept and reported",
              _st.stray_kept == [os.path.join(_g, "graphify-out")], str(_st.stray_kept))
        check("an un-deletable stray reaches FAIL end to end, not just in a fixture",
              _r.status == "FAIL", _r.summary)
        check("and that FAIL is what halts a cycle",
              pf.overall_exit([_r], strict=False) == 1)
        # A kept stray must not spawn graphify -- the binary above does not exist, so a
        # regression that dropped the `not st.stray_kept` guard would show up as rc 127.
        check("a kept stray stops preflight running graphify at all",
              _st.rebuilt is False and _st.query_rc is None,
              f"rebuilt={_st.rebuilt} query_rc={_st.query_rc}")

        # Same machine, same stray, no binary: the danger needs a querier, so this is a
        # WARN and the cycle continues. The stray still has to be NAMED -- it stays on
        # disk and starts answering false negatives the moment graphify is installed.
        pf.shutil.which = lambda c, *a, **k: (None if c == "graphify"
                                              else _real_which(c, *a, **k))
        _st_nb = pf.probe_graphify(_g, timeout=10)
        _r_nb = pf.classify_graphify(_st_nb)
        check("a kept stray without graphify installed does not halt a cycle",
              _r_nb.status == "WARN", _r_nb.status)
        check("but the kept stray is still named, not dropped for want of a binary",
              os.path.join(_g, "graphify-out") in " ".join([_r_nb.summary, *_r_nb.detail,
                                                           _r_nb.remedy or ""]),
              _r_nb.summary + str(_r_nb.detail))
        check("and the remedy is deleting the stray, not installing graphify",
              "rm -rf" in (_r_nb.remedy or ""), str(_r_nb.remedy))
    finally:
        pf.shutil.which = _real_which
        os.chmod(_g, _stat.S_IRWXU)
        shutil.rmtree(_g, ignore_errors=True)

# A stray preflight SUCCESSFULLY removed is a real change to the machine, and
# probe_graphify's contract is that both repairs are reported, "never folded into a
# silent PASS". Without a binary the classifier returned "graphify is not on PATH" and
# said nothing about the directory it had just deleted (#3140).
_g = tempfile.mkdtemp(prefix="preflight-stray-nobin-")
os.makedirs(os.path.join(_g, "AlRunner"))
os.makedirs(os.path.join(_g, "graphify-out"))
_real_which = shutil.which
pf.shutil.which = lambda c, *a, **k: (None if c == "graphify" else _real_which(c, *a, **k))
try:
    _st = pf.probe_graphify(_g, timeout=10)
    _r = pf.classify_graphify(_st)
    check("a stray is deleted even when graphify is not installed",
          _st.stray_removed == [os.path.join(_g, "graphify-out")]
          and not os.path.exists(os.path.join(_g, "graphify-out")), str(_st.stray_removed))
    check("the deletion is reported even when graphify is not installed",
          os.path.join(_g, "graphify-out") in " ".join([_r.summary, *_r.detail]),
          _r.summary + str(_r.detail))
    check("reporting the deletion does not turn a missing binary into a halt",
          _r.status == "WARN", _r.status)
finally:
    pf.shutil.which = _real_which
    shutil.rmtree(_g, ignore_errors=True)

# ---- nav-bc-decompiler: configured is not the same as usable
def dec_with(**kw):
    return pf.classify_decompiler(_dc.replace(_dec, **kw))


r = pf.classify_decompiler(pf.DecompilerState())
check("no .mcp.json is reported with the setup script",
      r.status == "WARN" and "setup-bc-decompiler.sh" in r.remedy, r.remedy)

r = dec_with(registered=False)
check("an .mcp.json without a bc-decompiler entry is reported", r.status == "WARN", r.summary)

r = dec_with(target_exists=False)
check("a registered server whose DLL is missing is reported", r.status == "WARN", r.summary)

# Registered and present, but the server does not actually answer. This is the
# "configured but not usable" state the check exists to separate out.
r = dec_with(probe_rc=1, error="server did not respond to initialize")
check("a registered server that does not answer is not reported as PASS",
      r.status == "WARN", r.summary)
check("the non-answering server's error is shown",
      "did not respond" in " ".join(r.detail), str(r.detail))

r = dec_with(aliases=[a for a in _EXPECTED_ALIASES if a != "bc284"])
check("a missing BC context is reported", r.status == "WARN", r.summary)
check("the missing BC context is named", "bc284" in r.summary, r.summary)

# ---- #3264: the expected contexts are .github/bc-versions.txt, not a second list
def _versions_file_prefixes():
    out = []
    with open(os.path.join(REPO_ROOT, ".github", "bc-versions.txt"), encoding="utf-8") as fh:
        for line in fh:
            if not line.lstrip().startswith("#"):
                out.extend(line.split())
    return out


_prefixes = _versions_file_prefixes()
check("bc-versions.txt: the file this check derives from lists versions at all",
      len(_prefixes) > 0, str(_prefixes))
check("bc-versions.txt: expected aliases were read without error",
      _EXPECTED_ERR == "", _EXPECTED_ERR)
check("bc-versions.txt: every listed version has exactly one expected context",
      sorted(_EXPECTED_ALIASES) == sorted("bc" + p.replace(".", "") for p in _prefixes)
      and len(set(_EXPECTED_ALIASES)) == len(_prefixes),
      f"expected={_EXPECTED_ALIASES} prefixes={_prefixes}")
check("bc-versions.txt: BC 26 is not expected, because the repository does not test it",
      "bc260" not in _EXPECTED_ALIASES, str(_EXPECTED_ALIASES))

_parsed, _err = pf.decompiler_aliases_from_versions("# header\n27.0 27.5\n# x\n28.4\n")
check("the versions parser maps prefixes to aliases and skips comments",
      _parsed == ["bc270", "bc275", "bc284"] and _err == "", f"{_parsed} {_err!r}")
_parsed, _err = pf.decompiler_aliases_from_versions("# only a header\n")
check("a versions file listing nothing is an error, not an empty expected set",
      _parsed is None and "no BC versions" in _err, f"{_parsed} {_err!r}")
_parsed, _err = pf.decompiler_aliases_from_versions("27.0 28\n")
check("a malformed version prefix is an error naming the token",
      _parsed is None and "'28'" in _err, f"{_parsed} {_err!r}")
_parsed, _err = pf.read_expected_decompiler_aliases(tempfile.mkdtemp())
check("an absent bc-versions.txt is an error, not an empty expected set",
      _parsed is None and "bc-versions.txt" in _err, f"{_parsed} {_err!r}")

# A context registered for a version the repo does not test is extra, not a finding:
# this box has bc260 registered, and it must not start warning the other way.
r = dec_with(aliases=list(_EXPECTED_ALIASES) + ["bc260"])
check("an extra registered context (bc260) does not stop the check passing",
      r.status == "PASS", r.summary)
r = dec_with(aliases=["bc260"] + [a for a in _EXPECTED_ALIASES if a != "bc273"])
check("bc260 being registered does not stand in for a missing tested version",
      r.status == "WARN" and "bc273" in r.summary and "bc260" not in r.summary, r.summary)

# The third state: the expected set could not be established. Never PASS, and the
# message names the file rather than claiming "all 0 contexts are registered".
for _exp, _why in ((None, "bc-versions.txt unreadable"), ([], "empty expected set")):
    r = dec_with(expected=_exp, expected_error=_why)
    check(f"expected contexts unknown ({_why}) is not reported as PASS",
          r.status == "WARN", f"{r.status} {r.summary}")
    check(f"expected contexts unknown ({_why}) names bc-versions.txt",
          "bc-versions.txt" in r.summary, r.summary)

# Docs that name a decompiler alias must name one the repository tests.
_alias_re = re.compile(r"\bbc(\d{3})\b")
_doc_hits, _silent_docs = {}, []
for _rel in (".claude/agents/impl-agent.md", ".claude/agents/triager.md",
             ".claude/skills/orchestrating-a-session/SKILL.md", "CLAUDE.md",
             "tools/setup-bc-decompiler.sh"):
    with open(os.path.join(REPO_ROOT, _rel), encoding="utf-8") as fh:
        _found = list(_alias_re.finditer(fh.read()))
    if not _found:
        _silent_docs.append(_rel)
    for _m in _found:
        _doc_hits.setdefault("bc" + _m.group(1), set()).add(_rel)
check("every scanned doc yields at least one alias (a zero here is a broken pattern or a moved table)",
      not _silent_docs, str(_silent_docs))
_stray = {a: sorted(f) for a, f in _doc_hits.items() if a not in _EXPECTED_ALIASES}
check("every decompiler alias the docs name is a version in bc-versions.txt",
      not _stray, str(_stray))

check("the passing decompiler report says a .mcp.json change needs a session restart",
      "SESSION RESTART" in " ".join(pf.classify_decompiler(_dec).detail),
      str(pf.classify_decompiler(_dec).detail))

# ---- the severity policy is a decision, so it is pinned here
_broken = [
    pf.classify_lsp(pf.LspState(script=False)),
    pf.classify_lsp(_dc.replace(_lsp, rc=2)),
    pf.classify_lsp(_dc.replace(_lsp, fixture_ok=False)),
    pf.classify_lsp(_dc.replace(_lsp, server_on_path=False)),
    pf.classify_lsp(_dc.replace(_lsp, timed_out=True, rc=124)),
    pf.classify_lsp(_dc.replace(_lsp, rc=0, out="nope")),
    pf.classify_graphify(pf.GraphifyState()),
    graph_with(stray_removed=["/repo/graphify-out"]),
    graph_with(rebuilt=True, rebuild_rc=1, rebuild_out="boom"),
    graph_with(query_rc=1),
    # Every branch, not every classifier: an earlier version of this list reached
    # classify_graphify only through GraphifyState() (graphify not on PATH), so a
    # mutation turning the "no graph" branch into a FAIL slipped past the severity
    # guard entirely. It was caught by a different test, which is luck, not coverage.
    graph_with(graph=None, graph_mtime=None),
    graph_with(query_out="NODE SomethingElse"),
    pf.classify_lsp(_dc.replace(_lsp, rc=1)),
    pf.classify_lsp(_dc.replace(_lsp, rc=99)),
    dec_with(registered=False),
    pf.classify_decompiler(pf.DecompilerState()),
    dec_with(probe_rc=1),
    dec_with(target_exists=False),
    dec_with(aliases=[]),
]
check("a tool being absent or unusable never halts a cycle - only a wrong ANSWER does",
      all(r.status != "FAIL" for r in _broken),
      str([(r.name, r.status) for r in _broken if r.status == "FAIL"]))
# The boundary itself, asserted from both sides in one place so it cannot drift: the
# un-removable stray graph is the ONLY navigation condition that may halt a cycle.
# Everything above degrades to rg / tools/context-pack.py and merely slows an agent
# down; a stray graph makes graphify answer "No matching nodes found." for symbols
# that exist, which no fallback rescues and no rebuild fixes.
check("the un-removable stray graph is the one navigation condition that CAN fail",
      pf.classify_graphify(_dc.replace(_graph, stray_kept=["/x"])).status == "FAIL")
check("and it is the ONLY one - every other broken state stays advisory",
      {r.status for r in _broken} == {"WARN"},
      str(sorted({(r.name, r.status) for r in _broken})))
check("every broken navigation state still says what to do about it",
      all(r.remedy for r in _broken),
      str([r.name for r in _broken if not r.remedy]))
check("every broken navigation state names the command that produced it",
      all(r.command for r in _broken),
      str([r.name for r in _broken if not r.command]))
check("a warning navigation check leaves the exit code at 0 (advisory, not blocking)",
      pf.overall_exit(_broken, strict=False) == 0, str(pf.overall_exit(_broken, False)))
check("--strict still promotes a navigation warning to exit 2",
      pf.overall_exit(_broken, strict=True) == 2)

# ---- human_age, used in the stale summary
check("human_age reports seconds", pf.human_age(30) == "30s")
check("human_age reports minutes", pf.human_age(600) == "10m")
check("human_age reports hours", pf.human_age(7200) == "2.0h")
check("human_age reports days", pf.human_age(18 * 86400) == "18.0d")

# ---- newest_source_mtime ignores build output, or every graph looks stale
_srcdir = tempfile.mkdtemp(prefix="preflight-src-")
os.makedirs(os.path.join(_srcdir, "obj"), exist_ok=True)
with open(os.path.join(_srcdir, "Real.cs"), "w") as fh:
    fh.write("x")
os.utime(os.path.join(_srcdir, "Real.cs"), (1000, 1000))
with open(os.path.join(_srcdir, "obj", "Generated.cs"), "w") as fh:
    fh.write("x")
os.utime(os.path.join(_srcdir, "obj", "Generated.cs"), (9_000_000, 9_000_000))
_m, _p = pf.newest_source_mtime(_srcdir)
check("newest_source_mtime skips obj/ build output", _m == 1000, f"{_m} {_p}")
check("newest_source_mtime names the file it picked", _p.endswith("Real.cs"), _p)
shutil.rmtree(_srcdir, ignore_errors=True)

# ---- read_mcp_entry against a real file
_cfgdir = tempfile.mkdtemp(prefix="preflight-mcp-")
_path, _entry = pf.read_mcp_entry(_cfgdir, pf.DECOMPILER_SERVER)
check("a repo with no .mcp.json reports no config", _path is None and _entry is None)
with open(os.path.join(_cfgdir, ".mcp.json"), "w") as fh:
    json.dump({"mcpServers": {"bc-decompiler": {"command": "dotnet", "args": ["/x/S.dll"]}}}, fh)
_path, _entry = pf.read_mcp_entry(_cfgdir, pf.DECOMPILER_SERVER)
check("the bc-decompiler entry is read out of .mcp.json",
      _entry is not None and _entry["args"] == ["/x/S.dll"], str(_entry))
with open(os.path.join(_cfgdir, ".mcp.json"), "w") as fh:
    fh.write("{ not json")
_path, _entry = pf.read_mcp_entry(_cfgdir, pf.DECOMPILER_SERVER)
check("an unparseable .mcp.json is reported as 'no entry', not raised",
      _path is not None and _entry is None)
shutil.rmtree(_cfgdir, ignore_errors=True)

# ---- mcp_call must survive mise's banner landing in the JSON-RPC stream
# `dotnet` is a mise shim on this box, and mise prints its activation banner on
# STDOUT. In a line-delimited JSON-RPC stream that is not a formatting nuisance --
# it is a parse error on the first read, which would report a healthy server as
# broken. Same failure mode strip_shim_banner() exists for, one layer down.
_fake = tempfile.mkdtemp(prefix="preflight-mcp-srv-")
_srv = os.path.join(_fake, "srv.py")
with open(_srv, "w") as fh:
    fh.write(
        "import json,sys\n"
        "sys.stdout.write('mise ~/.config/mise/config.toml tools: dotnet@10.0.0\\n')\n"
        "sys.stdout.flush()\n"
        "for line in sys.stdin:\n"
        "    try: msg=json.loads(line)\n"
        "    except ValueError: continue\n"
        "    if msg.get('method')=='initialize':\n"
        "        print(json.dumps({'jsonrpc':'2.0','id':msg['id'],'result':{}}),flush=True)\n"
        "    elif msg.get('method')=='tools/call':\n"
        "        body=json.dumps({'status':'ok','data':{'registeredAliases':['bc281']}})\n"
        "        print(json.dumps({'jsonrpc':'2.0','id':msg['id'],\n"
        "            'result':{'content':[{'type':'text','text':body}]}}),flush=True)\n")
_rc, _payload, _err = pf.mcp_call([sys.executable, _srv], "list_contexts", timeout=30)
check("mcp_call talks to a server whose stdout starts with a mise banner",
      _rc == 0, f"rc={_rc} err={_err}")
check("mcp_call returns the tool payload past the banner",
      _payload.get("data", {}).get("registeredAliases") == ["bc281"], str(_payload))

_rc, _payload, _err = pf.mcp_call([sys.executable, "-c", "import sys; sys.exit(0)"],
                                  "list_contexts", timeout=20)
check("a server that exits without answering is an error, never an empty success",
      _rc != 0 and _err, f"rc={_rc} err={_err}")

_rc, _payload, _err = pf.mcp_call(["/nonexistent/mcp-server"], "list_contexts", timeout=10)
check("a server binary that does not exist is an error, never an empty success",
      _rc != 0 and _err, f"rc={_rc} err={_err}")
shutil.rmtree(_fake, ignore_errors=True)

# ---- the report a contributor actually reads
_txt = pf.render_report([pf.classify_lsp(_dc.replace(_lsp, rc=2)),
                         graph_with(stray_removed=["/repo/graphify-out"]),
                         dec_with(probe_rc=1, error="no answer")], strict=False)
check("the navigation warnings render one line each with a name",
      _txt.count("WARN") >= 3, _txt)
check("the rendered navigation report tells the reader what to do",
      _txt.count("-> what to do:") >= 3, _txt)


# ------------------------------------------------ the push probe (#3076)
# preflight's exit 1 means "this box would produce untrustworthy results", and
# the autonomous cycle treats it as stop-everything. A SINGLE DROPPED PACKET must
# not produce it: measured 2026-09-06, the probe was refused, then succeeded, then
# was refused again by hand -- 1 of 3 -- and six consecutive runs seconds later
# all succeeded. The FAIL halted a healthy box.
#
# The opposite error is worse, though. A retry that masks a genuinely broken
# credential defeats the check entirely, so the line drawn here is the CLASS of
# the failure, not the count: a REFUSAL (permission, authentication, no such
# repository) is persistent and is never retried, and no number of retries can
# produce a PASS without one attempt actually negotiating successfully.
pf.PUSH_RETRY_BACKOFF = (0, 0)          # the tests must not sleep

# Real git output. The last line of both is the same boilerplate, which is why
# the reported summary read "push was refused: and the repository exists." for a
# failure that was a dropped packet.
SSH_TIMEOUT = ("ssh: connect to host github.com port 22: Connection timed out\r\n"
               "fatal: Could not read from remote repository.\n\n"
               "Please make sure you have the correct access rights\n"
               "and the repository exists.\n")
SSH_DENIED = ("git@github.com: Permission denied (publickey).\r\n"
              "fatal: Could not read from remote repository.\n\n"
              "Please make sure you have the correct access rights\n"
              "and the repository exists.\n")
HTTPS_403 = ("remote: Permission to StefanMaron/BusinessCentral.AL.Runner.git denied to bot.\n"
             "fatal: unable to access 'https://github.com/StefanMaron/"
             "BusinessCentral.AL.Runner.git/': The requested URL returned error: 403\n")


class PushScript:
    """A pf.run replacement that answers the push probe from a script.

    Every other command it is asked (the push remote's URL) is answered
    truthfully, so the check under test sees a normal box apart from the
    transport.
    """

    def __init__(self, outcomes, push_url="git@github.com:StefanMaron/"
                                          "BusinessCentral.AL.Runner.git"):
        self.outcomes = list(outcomes)
        self.push_calls = 0
        self.push_url = push_url

    def __call__(self, argv, **kw):
        if "push" in argv:
            self.push_calls += 1
            outcome = self.outcomes[min(self.push_calls, len(self.outcomes)) - 1]
            if outcome == "ok":
                return pf.Ran(rc=0, out="To github.com:StefanMaron/x.git\n", err="")
            if outcome == "timeout":
                return pf.Ran(rc=124, out="", err="", timed_out=True)
            return pf.Ran(rc=128, out="", err=outcome)
        if "get-url" in argv:
            return pf.Ran(rc=0, out=self.push_url + "\n", err="")
        return pf.Ran(rc=0, out="", err="")


def push_result(outcomes, **kw):
    script = PushScript(outcomes, **kw)
    saved = pf.run
    pf.run = script
    try:
        return pf.check_push("/repo", "stma-auto-1"), script
    finally:
        pf.run = saved


# ---- classification: which failures are worth a second attempt, and which are a verdict
check("an SSH connection timeout is transient",
      pf.classify_transport_error(SSH_TIMEOUT) == "transient",
      pf.classify_transport_error(SSH_TIMEOUT))
check("a publickey refusal is an authentication verdict, not a transient",
      pf.classify_transport_error(SSH_DENIED) == "auth",
      pf.classify_transport_error(SSH_DENIED))
check("an HTTPS 403 is an authorization verdict",
      pf.classify_transport_error(HTTPS_403) == "auth",
      pf.classify_transport_error(HTTPS_403))
check("a reset during SSH key exchange is transient",
      pf.classify_transport_error(
          "kex_exchange_identification: Connection reset by peer") == "transient")
check("a name-resolution failure is transient",
      pf.classify_transport_error(
          "fatal: unable to access 'https://github.com/x.git/': "
          "Could not resolve host: github.com") == "transient")
check("'Repository not found' is persistent, not something to retry",
      pf.classify_transport_error("ERROR: Repository not found.") == "auth",
      pf.classify_transport_error("ERROR: Repository not found."))
check("the shared boilerplate alone classifies as unknown, never as an auth verdict",
      pf.classify_transport_error("fatal: Could not read from remote repository.\n"
                                  "Please make sure you have the correct access rights\n"
                                  "and the repository exists.\n") == "unknown",
      pf.classify_transport_error("fatal: Could not read from remote repository.\n"
                                  "Please make sure you have the correct access rights\n"
                                  "and the repository exists.\n"))

# ---- the reported line: the cause, not the last line of boilerplate
check("the summary line names the transport failure, not 'and the repository exists.'",
      pf.transport_error_line(SSH_TIMEOUT)
      == "ssh: connect to host github.com port 22: Connection timed out",
      pf.transport_error_line(SSH_TIMEOUT))
check("the summary line names the publickey refusal",
      pf.transport_error_line(SSH_DENIED) == "git@github.com: Permission denied (publickey).",
      pf.transport_error_line(SSH_DENIED))
check("an error with nothing but boilerplate still reports something",
      pf.transport_error_line("") == "no message",
      pf.transport_error_line(""))

# ---- 1 of 3, the measured case: a WARN that names what it measured, never a FAIL
_res, _script = push_result([SSH_TIMEOUT, SSH_TIMEOUT, "ok"])
check("two dropped packets followed by a success does not fail the box",
      _res.status == "WARN", f"{_res.status}: {_res.summary}")
check("a flaky transport is retried to the third attempt",
      _script.push_calls == 3, _script.push_calls)
check("the flaky verdict says what it measured",
      "1 of 3" in _res.summary, _res.summary)
check("the flaky verdict names the transport",
      "ssh" in (_res.summary + " ".join(_res.detail)).lower(),
      f"{_res.summary} | {_res.detail}")

# ---- a healthy box costs exactly one attempt, and still PASSes
_res, _script = push_result(["ok"])
check("a working push is a PASS", _res.status == "PASS", _res.summary)
check("a working push costs one attempt, not three", _script.push_calls == 1,
      _script.push_calls)

# ---- a real refusal FAILs on the first attempt: retrying it would only delay a verdict
_res, _script = push_result([SSH_DENIED, "ok", "ok"])
check("a publickey refusal fails the box", _res.status == "FAIL", _res.summary)
check("a refusal is never retried -- a later success cannot un-refuse it",
      _script.push_calls == 1, _script.push_calls)
check("the refusal summary names the refusal itself",
      "Permission denied (publickey)" in _res.summary, _res.summary)
check("the refusal summary is not the trailing boilerplate",
      "and the repository exists" not in _res.summary, _res.summary)

# ---- transport down for every attempt is still a FAIL, but it says reachability
_res, _script = push_result([SSH_TIMEOUT, SSH_TIMEOUT, SSH_TIMEOUT])
check("a transport that never works fails the box", _res.status == "FAIL", _res.summary)
check("no number of retries invents a PASS", _script.push_calls == 3, _script.push_calls)
check("an unreachable transport is reported as reachability, not authorization",
      "reach" in _res.summary.lower() or "reach" in " ".join(_res.detail).lower(),
      f"{_res.summary} | {_res.detail}")
check("the unreachable remedy does not send the reader to re-authenticate first",
      "port 22" in (_res.remedy + " ".join(_res.detail)) or
      "network" in _res.remedy.lower(), _res.remedy)

# ---- an unrecognised error is retried rather than treated as a verdict
_res, _script = push_result(["something nobody has seen before\n",
                             "something nobody has seen before\n", "ok"])
check("an unclassifiable error is retried, not read as a broken credential",
      _script.push_calls == 3 and _res.status == "WARN",
      f"{_script.push_calls} {_res.status}")

# ---- the hang diagnosis survives: an interactive prompt nobody answers
_res, _script = push_result(["timeout", "timeout", "timeout"])
check("a probe that hangs every time still reports the interactive-prompt cause",
      _res.status == "FAIL" and "interactive" in _res.summary,
      f"{_res.status}: {_res.summary}")
_res, _script = push_result(["timeout", "ok"])
check("one hung attempt followed by a success is a flaky transport, not a hang",
      _res.status == "WARN" and _script.push_calls == 2,
      f"{_res.status} {_script.push_calls}")

# ---- run_retry spaces its attempts instead of firing them back to back
_slept: list = []
_calls: list = []


def _always_fail(argv, **kw):
    _calls.append(argv)
    return pf.Ran(rc=1, out="", err="nope")


_saved_run = pf.run
pf.run = _always_fail
try:
    _r = pf.run_retry(["gh", "api", "user"], attempts=3, sleeper=_slept.append)
finally:
    pf.run = _saved_run
check("run_retry uses every attempt before giving up", len(_calls) == 3, len(_calls))
check("run_retry waits between attempts rather than firing them back to back",
      len(_slept) == 2 and all(s > 0 for s in _slept), _slept)

_slept, _calls = [], []
_saved_run = pf.run
pf.run = lambda argv, **kw: (_calls.append(argv), pf.Ran(rc=0, out="ok", err=""))[1]
try:
    pf.run_retry(["gh", "api", "user"], attempts=3, sleeper=_slept.append)
finally:
    pf.run = _saved_run
check("run_retry never sleeps after a first-attempt success",
      len(_calls) == 1 and _slept == [], f"{len(_calls)} {_slept}")


# --------------------------------------- token scopes: what this box can merge (#3192)
# check_github reported "merge a PR: yes" from the REPOSITORY's permissions while
# the token had no `workflow` scope, so every PR touching .github/workflows/ was
# unmergeable -- discovered at the last step, after review and after CI.
GH_AUTH_STATUS = ("github.com\n"
                  "  ✓ Logged in to github.com account StefanMaron (keyring)\n"
                  "  - Active account: true\n"
                  "  - Git operations protocol: ssh\n"
                  "  - Token: gho_************************************\n"
                  "  - Token scopes: 'admin:public_key', 'gist', 'read:org', 'repo'\n")

check("the token's scopes are read out of a real `gh auth status`",
      pf.parse_token_scopes(GH_AUTH_STATUS)
      == {"admin:public_key", "gist", "read:org", "repo"},
      pf.parse_token_scopes(GH_AUTH_STATUS))
check("a status with no scopes line is unknown, not an empty scope set",
      pf.parse_token_scopes("github.com\n  - Active account: true\n") is None,
      pf.parse_token_scopes("github.com\n  - Active account: true\n"))
check("'Token scopes: none' is unknown too -- a fine-grained token has no classic scopes",
      pf.parse_token_scopes("  - Token scopes: none\n") is None,
      pf.parse_token_scopes("  - Token scopes: none\n"))


class GhScript:
    """pf.run replacement answering the three commands check_github issues."""

    def __init__(self, auth_status=GH_AUTH_STATUS, perms=None, auth_rc=0, auth_err=""):
        self.auth_status = auth_status
        self.auth_rc = auth_rc
        self.auth_err = auth_err
        self.perms = perms if perms is not None else {
            "admin": True, "maintain": True, "push": True, "pull": True, "triage": True}

    def __call__(self, argv, **kw):
        joined = " ".join(argv)
        if "auth" in argv and "status" in argv:
            if self.auth_rc:
                return pf.Ran(rc=self.auth_rc, out="", err=self.auth_err)
            return pf.Ran(rc=0, out=self.auth_status, err="")
        if "user" in joined:
            return pf.Ran(rc=0, out="StefanMaron\n", err="")
        if "repos/" in joined:
            return pf.Ran(rc=0, out=json.dumps(self.perms), err="")
        return pf.Ran(rc=0, out="", err="")


class FakeShutil:
    which = staticmethod(lambda name: "/usr/bin/" + name)
    rmtree = staticmethod(shutil.rmtree)


def github_result(**kw):
    saved_run, saved_shutil = pf.run, pf.shutil
    pf.run, pf.shutil = GhScript(**kw), FakeShutil
    try:
        return pf.check_github("StefanMaron/BusinessCentral.AL.Runner")
    finally:
        pf.run, pf.shutil = saved_run, saved_shutil


_res = github_result()
check("a token without `workflow` scope is a WARN, not a silent PASS",
      _res.status == "WARN", f"{_res.status}: {_res.summary}")
check("the warning names the scope that is missing",
      "workflow" in _res.summary, _res.summary)
check("the report says which class of PR this box cannot merge",
      any(".github/workflows/" in d for d in _res.detail), _res.detail)
check("the machine-readable answer records it too",
      _res.data.get("can_merge_workflow_changes") is False, _res.data)
check("the remedy does not teach the loop a second mode",
      "human" in _res.remedy or "gh auth refresh" in _res.remedy, _res.remedy)

_res = github_result(auth_status=GH_AUTH_STATUS.replace(
    "'read:org'", "'read:org', 'workflow'"))
check("a token WITH `workflow` scope passes",
      _res.status == "PASS", f"{_res.status}: {_res.summary}")
check("and says so, rather than staying silent about the class",
      any(".github/workflows/" in d and "yes" in d for d in _res.detail), _res.detail)
check("the machine-readable answer records the affirmative",
      _res.data.get("can_merge_workflow_changes") is True, _res.data)

_res = github_result(auth_status="github.com\n  - Active account: true\n")
check("unreadable scopes never manufacture a warning",
      _res.status == "PASS", f"{_res.status}: {_res.summary}")
check("unreadable scopes are said out loud rather than assumed",
      any("scope" in d and ("not" in d or "could not" in d) for d in _res.detail),
      _res.detail)
check("and the machine-readable answer is unknown, not False",
      _res.data.get("can_merge_workflow_changes") is None, _res.data)


# ---- the same dropped packet, one function over: `gh auth status` also uses the network
GH_UNREACHABLE = ("error connecting to github.com\n"
                  "Post \"https://github.com/api/v3/graphql\": dial tcp 140.82.121.4:443: "
                  "i/o timeout\n")
check("gh's own transport failure classifies as transient",
      pf.classify_transport_error(GH_UNREACHABLE) == "transient",
      pf.classify_transport_error(GH_UNREACHABLE))

_res = github_result(auth_rc=1, auth_err=GH_UNREACHABLE)
check("an unreachable github.com is not reported as a missing credential",
      _res.status == "FAIL" and "not authenticated" not in _res.summary,
      f"{_res.status}: {_res.summary}")
check("it is reported as reachability instead",
      "reach" in _res.summary, _res.summary)

_res = github_result(auth_rc=1, auth_err="You are not logged into any GitHub hosts. "
                                         "To log in, run: gh auth login\n")
check("a genuine logged-out gh still FAILs as not authenticated",
      _res.status == "FAIL" and "not authenticated" in _res.summary,
      f"{_res.status}: {_res.summary}")


# ------------------------ how current the checkout being measured is (real git)
# The whole-tree version of the same trap: a coordinator read tools/ci-wait.py out
# of a top-level checkout 40+ commits behind origin/main, found a constant that
# origin/main had already renamed, concluded the tool was broken for every pull
# request in the repository, and misdirected four agents. origin/main's copy was
# correct throughout. Real git here, not a fixture: the question is what git says
# about HEAD against refs/remotes/origin/main, and a mocked answer proves nothing.
def lag_repo(behind: int, base_age_days: float = 0.0) -> str:
    root = tempfile.mkdtemp(prefix="preflight-lag-")
    git("init", "-q", "-b", "main", ".", cwd=root)
    git("config", "user.email", "t@example.com", cwd=root)
    git("config", "user.name", "T", cwd=root)
    when = dt.datetime.now(dt.timezone.utc) - dt.timedelta(days=base_age_days)
    stamp = when.strftime("%Y-%m-%dT%H:%M:%S+00:00")
    open(os.path.join(root, "f"), "w").write("base\n")
    git("add", "f", cwd=root)
    git("commit", "-q", "-m", "base", cwd=root,
        env=dict(os.environ, GIT_AUTHOR_DATE=stamp, GIT_COMMITTER_DATE=stamp))
    branch_point = git("rev-parse", "HEAD", cwd=root).stdout.strip()
    for i in range(behind):
        open(os.path.join(root, "f"), "w").write(f"main {i}\n")
        git("commit", "-q", "-am", f"main {i}", cwd=root)
    tip = git("rev-parse", "HEAD", cwd=root).stdout.strip()
    git("update-ref", "refs/remotes/origin/main", tip, cwd=root)
    git("checkout", "-q", "-B", "work", branch_point, cwd=root)
    return root


_root = lag_repo(behind=3)
_lag = pf.checkout_lag("this checkout", _root)
check("a checkout three commits behind measures as three",
      _lag["behind"] == 3 and _lag["ahead"] == 0, _lag)
check("and the branch point's age is measured, not guessed",
      _lag["base_age_hours"] is not None and _lag["base_age_hours"] < 1, _lag)
_res = pf.classify_checkout([_lag])
check("normal drift is a PASS -- a warning that always fires is not read",
      _res.status == "PASS", f"{_res.status}: {_res.summary}")
check("but the number is reported either way, which is what was missing",
      any("3 commit(s) behind" in d for d in _res.detail), _res.detail)
shutil.rmtree(_root, ignore_errors=True)

_root = lag_repo(behind=40)
_res = pf.classify_checkout([pf.checkout_lag("the main checkout", _root)])
check("40 commits behind -- the measured incident -- WARNs",
      _res.status == "WARN", f"{_res.status}: {_res.summary}")
check("the warning says what to stop doing, not just what to run",
      "measure" in _res.remedy.lower() or "conclude" in _res.remedy.lower(), _res.remedy)
shutil.rmtree(_root, ignore_errors=True)

_root = lag_repo(behind=1, base_age_days=3)
_res = pf.classify_checkout([pf.checkout_lag("this checkout", _root)])
check("a tree branched three days ago WARNs even when it is one commit behind",
      _res.status == "WARN", f"{_res.status}: {_res.summary}")
shutil.rmtree(_root, ignore_errors=True)

_root = tempfile.mkdtemp(prefix="preflight-lag-none-")
git("init", "-q", "-b", "main", ".", cwd=_root)
_lag = pf.checkout_lag("this checkout", _root)
check("a checkout with no origin/main says so rather than reporting zero",
      _lag["error"] and _lag["behind"] is None, _lag)
_res = pf.classify_checkout([_lag])
check("and that is a WARN about not knowing, never a PASS",
      _res.status == "WARN" and "could not" in _res.summary, _res.summary)
shutil.rmtree(_root, ignore_errors=True)

_res = pf.classify_checkout([{"label": "a", "error": "no origin/main"},
                             {"label": "b", "branch": "main", "ahead": 0, "behind": 40,
                              "base_age_hours": 30.0, "error": ""}])
check("one unmeasurable checkout does not hide a stale one",
      _res.status == "WARN" and "40 commit(s)" in _res.summary, _res.summary)


# ------------------------------------- a stale copy of preflight must not answer (#3164)
# Same exposure #3020 fixed in ci-wait.py and pr-body.py: an agent runs the copy
# in its own worktree, and a worktree is created once and never fast-forwarded.
# Measured 2026-09-06, 40 of the 59 worktrees carrying tools/preflight.py had a
# version that was not origin/main's, across 4 distinct versions. A copy predating
# #2936 reports a HEALTHY box as unable to push -- a wrong verdict, from the tool
# whose entire job is to produce trustworthy ones.
class FakeFreshness:
    def __init__(self, refuse, notes=("note: preflight.py is STALE.",)):
        self._refuse = refuse
        self.notes = list(notes)
        self.targets: list = []
        self.remote_checks: list = []
        self.__file__ = "/repo/tools/agent_self_freshness.py"

    def assess(self, target, remote_check=True, **kw):
        self.targets.append(target)
        self.remote_checks.append(remote_check)
        return pf_freshness_result(self._refuse, self.notes)


def pf_freshness_result(refuse, notes):
    class R:
        pass
    r = R()
    r.refuse = refuse
    r.notes = list(notes)
    r.state = "stale" if refuse else "current"
    return r


def refusal_with(fresh, **kw):
    saved = pf._freshness
    pf._freshness = fresh
    printed: list = []
    try:
        return pf.freshness_refusal(printed.append, **kw), printed
    finally:
        pf._freshness = saved


_fake = FakeFreshness(refuse=True)
_code, _printed = refusal_with(_fake)
check("a stale preflight.py refuses rather than reporting on the box",
      _code == 3, _code)
check("the refusal is exit 3 -- could not complete, never a verdict about the box",
      pf.EXIT_MEANING[3] and _code == 3, pf.EXIT_MEANING.get(3))
check("the refusal explains itself and prints the notes",
      any("STALE" in p for p in _printed) and
      any("preflight" in p for p in "\n".join(_printed).splitlines()),
      _printed)
check("both this file and the freshness module itself are checked",
      len(_fake.targets) == 2 and
      any(t.endswith("preflight.py") for t in _fake.targets) and
      any(t.endswith("agent_self_freshness.py") for t in _fake.targets),
      _fake.targets)
check("the remote is confirmed once, not once per file",
      _fake.remote_checks == [True, False], _fake.remote_checks)

_fake = FakeFreshness(refuse=False, notes=["note: current."])
_code, _printed = refusal_with(_fake)
check("a current copy answers normally", _code is None, _code)

_code, _printed = refusal_with(FakeFreshness(refuse=True), remote_check=False)
check("--no-freshness-fetch skips the ls-remote but not the staleness check",
      _code == 3, _code)

_saved = pf._freshness
pf._freshness = None
_printed = []
try:
    _code = pf.freshness_refusal(_printed.append)
finally:
    pf._freshness = _saved
check("a copy detached from the freshness module answers, but says nothing checked it",
      _code is None and any("could not establish" in p for p in _printed), _printed)

# ---- and the refusal happens BEFORE anything is probed
_probed: list = []
_saved_check_space, _saved_refusal = pf.check_space, pf.freshness_refusal
pf.check_space = lambda *a, **k: _probed.append(a) or pf.CheckResult(
    name="disk", status="PASS", summary="x")
pf.freshness_refusal = lambda *a, **k: 3
_buf = io.StringIO()
_saved_stdout = sys.stdout
sys.stdout = _buf
try:
    _rc = pf.main(["--json"])
finally:
    sys.stdout = _saved_stdout
    pf.check_space, pf.freshness_refusal = _saved_check_space, _saved_refusal
check("main returns 3 on a stale copy", _rc == 3, _rc)
check("a refused run probes nothing at all", _probed == [], _probed)
_doc = json.loads(_buf.getvalue())
check("--json still emits a document, so a machine reader cannot read silence as success",
      _doc.get("exit_code") == 3, _buf.getvalue()[:200])
check("the JSON refusal carries no checks and says why",
      _doc.get("checks") == [] and "refusal" in _doc, sorted(_doc))



# ------------------------------------------------- corpus baseline (--with-corpus)
print()
print("preflight.py -- corpus baseline")

# The three corpus apps `scripts/corpus-app-dirs.py` enumerates.
#
# There is no committed expected count for them any more (#3675): the corpus is
# resolved per run (#3737), so a number checked in here would go stale the moment
# an upstream corpus PR merged and this check would fail on a healthy box. CI
# compares against the last count a main run recorded, at its corpus SHA -- a
# second endpoint a single local run does not have. What this check still holds
# is everything it could always read for itself: failures, lost suites, a missing
# summary, a timeout, an enumerator that produced nothing, and the run's own
# summary against its per-bundle PASS lines.
CORPUS_APPS = ["tests/al-language/tests/al-language",
               "tests/al-language/tests/al-language-internals-fixture",
               "tests/al-language/tests/al-language-onprem"]


def corpus_repo():
    """A throwaway repo root for the corpus check to run against."""
    return tempfile.mkdtemp(prefix="preflight-corpus-")


# Verbatim shape of a real run, captured from
#   dotnet run --project AlRunner -c Release -- tests/runner-extras/object-system-table \
#     --package-cache ... --show-pass
# on 2026-09-07. The labels, the two spaces after PASS, the `(45ms)` suffix and
# every summary line below are that output, not an idea of it -- a fixture shaped
# to satisfy the parser would test the author rather than the runner (#3311).
def corpus_output(counts, *, fail=0, error=0, skipped=0, oos=0, known_gap=0,
                  reported_pass=None, summary=True, suite_errors=(), compile_fail=(),
                  drop_fields=()):
    """Synthesise runner output for `counts` = {bundle dir name: passing tests}."""
    lines = ["al-runner — running %d bundle(s)" % (len(counts) + len(compile_fail))]
    for name in compile_fail:
        lines += ["", f"=== {name} — COMPILE FAIL ===", "  AL0185: something"]
    for name, n in counts.items():
        if name in suite_errors:
            lines += ["", f"=== {name} — SUITE ERRORS (1) ===", "  lost a suite",
                      "  → the tests these suites declare are MISSING from this run, "
                      "not passing."]
        if n == 0:
            continue
        lines += ["", f"=== {name} ==="]
        for i in range(n):
            label = "PASS "
            if i < oos:
                label = "PASS (oos)"
            elif i < oos + known_gap:
                label = "PASS (known-gap)"
            lines.append(f"{label} Codeunit6555{len(name) % 10}.Test_{name.replace('-', '_')}_{i} ({i}ms)")
        for i in range(fail if name == list(counts)[0] else 0):
            lines.append(f"FAIL  Codeunit65551.Broken_{i} (3ms)")
            lines.append("      Assert.AreEqual failed")
    if not summary:
        return "\n".join(lines) + "\n"
    total = sum(counts.values()) + fail + error + skipped
    shown = sum(counts.values()) if reported_pass is None else reported_pass
    lines += ["", "=" * 65, "al-runner — test run summary", "=" * 65,
              f"Buckets:       {len(counts)} total",
              f"  ran:         {len(counts)}",
              "  compile-fail:%d" % len(compile_fail),
              "  exec-fail:   0",
              f"Tests:         {total} total",
              f"  pass:        {shown}"]
    if oos:
        lines.append(f"    pass-oos:        {oos}")
    if known_gap:
        lines.append(f"    pass-known-gap:  {known_gap}")
    # `drop_fields` omits a summary LINE, which is how a key goes missing for
    # real: parse_corpus_run seeds the dict from `Tests: N total` and adds a key
    # only when its line appears, so a summary block truncated or reshaped by a
    # BC-version change yields {"total", "pass"} with no `fail` at all (#3361).
    if "fail" not in drop_fields:
        lines.append(f"  fail:        {fail}")
    if "error" not in drop_fields:
        lines.append(f"  error:       {error}")
    if skipped:
        lines.append(f"  skipped:     {skipped}")
    lines += ["Time:", "  AL emit:     2.1s", "  C# compile:  1.7s",
              "  test run:    0.2s", "  total:       4.0s", "  wall:        6.3s",
              "=" * 65]
    return "\n".join(lines) + "\n"


class CorpusScript:
    """A pf.run replacement answering both commands check_corpus issues."""

    def __init__(self, output, rc=0, apps=None, timed_out=False, enumerator_rc=0):
        self.output, self.rc, self.timed_out = output, rc, timed_out
        self.apps = CORPUS_APPS if apps is None else apps
        self.enumerator_rc = enumerator_rc
        self.calls = []

    def __call__(self, argv, **kw):
        self.calls.append(argv)
        if any("corpus-app-dirs" in a for a in argv):
            return pf.Ran(rc=self.enumerator_rc,
                          out="".join(a + "\n" for a in self.apps), err="")
        return pf.Ran(rc=self.rc, out=self.output, err="", timed_out=self.timed_out)


def corpus_result(output, **kw):
    script = CorpusScript(output, **kw)
    root = corpus_repo()
    saved = pf.run
    pf.run = script
    try:
        return pf.check_corpus(root, True), script
    finally:
        pf.run = saved
        shutil.rmtree(root, ignore_errors=True)


# ---- the positive: a clean run PASSes and REPORTS what it counted
#
# It no longer asserts a number against a checked-in one, so the claim is
# narrower and is stated as such: the run was clean, and here is what it ran.
# What must not happen is a PASS that names no count at all -- then nobody can
# tell this box's result from another's (#3737).
_full = corpus_output({"al-language": 12,
                       "al-language-internals-fixture": 0,
                       "al-language-onprem": 3}, oos=2, known_gap=1)
_res, _script = corpus_result(_full)
check("a clean corpus run PASSes",
      _res.status == "PASS", f"{_res.status}: {_res.summary}")
check("...and the summary states the count it observed, not just 'ok'",
      "15" in _res.summary or any("15" in d for d in _res.detail),
      f"{_res.summary} {_res.detail}")
check("...and names the corpus it measured, since nothing in the tree records it",
      "corpus" in (_res.summary + " ".join(_res.detail)).lower(),
      f"{_res.summary} {_res.detail}")
check("expectation-reclassified passes -- PASS (oos) / PASS (known-gap) -- still count",
      _res.data.get("observed", {}).get("al-language") == 12, _res.data)

# ---- the invocation itself: the flags used to be fabricated (#3357 defect 1)
_argv = [a for a in _script.calls if "dotnet" in a[0]][0]
check("the runner is invoked with the bundle paths POSITIONALLY",
      all(app in _argv for app in CORPUS_APPS), _argv)
check("there is no invented `test` subcommand or `--bundle` flag",
      "--bundle" not in _argv and "test" not in _argv, _argv)
check("the corpus apps are enumerated, never hardcoded to one path",
      any("corpus-app-dirs" in a for c in _script.calls for a in c), _script.calls)
check("the SHARED package caches are used -- a private one is blind to this failure",
      _argv.count("--package-cache") == 2, _argv)
check("--count-baseline is gone: the corpus suites are no longer declared in it",
      "--count-baseline" not in _argv, _argv)

# ---- a failing test is a FAIL even when the counts add up
_failing = corpus_output({"al-language": 11,
                          "al-language-internals-fixture": 0,
                          "al-language-onprem": 3}, fail=1)
_res, _ = corpus_result(_failing, rc=1)
check("a corpus test that FAILED is a FAIL", _res.status == "FAIL", _res.summary)

# ---- absence of evidence is never a pass
_res, _ = corpus_result("al-runner — running 3 bundle(s)\n")
check("a run that printed no summary FAILs rather than passing on the exit code",
      _res.status == "FAIL", f"{_res.status}: {_res.summary}")
check("...and says the run was not verified rather than reporting 0 passes as fact",
      "summary" in (_res.summary + " ".join(_res.detail)).lower(),
      f"{_res.summary} {_res.detail}")

_res, _ = corpus_result("", timed_out=True)
check("a timeout is a FAIL of its own", _res.status == "FAIL", _res.summary)
check("...and is not reported as a count mismatch",
      "timed out" in (_res.summary + " ".join(_res.detail)).lower(),
      f"{_res.summary} {_res.detail}")

# ---- a lost suite is a FAIL even when every surviving test passed
_lost = corpus_output({"al-language": 12,
                       "al-language-internals-fixture": 0,
                       "al-language-onprem": 3}, suite_errors=("al-language",))
_res, _ = corpus_result(_lost)
check("a bundle that lost a suite FAILs even with the counts intact",
      _res.status == "FAIL", f"{_res.status}: {_res.summary}")

# ---- the exit code may add a FAIL, never grant a PASS
_res, _ = corpus_result(_full, rc=4)
check("a non-zero exit still FAILs when every number checks out",
      _res.status == "FAIL", f"{_res.status}: {_res.summary}")
check("...and says the exit code is what disagreed", "exit" in _res.summary.lower()
      or any("exit" in d.lower() for d in _res.detail), f"{_res.summary} {_res.detail}")

# ---- and the summary's own total is cross-checked against the per-bundle lines
_lying = corpus_output({"al-language": 12,
                        "al-language-internals-fixture": 0,
                        "al-language-onprem": 3}, reported_pass=15 + 4)
_res, _ = corpus_result(_lying)
check("a summary that disagrees with the per-bundle PASS lines FAILs",
      _res.status == "FAIL", f"{_res.status}: {_res.summary}")

_res, _ = corpus_result(_full, apps=[])
check("an enumerator that produced no apps FAILs -- a run of nothing is not a baseline",
      _res.status == "FAIL", f"{_res.status}: {_res.summary}")

# ---- a summary that parsed but LOST a key is unreadable, not "zero failures"
#
# #3361 part 2. The guard used to read `if summary.get("fail") or
# summary.get("error")`, so a summary block without those lines answered False --
# the success direction -- for a question it had not measured.
#
# The issue and its reviewer both judged this unreachable, backstopped by the
# `summary.get("pass") != counted` comparison below it, where a None FAILs
# loudly. That backstop does not cover this shape: `pass` is still present and
# still agrees with the per-bundle PASS lines, so the run goes green on a summary
# that never said whether anything failed.
_no_fail_key = corpus_output({"al-language": 12,
                              "al-language-internals-fixture": 0,
                              "al-language-onprem": 3}, drop_fields=("fail",))
check("the fixture really does lose the key -- otherwise the check below proves nothing",
      "fail" not in (pf.parse_corpus_run(_no_fail_key).summary or {"fail": 0}),
      str(pf.parse_corpus_run(_no_fail_key).summary))
_res, _ = corpus_result(_no_fail_key)
check("a summary missing `fail` FAILs rather than reading as zero failures",
      _res.status == "FAIL", f"{_res.status}: {_res.summary}")
check("...and says the count is MISSING, not that it counted zero",
      "did not report" in (_res.summary + " ".join(_res.detail)).lower(),
      f"{_res.summary} {_res.detail}")
check("...and names which key was absent, so the remedy is not a guess",
      "fail" in _res.summary.lower(), _res.summary)

_no_error_key = corpus_output({"al-language": 12,
                               "al-language-internals-fixture": 0,
                               "al-language-onprem": 3}, drop_fields=("error",))
check("a summary missing `error` FAILs the same way -- the sibling key, same shape",
      corpus_result(_no_error_key)[0].status == "FAIL",
      corpus_result(_no_error_key)[0].summary)

# The constraint from guards-need-a-third-state.md: only an UNMEASURABLE thing
# becomes the third state. A summary that reports zero failures explicitly is a
# legitimate pass and must stay one, or the fix has traded a false green for a
# false red.
check("a summary reporting `fail: 0` explicitly still PASSes -- zero measured is not zero missing",
      corpus_result(_full)[0].status == "PASS", corpus_result(_full)[0].summary)

# ---- SKIP stays honest, and still points at the file it is about
_res = pf.check_corpus(corpus_repo(), False)
check("the default is still SKIP, never folded into the passing count",
      _res.status == "SKIP", _res.status)


# --------------------------------------------------------------------------
# which corpus is checked out here (#3737)
# --------------------------------------------------------------------------
# The pin is gone, and with it the hazard this section used to cover: a submodule
# working directory shared by every worktree, which one process could silently
# move for all of them (#3404). Each worktree now has its own clone, so there is
# nothing left to share.
#
# What the check answers instead is WHICH corpus this worktree holds. Nothing in
# the tree records that any more, so a PASS naming no SHA would be a green tick
# with the answer missing -- classification is asserted against captured readings,
# and the gatherer end-to-end against real directories.
print()
print("preflight.py -- corpus checkout")

_SHA = "af01bbbc176b4cc5ca3d7020094103911af51a35"


def checkout_reading(state="ok", sha=_SHA, error=""):
    return {"state": state, "sha": sha if state == "ok" else None,
            "path": "tests/al-language", "error": error}


_res = pf.classify_corpus_checkout(checkout_reading())
check("a corpus clone PASSes", _res.status == "PASS", f"{_res.status}: {_res.summary}")
check("...and the summary NAMES the commit, which is the whole point of the check",
      _SHA in _res.summary, _res.summary)
check("...and points at the tool that moves it",
      "corpus-checkout.py" in (_res.summary + _res.command + " ".join(_res.detail)),
      f"{_res.summary} {_res.command} {_res.detail}")

_res = pf.classify_corpus_checkout(checkout_reading("absent"))
check("no corpus checked out is a WARN, not a FAIL -- one command fixes it",
      _res.status == "WARN", f"{_res.status}: {_res.summary}")
check("...and it never claims a SHA it does not have",
      _SHA not in _res.summary, _res.summary)
check("...and says a fresh worktree is the ordinary way to get here",
      any("fresh worktree" in d for d in _res.detail), _res.detail)

_res = pf.classify_corpus_checkout(checkout_reading("submodule-leftover"))
check("the pre-#3737 submodule checkout WARNs", _res.status == "WARN",
      f"{_res.status}: {_res.summary}")
check("...and the remedy is the removal, spelled out",
      "rm -rf tests/al-language" in (_res.remedy or ""), _res.remedy)
check("...and it says why no tool does it for you",
      any("every worktree shares" in d for d in _res.detail), _res.detail)

_res = pf.classify_corpus_checkout(checkout_reading("unreadable", error="bad object"))
check("a corpus whose commit cannot be read WARNs rather than passing",
      _res.status == "WARN", f"{_res.status}: {_res.summary}")
check("...and quotes what git said", "bad object" in _res.summary, _res.summary)

# ---- the gatherer, against real directories on disk
_co_tmp = tempfile.mkdtemp()
try:
    def _g(root, *a):
        return subprocess.run(["git", "-C", root, *a], capture_output=True,
                              text=True).stdout.strip()

    _repo = os.path.join(_co_tmp, "runner")
    os.makedirs(os.path.join(_repo, "tests"))
    check("a worktree with no corpus directory reads as absent",
          pf.corpus_checkout_reading(_repo)["state"] == "absent",
          pf.corpus_checkout_reading(_repo))

    _corpus = os.path.join(_repo, "tests", "al-language")
    os.makedirs(_corpus)
    check("an EMPTY corpus directory is absent, not unreadable",
          pf.corpus_checkout_reading(_repo)["state"] == "absent",
          pf.corpus_checkout_reading(_repo))

    subprocess.run(["git", "init", "-q", "-b", "master", _corpus], check=True,
                   capture_output=True)
    _g(_corpus, "config", "user.email", "t@example.invalid")
    _g(_corpus, "config", "user.name", "T")
    open(os.path.join(_corpus, "a"), "w").write("1\n")
    _g(_corpus, "add", "a")
    _g(_corpus, "commit", "-qm", "one")
    _head = _g(_corpus, "rev-parse", "HEAD")
    _read = pf.corpus_checkout_reading(_repo)
    check("a real clone reads as ok with its own HEAD",
          _read["state"] == "ok" and _read["sha"] == _head, f"{_read} {_head}")

    # The migration state: a `.git` FILE, pointing into the superproject.
    _left = os.path.join(_co_tmp, "left")
    os.makedirs(os.path.join(_left, "tests", "al-language"))
    with open(os.path.join(_left, "tests", "al-language", ".git"), "w") as fh:
        fh.write("gitdir: ../../.git/modules/tests/al-language\n")
    check("a .git FILE reads as the submodule leftover, never as a clone",
          pf.corpus_checkout_reading(_left)["state"] == "submodule-leftover",
          pf.corpus_checkout_reading(_left))
finally:
    shutil.rmtree(_co_tmp, ignore_errors=True)


# -------------------------------------------------- a branch that already heads
# someone else's pull request (#3707)
#
# Two loops can hold one identity, and a worktree path built from the identity
# alone renders the same directory for both, so one loop's commits land on the
# other's PR branch. The `agent:` label on the OPEN pull request heading this
# branch is the one signal that names a loop; the assignee names an ACCOUNT, and
# several loops share one (check-open-prs-before-claiming.md). Across accounts it
# does discriminate, which is why this check reads the branch prefix.

def _pr(number, labels, state="OPEN"):
    return {"number": number, "state": state,
            "labels": [{"name": n} for n in labels]}


_WT = "/repo/BusinessCentral.AL.Runner/.claude/worktrees/fbk-3-issue-3707"
_MAIN = "/repo/BusinessCentral.AL.Runner"

_foreign = pf.judge_branch_ownership(cwd=_WT, branch="agent/fbk-3/issue-3707",
                                     pr=_pr(3742, ["agent: fbk-7"]), agent_id="fbk-3")
check("a branch heading another loop's open PR is a FAIL",
      _foreign.status == "FAIL", _foreign.summary)
check("...and the refusal names the pull request and the label that owns it",
      "3742" in _foreign.summary and "fbk-7" in _foreign.summary, _foreign.summary)

_own = pf.judge_branch_ownership(cwd=_WT, branch="agent/fbk-3/issue-3707",
                                 pr=_pr(3742, ["agent: fbk-3"]), agent_id="fbk-3")
check("your own open PR is a PASS", _own.status == "PASS", _own.summary)

_none = pf.judge_branch_ownership(cwd=_WT, branch="agent/fbk-3/issue-3707",
                                  pr=None, agent_id="fbk-3")
check("no open PR on the branch is a PASS", _none.status == "PASS", _none.summary)

_closed = pf.judge_branch_ownership(cwd=_WT, branch="agent/fbk-9/issue-1",
                                    pr=_pr(1, ["agent: fbk-7"], state="MERGED"),
                                    agent_id="fbk-3")
check("a MERGED PR by another loop does not refuse", _closed.status == "PASS",
      _closed.summary)

_outside = pf.judge_branch_ownership(cwd=_MAIN, branch="main",
                                     pr=_pr(3742, ["agent: fbk-7"]), agent_id="fbk-3")
check("the main checkout is never refused, whatever PR exists",
      _outside.status == "PASS", _outside.summary)

# The third state: distinct from PASS, and it says which of the three it is
# (guards-need-a-third-state.md). Neither of these may resolve toward success.
_noid = pf.judge_branch_ownership(cwd=_WT, branch="agent/fbk-3/issue-3707",
                                  pr=_pr(3742, ["agent: fbk-7"]), agent_id=None)
check("no identity to compare against is undetermined, not a PASS",
      _noid.status == "WARN", _noid.summary)
check("...and it names the flag that would settle it",
      "--agent-id" in (_noid.remedy + _noid.summary), _noid.remedy)

_nolabel = pf.judge_branch_ownership(cwd=_WT, branch="agent/fbk-3/issue-3707",
                                     pr=_pr(3742, []), agent_id="fbk-3")
check("an open PR carrying no agent: label is undetermined, not a PASS",
      _nolabel.status == "WARN", _nolabel.summary)

# The lookup itself failing must not read as "the branch is free" -- an
# unauthenticated gh, or a repository whose open PRs could not be listed, is the
# third state and not a PASS.
_blind = pf.judge_branch_ownership(cwd=_WT, branch="agent/fbk-3/issue-3707", pr=None,
                                   agent_id="fbk-3", lookup_status="gh is not installed")
check("an unreadable pull-request list is undetermined, not a PASS",
      _blind.status == "WARN", _blind.summary)
check("...and it says the list could not be read", "could not be read" in _blind.summary,
      _blind.summary)

_detached = pf.judge_branch_ownership(cwd=_WT, branch=None, pr=None, agent_id="fbk-3")
check("a detached HEAD in a worktree is undetermined, not a PASS",
      _detached.status == "WARN", _detached.summary)

# -------------------------------------------------- an INHERITED foreign worktree cwd
# (#4340)
#
# A dispatched agent inherits the coordinator's shell cwd, and three times in one
# session that cwd was another identity's live worktree
# (.claude/worktrees/stma-auto2-6-issue-4289, on open PR #4318's branch). The third
# instance was a RESUMED agent picking the value back out of its own transcript, which
# is why a coordinator-side reset cannot be the defence.
#
# Every case above happens to stand in a worktree whose name carries its own identity,
# so the directory NAME was never a signal here -- ownership was decided entirely by a
# network lookup of the branch's pull request. That leaves the defect: with no open PR
# on the branch (merged, closed, or never opened), standing in a foreign identity's
# worktree returned PASS.
#
# The directory name is conclusive on its own and costs no network call:
# `.claude/worktrees/stma-auto2-6-issue-4289` is not stma-auto-1's, whatever any API
# says. So it is judged BEFORE the pull-request lookup, and an unparseable name is the
# third state rather than a pass (`guards-need-a-third-state.md`: unknown means refuse,
# not proceed -- the trap being that a guard keyed on a list silently skips what it does
# not recognise).

_FOREIGN_WT = "/repo/BusinessCentral.AL.Runner/.claude/worktrees/stma-auto2-6-issue-4289"

# The reported shape: no open PR heads the branch, so the lookup says nothing.
_inherited = pf.judge_branch_ownership(cwd=_FOREIGN_WT,
                                       branch="agent/stma-auto2-6/issue-4289",
                                       pr=None, agent_id="stma-auto-1")
check("a foreign identity's worktree is a FAIL even with no open PR on the branch",
      _inherited.status == "FAIL", _inherited.summary)
check("...and the refusal names the identity the directory belongs to",
      "stma-auto2-6" in _inherited.summary, _inherited.summary)

# The same directory while standing on a branch that IS the agent's own. The cwd is
# still wrong: writes land in another loop's tree whatever HEAD says.
_inherited_own_branch = pf.judge_branch_ownership(
    cwd=_FOREIGN_WT, branch="agent/stma-auto-1/issue-4340", pr=None,
    agent_id="stma-auto-1")
check("a foreign worktree is a FAIL even when the branch checked out there is yours",
      _inherited_own_branch.status == "FAIL", _inherited_own_branch.summary)

# The lookup being unreadable must not downgrade a refusal the directory name already
# settled: the name is local evidence and needs no network.
_inherited_blind = pf.judge_branch_ownership(
    cwd=_FOREIGN_WT, branch="agent/stma-auto2-6/issue-4289", pr=None,
    agent_id="stma-auto-1", lookup_status="gh is not installed")
check("an unreadable PR list does not downgrade a foreign-worktree refusal",
      _inherited_blind.status == "FAIL", _inherited_blind.summary)

# A detached HEAD is undetermined for the BRANCH question, but the directory name is
# still conclusive, so the foreign cwd must win.
_inherited_detached = pf.judge_branch_ownership(
    cwd=_FOREIGN_WT, branch=None, pr=None, agent_id="stma-auto-1")
check("a foreign worktree with a detached HEAD is a FAIL, not merely undetermined",
      _inherited_detached.status == "FAIL", _inherited_detached.summary)

# An agent's cwd is usually a SUBDIRECTORY of its worktree, not the root -- which is what
# `worktree_identity` takes the segment after `worktrees` for, rather than the last one.
# Nothing here stood anywhere but a root until #4340's review, and a mutation reading the
# last segment instead passed the whole suite while degrading a foreign SUBDIRECTORY from
# FAIL to WARN: correct at the root, laundering one directory down. That is the shape the
# third state exists to refuse, so it is pinned at both depths.
_FOREIGN_SUBDIR = _FOREIGN_WT + "/AlRunner/Patches"

_inherited_subdir = pf.judge_branch_ownership(
    cwd=_FOREIGN_SUBDIR, branch="agent/stma-auto2-6/issue-4289", pr=None,
    agent_id="stma-auto-1")
check("a SUBDIRECTORY of a foreign worktree is a FAIL, not merely undetermined",
      _inherited_subdir.status == "FAIL", _inherited_subdir.summary)
check("...and it names the identity the enclosing worktree belongs to",
      "stma-auto2-6" in _inherited_subdir.summary, _inherited_subdir.summary)

_win_subdir = pf.judge_branch_ownership(
    cwd=r"C:\repo\.claude\worktrees\stma-auto2-6-issue-4289\AlRunner\Patches",
    branch="agent/stma-auto2-6/issue-4289", pr=None, agent_id="stma-auto-1")
check("a foreign worktree subdirectory is refused with Windows separators too",
      _win_subdir.status == "FAIL", _win_subdir.summary)

# ---- and the directions it must NOT fire in, so the check discriminates rather than
# ---- simply refusing everything.

# The control for the two arms above: standing one directory down inside YOUR OWN worktree
# is the ordinary case and must stay a PASS. Without it, a guard that simply refused every
# subdirectory would satisfy the FAIL arms.
_own_subdir = pf.judge_branch_ownership(
    cwd="/repo/BusinessCentral.AL.Runner/.claude/worktrees/stma-auto-1-issue-4340/AlRunner/Patches",
    branch="agent/stma-auto-1/issue-4340", pr=None, agent_id="stma-auto-1")
check("a subdirectory of your OWN worktree is still a PASS",
      _own_subdir.status == "PASS", _own_subdir.summary)

_own_wt = pf.judge_branch_ownership(
    cwd="/repo/BusinessCentral.AL.Runner/.claude/worktrees/stma-auto-1-issue-4340",
    branch="agent/stma-auto-1/issue-4340", pr=None, agent_id="stma-auto-1")
check("your own worktree is still a PASS", _own_wt.status == "PASS", _own_wt.summary)

# An identity is a prefix of another one. `stma-auto-1` must not be read as owning
# `stma-auto-12-issue-9`, and the segment boundary is what decides it.
_prefix = pf.judge_branch_ownership(
    cwd="/repo/BusinessCentral.AL.Runner/.claude/worktrees/stma-auto-12-issue-9",
    branch="agent/stma-auto-12/issue-9", pr=None, agent_id="stma-auto-1")
check("an identity that is only a PREFIX of the directory's does not own it",
      _prefix.status == "FAIL", _prefix.summary)

# ...and the converse: the longer identity standing in its own directory is fine.
_prefix_own = pf.judge_branch_ownership(
    cwd="/repo/BusinessCentral.AL.Runner/.claude/worktrees/stma-auto-12-issue-9",
    branch="agent/stma-auto-12/issue-9", pr=None, agent_id="stma-auto-12")
check("the longer identity owns its own directory", _prefix_own.status == "PASS",
      _prefix_own.summary)

# Windows separators reach this function too -- WORKTREE_DIR already accepts both.
_win = pf.judge_branch_ownership(
    cwd=r"C:\repo\.claude\worktrees\stma-auto2-6-issue-4289",
    branch="agent/stma-auto2-6/issue-4289", pr=None, agent_id="stma-auto-1")
check("a foreign worktree is refused with Windows separators too",
      _win.status == "FAIL", _win.summary)

# The third state: a worktree directory whose name does not carry an identity at all
# cannot be attributed, so it must not read as "yours". A guard that skips what it does
# not recognise silently restores the defect.
_unparseable = pf.judge_branch_ownership(
    cwd="/repo/BusinessCentral.AL.Runner/.claude/worktrees/scratch",
    branch="agent/stma-auto-1/issue-4340", pr=None, agent_id="stma-auto-1")
check("a worktree name carrying no identity is undetermined, not a PASS",
      _unparseable.status == "WARN", _unparseable.summary)

# With no identity declared, the directory cannot be attributed either -- the existing
# WARN for that case must not become a silent pass just because a name is present.
_noid_wt = pf.judge_branch_ownership(
    cwd=_FOREIGN_WT, branch="agent/stma-auto2-6/issue-4289", pr=None, agent_id=None)
check("no identity declared is undetermined for the directory too, not a PASS",
      _noid_wt.status == "WARN", _noid_wt.summary)

# End to end against a real repository: the branch has to be READ, and reading it
# from the wrong directory is exactly the mistake this check exists to catch.
_own_tmp = tempfile.mkdtemp(prefix="preflight-ownership-")
try:
    subprocess.run(["git", "init", "-q", "-b", "main", _own_tmp], check=True,
                   capture_output=True)
    _g(_own_tmp, "config", "user.email", "t@example.invalid")
    _g(_own_tmp, "config", "user.name", "T")
    open(os.path.join(_own_tmp, "F"), "w").write("x")
    _g(_own_tmp, "add", "F")
    _g(_own_tmp, "commit", "-qm", "init")
    _wt_path = os.path.join(_own_tmp, ".claude", "worktrees", "fbk-9-issue-77")
    _g(_own_tmp, "worktree", "add", "-q", "-b", "agent/fbk-9/issue-77", _wt_path)

    _asked = []

    def _lookup(branch):
        _asked.append(branch)
        return (_pr(4242, ["agent: fbk-1"]), "ok")

    _res = pf.check_branch_ownership(_own_tmp, agent_id="fbk-9", cwd=_wt_path,
                                     lookup=_lookup)
    check("the real worktree's branch is read and refused for the right PR",
          _res.status == "FAIL" and "4242" in _res.summary, _res.summary)
    check("...and the pull request was asked for BY BRANCH, not looked up in a window",
          _asked == ["agent/fbk-9/issue-77"], str(_asked))
    # Owning the PR is NOT a licence to stand in another identity's worktree (#4340).
    # #3742 wrote this case as a PASS because the only question then was whose PR the
    # BRANCH heads; the directory was incidental fixture reuse. Both questions must now
    # hold, and fbk-1 writing in fbk-9's tree is the cross-worktree write #3014 describes
    # whoever owns the pull request -- a commit made from here goes onto fbk-9's branch.
    _res_ok = pf.check_branch_ownership(_own_tmp, agent_id="fbk-1", cwd=_wt_path,
                                        lookup=_lookup)
    check("...but owning that PR does not license standing in another identity's worktree",
          _res_ok.status == "FAIL", _res_ok.summary)
    check("...and that refusal is about the DIRECTORY, naming whose it is",
          "fbk-9" in _res_ok.summary and "worktrees" in _res_ok.summary,
          _res_ok.summary)
    # The loop whose worktree it is, standing in it with its own PR, is the real PASS.
    _res_home = pf.check_branch_ownership(
        _own_tmp, agent_id="fbk-9", cwd=_wt_path,
        lookup=lambda b: (_pr(4242, ["agent: fbk-9"]), "ok"))
    check("...while the loop whose worktree it is, on its own PR, is a PASS",
          _res_home.status == "PASS", _res_home.summary)
    _res_main = pf.check_branch_ownership(_own_tmp, agent_id="fbk-9", cwd=_own_tmp,
                                          lookup=_lookup)
    check("...while the main checkout of the same repository is a PASS",
          _res_main.status == "PASS", _res_main.summary)
    _res_blind = pf.check_branch_ownership(
        _own_tmp, agent_id="fbk-9", cwd=_wt_path,
        lookup=lambda b: (None, "gh pr list failed: network unreachable"))
    check("...and a failed lookup from the worktree is a WARN, never a PASS",
          _res_blind.status == "WARN", _res_blind.summary)
finally:
    shutil.rmtree(_own_tmp, ignore_errors=True)

# -------------------------------------------------- BEGIN agent-id-wiring (#3746)
# The identity the ownership check needs has to be IN the documented invocation.
#
# judge_branch_ownership can only reach its FAIL row when the run declared an
# identity; a `--agent-id` nothing passes leaves the check producing the WARN row
# from every worktree, which is the state #3742 shipped in. The identity travels
# as an ARGUMENT rather than an exported AL_RUNNER_AGENT_ID because shell state
# does not survive between an agent's tool calls (impl-agent.md), so an `export`
# is gone by the next command and the check drops back to WARN without saying so.

_DOCUMENTED_INVOCATION = re.compile(
    r"tools/preflight\.py(?:\s+--\S+)*\s+--agent-id\s+<AGENT-ID>")

for _doc in (".claude/skills/autonomous-cycle/SKILL.md",
             ".claude/skills/orchestrating-a-session/SKILL.md",
             ".claude/agents/impl-agent.md"):
    # utf-8 explicitly: these files carry em-dashes, and the Windows default
    # codec turns reading one into a failure that looks like a drifted document.
    with open(os.path.join(os.path.dirname(HERE), _doc), encoding="utf-8") as _fh:
        _text = _fh.read()
    check(f"{_doc} documents the preflight run WITH --agent-id",
          bool(_DOCUMENTED_INVOCATION.search(_text)),
          "no `tools/preflight.py [flags] --agent-id <AGENT-ID>` invocation in this file")

# The other direction, so a rename cannot leave three documents pointing at a
# flag that no longer exists: ask preflight's own parser, not the source text.
_help_out = io.StringIO()
_help_rc = None
_saved_stdout, sys.stdout = sys.stdout, _help_out
try:
    pf.main(["--help"])
except SystemExit as _exc:
    _help_rc = _exc.code
finally:
    sys.stdout = _saved_stdout
check("...and --agent-id is still a real flag on preflight.py itself",
      _help_rc == 0 and "--agent-id" in _help_out.getvalue(),
      f"rc={_help_rc} help={_help_out.getvalue()[:160]!r}")
# -------------------------------------------------- END agent-id-wiring (#3746)


# -------------------------------------------------- artifacts probe (#3878)
# A partially-provisioned artifact directory used to be indistinguishable from a
# complete one until a consumer failed deep inside an assembly load. preflight
# had no artifacts check at all (measured: 0 occurrences of "artifact" in a
# 154 KB file), so the breakage was found mid-measurement, three times in one
# session, rather than at cycle start.
#
# The classification is proven in C# (AlRunner.Tests/ArtifactDirStateTests.cs)
# against the real closure predicate. What is proven HERE is the disposition:
# which status each reading maps to, including the third state.

def _art(status, broken=(), total=0, error=""):
    return {"status": status, "broken": list(broken), "total": total, "error": error}


check("artifacts: a root where every directory is complete PASSes",
      pf.classify_artifacts(_art("ok", total=11)).status == "PASS",
      f"got {pf.classify_artifacts(_art('ok', total=11)).status}")

# The constraint from guards-need-a-third-state.md: a genuinely absent root is a
# legitimate state, not a broken measurement. A box that has provisioned nothing
# is fine; refusing it would swap a false green for a false red.
_absent = pf.classify_artifacts(_art("absent"))
check("artifacts: no artifacts root at all is a PASS, not a failure",
      _absent.status == "PASS", f"got {_absent.status}: {_absent.summary}")

# The issue's subject: broken directories are named, so nobody re-derives which.
_partial = pf.classify_artifacts(_art("partial", broken=["27.5.46862.48827", "28.0.46665.54452"], total=13))
check("artifacts: a partially-provisioned directory WARNs",
      _partial.status == "WARN", f"got {_partial.status}")
check("artifacts: ...and names every broken directory in the summary or detail",
      all(d in (_partial.summary + " " + " ".join(_partial.detail))
          for d in ("27.5.46862.48827", "28.0.46665.54452")),
      f"summary={_partial.summary!r} detail={_partial.detail!r}")
check("artifacts: ...and does not propose deleting them",
      "rm -rf" not in _partial.remedy and "rm -rf" not in " ".join(_partial.detail),
      f"remedy={_partial.remedy!r}")

# The third state, proven to fire: a root that could not be read is NOT reported
# as the success state and NOT folded into "absent" (whose remedy is `provision`,
# which cannot fix an unreadable path).
_unreadable = pf.classify_artifacts(_art("unreadable", error="permission denied"))
check("artifacts: an unreadable root is neither PASS nor the absent case",
      _unreadable.status == "WARN" and "permission denied" in
      (_unreadable.summary + " " + " ".join(_unreadable.detail)),
      f"got {_unreadable.status}: {_unreadable.summary!r}")
check("artifacts: ...and says the measurement failed rather than that nothing is provisioned",
      _unreadable.summary != _absent.summary,
      "the unreadable and absent summaries are identical, so the third state is spelled as row 1")

# An artifacts root that EXISTS but holds no version directory used to summarise as
# "0 artifact directories, all holding a complete engine closure" -- vacuously true and
# misleading. It is still a PASS (nothing is broken), but it must say what it found.
_empty_root = pf.classify_artifacts(_art("ok", total=0))
check("artifacts: an existing-but-empty root PASSes",
      _empty_root.status == "PASS", f"got {_empty_root.status}")
check("artifacts: ...without claiming 0 directories are 'all complete'",
      "all holding" not in _empty_root.summary,
      f"summary reads {_empty_root.summary!r}")

# -------------------------------------------------- .NET SDK toolchain (#4200)
# preflight's stated job is "is this box fit to run an autonomous cycle?", and it
# answered yes on a box with no .NET SDK at all -- where `dotnet build`, `dotnet
# test` and artifact provisioning each exit 127. The verdicts below are proven
# against captured `dotnet --list-sdks` output, never against whatever SDK the box
# running these tests happens to have; a suite that reads the live toolchain
# passes for the wrong reason on a healthy box and cannot be made to fail.

# Captured verbatim from `dotnet --list-sdks` on the reporting box (8.0 only) and
# on a box that can build the solution.
SDKS_8_ONLY = "8.0.425 [/usr/share/dotnet/sdk]\n"
SDKS_MIXED = "8.0.130 [/usr/share/dotnet/sdk]\n10.0.111 [/usr/share/dotnet/sdk]\n"


def _tc(status, sdks=(), error="", solution="AlRunner.slnx", floor=9, off_path=""):
    return {"status": status, "sdks": list(sdks), "error": error, "off_path_dir": off_path,
            "solution": solution, "floor": floor}


print("preflight.py -- .NET SDK toolchain (#4200)")

# Verdict 0: an SDK that can build the solution.
_ok = pf.classify_toolchain(_tc("ok", sdks=["8.0.130", "10.0.111"]))
check("toolchain: an SDK new enough for the .slnx PASSes",
      _ok.status == "PASS", f"got {_ok.status}: {_ok.summary}")
check("toolchain: ...and names the SDK versions it found, so the answer is attributable",
      "10.0.111" in (_ok.summary + " " + " ".join(_ok.detail)),
      f"summary={_ok.summary!r} detail={_ok.detail!r}")

# Verdict 1: no SDK at all. This is a genuine FAIL, NOT the artifacts check's
# "genuinely absent stays a pass" -- guards-need-a-third-state.md's constraint
# turns on whether an in-repo remedy exists, and for a missing SDK none does.
_none = pf.classify_toolchain(_tc("absent"))
check("toolchain: no .NET SDK at all is a FAIL, not a legitimate-absent PASS",
      _none.status == "FAIL", f"got {_none.status}: {_none.summary}")
check("toolchain: ...and the remedy is installing an SDK, not an in-repo command",
      "install" in (_none.remedy + " " + " ".join(_none.detail)).lower(),
      f"remedy={_none.remedy!r}")

# Verdict 3 -- the third state, proven to fire. An SDK IS present, so this is not
# the absent case; it cannot build the solution FORMAT, so it is not the success
# state either. Spelling it as either one is the defect this check exists to stop:
# 8.0.x alone gives MSB4068 on a <Solution> element, naming neither the SDK nor
# the solution format.
_too_old = pf.classify_toolchain(_tc("too-old", sdks=["8.0.425"]))
check("toolchain: an SDK present but too old for the .slnx is neither PASS nor the absent case",
      _too_old.status == "FAIL" and _too_old.summary != _none.summary,
      f"got {_too_old.status}: {_too_old.summary!r} vs absent {_none.summary!r}")
check("toolchain: ...and names the SDK it DID find, so a typo is visible rather than inferred",
      "8.0.425" in (_too_old.summary + " " + " ".join(_too_old.detail)),
      f"summary={_too_old.summary!r} detail={_too_old.detail!r}")
check("toolchain: ...and names MSB4068, the error the box would otherwise hit",
      "MSB4068" in (_too_old.summary + " " + " ".join(_too_old.detail) + " " + _too_old.remedy),
      f"detail={_too_old.detail!r}")
check("toolchain: ...and names the solution format rather than only the SDK version",
      ".slnx" in (_too_old.summary + " " + " ".join(_too_old.detail) + " " + _too_old.remedy),
      f"detail={_too_old.detail!r}")

# The fourth outcome, distinct from all three: `dotnet` exists and could not be
# ASKED. Folding it into "absent" would send the reader to an install that is
# already done; folding it into PASS is the shape the whole rule forbids.
_unreadable = pf.classify_toolchain(_tc("unreadable", error="dotnet --list-sdks exited 134"))
check("toolchain: an SDK list that could not be read is neither PASS nor absent",
      _unreadable.status == "FAIL" and _unreadable.summary not in (_none.summary, _ok.summary),
      f"got {_unreadable.status}: {_unreadable.summary!r}")
check("toolchain: ...and says the measurement failed rather than that no SDK exists",
      "dotnet --list-sdks exited 134" in
      (_unreadable.summary + " " + " ".join(_unreadable.detail)),
      f"summary={_unreadable.summary!r}")


# ------------------------------------- an SDK installed but off PATH (#4299)
# The FIFTH outcome, and the false RED on the other side of #4200's false green.
# `shutil.which("dotnet")` answers "is dotnet resolvable on PATH right now"; the
# absent branch reported it as "there is no SDK here". On the box #4299 was filed
# from those diverged -- 8.0.425 and 9.0.318 under /root/.dotnet, reported as
# "no .NET SDK on this box", with the whole suite building after one export.
#
# Every check below is against a synthetic reading or an injected filesystem, for
# the reason the rest of this section is: a suite that reads the live box cannot
# fail on the box the defect was filed from.
_off = pf.classify_toolchain(_tc("ok", sdks=["8.0.425", "9.0.318"], off_path="/root/.dotnet"))
check("toolchain: an SDK that is installed but off PATH is NOT the absent FAIL",
      _off.status != "FAIL" and _off.summary != _none.summary,
      f"got {_off.status}: {_off.summary!r}")
check("toolchain: ...it WARNs, so the cycle continues at exit 0 and --strict still sees it",
      _off.status == "WARN", f"got {_off.status}")
check("toolchain: ...and names the directory the SDK was found in",
      "/root/.dotnet" in _off.summary, f"summary={_off.summary!r}")
check("toolchain: ...and the remedy is the export, not an install",
      _off.remedy == 'export PATH="/root/.dotnet:$PATH"' and "download" not in _off.remedy,
      f"remedy={_off.remedy!r}")
check("toolchain: ...and says the SDK is present, contradicting the message it used to print",
      "not 'no SDK is installed'" in " ".join(_off.detail).replace('"', "'"),
      f"detail={_off.detail!r}")
check("toolchain: ...and says the caller's own shell is still unrepaired",
      any("exits 127" in d for d in _off.detail), f"detail={_off.detail!r}")
check("toolchain: ...and is a DIFFERENT verdict from an SDK that is on PATH",
      (_off.status, _off.summary) != (_ok.status, _ok.summary),
      f"off={_off.summary!r} on-path={_ok.summary!r}")

# The constraint that must not be traded away. An off-PATH SDK is excused for
# being off PATH and for nothing else: if it cannot build the solution, or could
# not be asked, finding it on disk must not launder the FAIL into a WARN.
_off_old = pf.classify_toolchain(_tc("too-old", sdks=["8.0.425"], off_path="/root/.dotnet"))
check("toolchain: an off-PATH SDK that is TOO OLD is still a FAIL, not laundered to WARN",
      _off_old.status == "FAIL", f"got {_off_old.status}: {_off_old.summary!r}")
_off_bad = pf.classify_toolchain(_tc("unreadable", error="exited 134", off_path="/root/.dotnet"))
check("toolchain: an off-PATH SDK that could not be READ is still a FAIL",
      _off_bad.status == "FAIL", f"got {_off_bad.status}: {_off_bad.summary!r}")
check("toolchain: no SDK anywhere is still #4200's FAIL, with the probe in place",
      pf.classify_toolchain(_tc("absent", off_path="")).status == "FAIL",
      "the false-green #4200 closed must stay closed")

# ------------------------------------------------------ the probe itself
_FAKE = {"/opt/dotnet/dotnet", "/usr/share/dotnet/dotnet"}
check("toolchain: the probe returns the directory, not the binary, of the first hit",
      pf.find_offpath_dotnet(["/nope", "/usr/share/dotnet", "/opt/dotnet"],
                             is_exec=_FAKE.__contains__) == "/usr/share/dotnet",
      repr(pf.find_offpath_dotnet(["/nope", "/usr/share/dotnet", "/opt/dotnet"],
                                  is_exec=_FAKE.__contains__)))
check("toolchain: the probe answers None when no candidate holds a dotnet",
      pf.find_offpath_dotnet(["/nope", "/also-nope"], is_exec=_FAKE.__contains__) is None,
      "a probe that invents a hit would report an SDK the box does not have")
check("toolchain: the probe is ORDERED -- an earlier candidate wins over a later one",
      pf.find_offpath_dotnet(["/opt/dotnet", "/usr/share/dotnet"],
                             is_exec=_FAKE.__contains__) == "/opt/dotnet",
      "PATH-like precedence, so the reported path is the one the export would use")
_dirs = pf.dotnet_probe_dirs({"DOTNET_ROOT": "/custom/sdk"})
check("toolchain: $DOTNET_ROOT is probed, and probed FIRST",
      _dirs and _dirs[0] == "/custom/sdk", repr(_dirs[:3]))
check("toolchain: ~ is expanded, so the list holds paths os.path can test",
      all(not d.startswith("~") for d in pf.dotnet_probe_dirs({})),
      repr(pf.dotnet_probe_dirs({})))
check("toolchain: the probe list has no duplicates",
      len(pf.dotnet_probe_dirs({"DOTNET_ROOT": "/usr/share/dotnet"})) ==
      len(set(pf.dotnet_probe_dirs({"DOTNET_ROOT": "/usr/share/dotnet"}))),
      repr(pf.dotnet_probe_dirs({"DOTNET_ROOT": "/usr/share/dotnet"})))
check("toolchain: Windows' ProgramFiles is probed as <ProgramFiles>/dotnet, not as itself",
      os.path.join("C:/PF", "dotnet") in pf.dotnet_probe_dirs({"ProgramFiles": "C:/PF"}),
      repr(pf.dotnet_probe_dirs({"ProgramFiles": "C:/PF"})[:3]))

# ------------------------------------------------------ the PATH repair
check("toolchain: the repair puts the directory at the FRONT of PATH",
      pf.path_with_dir_prepended("/root/.dotnet", "/usr/bin:/bin")
      == "/root/.dotnet" + os.pathsep + "/usr/bin" + os.pathsep + "/bin",
      repr(pf.path_with_dir_prepended("/root/.dotnet", "/usr/bin:/bin")))
check("toolchain: the repair is idempotent, so repeated probing cannot grow PATH",
      pf.path_with_dir_prepended("/root/.dotnet",
                                 pf.path_with_dir_prepended("/root/.dotnet", "/usr/bin"))
      == pf.path_with_dir_prepended("/root/.dotnet", "/usr/bin"),
      "a repair that degrades with each call is not a repair")
check("toolchain: an entry already further down PATH is MOVED, not duplicated",
      pf.path_with_dir_prepended("/opt/dotnet", "/usr/bin:/opt/dotnet:/bin")
      == "/opt/dotnet" + os.pathsep + "/usr/bin" + os.pathsep + "/bin",
      repr(pf.path_with_dir_prepended("/opt/dotnet", "/usr/bin:/opt/dotnet:/bin")))
check("toolchain: the repair survives an empty PATH rather than emitting a bare separator",
      pf.path_with_dir_prepended("/root/.dotnet", "") == "/root/.dotnet",
      repr(pf.path_with_dir_prepended("/root/.dotnet", "")))

# ------------------------------- the WIRING, which the pure checks above cannot see
# The trap tdd.md names: every check above would pass with `find_offpath_dotnet`
# defined and never called from toolchain_reading. These drive the real function
# with `which`, the probe and `run` replaced, so nothing here reads the live box.
def _wired(which_hit, probe_hit, sdk_text="9.0.318 [/fake/sdk]\n"):
    saved = (pf.shutil.which, pf.find_offpath_dotnet, pf.run, os.environ.get("PATH", ""),
             pf._DOTNET_PATH_REPAIR)
    pf._DOTNET_PATH_REPAIR = ""
    try:
        pf.shutil.which = lambda _n: which_hit
        pf.find_offpath_dotnet = lambda _d, **_k: probe_hit
        pf.run = lambda *_a, **_k: pf.Ran(rc=0, out=sdk_text, err="")
        reading = pf.toolchain_reading(".")
        return reading, os.environ.get("PATH", "")
    finally:
        pf.shutil.which, pf.find_offpath_dotnet, pf.run = saved[0], saved[1], saved[2]
        os.environ["PATH"] = saved[3]
        pf._DOTNET_PATH_REPAIR = saved[4]


_r_off, _path_after = _wired(None, "/fake/dotnet")
check("toolchain: toolchain_reading ASKS the probe when dotnet is not on PATH",
      _r_off.get("off_path_dir") == "/fake/dotnet" and _r_off.get("status") == "ok",
      repr(_r_off))
check("toolchain: ...and REPAIRS this process's PATH, so every check below measures a real box",
      _path_after.split(os.pathsep)[0] == "/fake/dotnet", repr(_path_after[:80]))
_r_none, _path_none = _wired(None, None)
check("toolchain: ...and reports absent, unrepaired, when the probe finds nothing either",
      _r_none.get("status") == "absent" and "/fake/dotnet" not in _path_none,
      repr(_r_none))
_r_on, _ = _wired("/usr/bin/dotnet", "/must-not-be-used")
check("toolchain: a dotnet already on PATH is never attributed to the probe",
      _r_on.get("off_path_dir") == "" and _r_on.get("status") == "ok", repr(_r_on))


# The warm-run half (local-test-scope.md). The repair makes `which` succeed, so a
# SECOND reading in one process would answer PASS and erase both the finding and
# the export the caller's shell still needs. Measured before the fix: WARN, PASS,
# PASS. CI cannot catch this -- it starts a fresh process for every run.
def _wired_twice():
    saved = (pf.shutil.which, pf.find_offpath_dotnet, pf.run, os.environ.get("PATH", ""),
             pf._DOTNET_PATH_REPAIR)
    pf._DOTNET_PATH_REPAIR = ""
    try:
        resolvable = []
        pf.shutil.which = lambda _n: ("/fake/dotnet/dotnet" if resolvable else None)
        pf.find_offpath_dotnet = lambda _d, **_k: "/fake/dotnet"
        pf.run = lambda *_a, **_k: pf.Ran(rc=0, out="9.0.318 [/fake/sdk]\n", err="")
        first = pf.toolchain_reading(".")
        resolvable.append(True)          # ...because the first call repaired PATH
        second = pf.toolchain_reading(".")
        return first, second, os.environ.get("PATH", "")
    finally:
        pf.shutil.which, pf.find_offpath_dotnet, pf.run = saved[0], saved[1], saved[2]
        os.environ["PATH"] = saved[3]
        pf._DOTNET_PATH_REPAIR = saved[4]


_w1, _w2, _wpath = _wired_twice()
check("toolchain: a SECOND reading still reports the SDK as off PATH, warm as well as cold",
      _w2.get("off_path_dir") == "/fake/dotnet" == _w1.get("off_path_dir"),
      f"cold={_w1.get('off_path_dir')!r} warm={_w2.get('off_path_dir')!r}")
check("toolchain: ...and so the WARM verdict is still the WARN, not a PASS",
      pf.classify_toolchain(_w2).status == "WARN",
      f"got {pf.classify_toolchain(_w2).status}: the repair would erase its own finding")
check("toolchain: ...and PATH gained the directory once, not once per call",
      _wpath.split(os.pathsep).count("/fake/dotnet") == 1, repr(_wpath[:80]))

# The sibling state (#4299, question 3). classify_artifacts is handed "can this
# box run `dotnet run`", and main() must answer it from the READING, not from the
# verdict letter -- keyed on the letter, an off-PATH WARN would make the artifacts
# check tell a box whose SDK works that it has none.
_art_off = pf.classify_artifacts({"status": "absent", "broken": [], "total": 0,
                                  "error": "", "root": ""}, toolchain_ok=True)
_art_no_sdk = pf.classify_artifacts({"status": "absent", "broken": [], "total": 0,
                                     "error": "", "root": ""}, toolchain_ok=False)
check("toolchain: an off-PATH SDK PERMITS provisioning -- it is a WARN, and it works",
      pf.toolchain_permits_provisioning(_off) is True,
      "keyed on status == 'PASS' this answers False, and the note below goes wrong")
check("toolchain: ...as does an SDK on PATH, unchanged",
      pf.toolchain_permits_provisioning(_ok) is True, "no regression on the PASS path")
check("toolchain: ...while no SDK, a too-old SDK and an unreadable one all do NOT",
      not any(pf.toolchain_permits_provisioning(r) for r in (_none, _too_old, _off_bad)),
      "a box that cannot run `dotnet run` must not be told a run will provision for it")
check("toolchain: ...and the two artifacts notes really do differ, so that choice matters",
      _art_off.detail != _art_no_sdk.detail,
      "if they were identical this check would be asserting nothing")
check("toolchain: ...with the false-statement note reaching only the box that has no SDK",
      any("has no" in d for d in _art_no_sdk.detail)
      and not any("has no" in d for d in _art_off.detail),
      f"ok={_art_off.detail!r} not-ok={_art_no_sdk.detail!r}")

# ------------------------------------------------ parsing `dotnet --list-sdks`
check("toolchain: a single 8.0 SDK parses to exactly that version",
      pf.parse_list_sdks(SDKS_8_ONLY) == ["8.0.425"],
      repr(pf.parse_list_sdks(SDKS_8_ONLY)))
check("toolchain: a mixed list keeps both versions in order",
      pf.parse_list_sdks(SDKS_MIXED) == ["8.0.130", "10.0.111"],
      repr(pf.parse_list_sdks(SDKS_MIXED)))
check("toolchain: the mise shim banner is not parsed as an SDK version",
      pf.parse_list_sdks(BANNER + SDKS_MIXED) == ["8.0.130", "10.0.111"],
      repr(pf.parse_list_sdks(BANNER + SDKS_MIXED)))
check("toolchain: empty output parses to no SDKs rather than to a false answer",
      pf.parse_list_sdks("") == [], repr(pf.parse_list_sdks("")))

# The floor is a MAJOR comparison, and it must not be a string one: "10.0.111"
# sorts BEFORE "9.0.0" lexically, which would report the newest SDK as too old.
check("toolchain: 10.x clears a floor of 9 (a lexical compare would fail this)",
      pf.sdk_meets_floor(["10.0.111"], 9) is True)
check("toolchain: 9.0.318 clears a floor of 9",
      pf.sdk_meets_floor(["9.0.318"], 9) is True)
check("toolchain: 8.0.425 alone does NOT clear a floor of 9",
      pf.sdk_meets_floor(["8.0.425"], 9) is False)
check("toolchain: the highest SDK decides, not the first or the last listed",
      pf.sdk_meets_floor(["10.0.111", "8.0.130"], 9) is True)
check("toolchain: no SDKs at all does not clear the floor",
      pf.sdk_meets_floor([], 9) is False)
check("toolchain: an unparseable version is ignored rather than crashing the check",
      pf.sdk_meets_floor(["not-a-version", "10.0.111"], 9) is True)

# ------------------------- the floor is READ from global.json, not transcribed
# Two copies agree until one is edited, and nothing says which. The pin in
# global.json is the one CI resolves, so it is the one preflight must measure
# against -- a hardcoded 9 here would go stale the day the pin moves.
_gj = pf.global_json_floor('{"sdk": {"version": "9.0.100", "rollForward": "latestMajor"}}')
check("toolchain: the floor is read out of global.json rather than hardcoded",
      _gj == 9, f"got {_gj!r}")
check("toolchain: a global.json that cannot be parsed yields no floor rather than a wrong one",
      pf.global_json_floor("{not json") is None,
      repr(pf.global_json_floor("{not json")))
check("toolchain: a global.json with no sdk.version yields no floor",
      pf.global_json_floor('{"sdk": {"rollForward": "latestMajor"}}') is None)

# ------------- the artifacts check must not pass by naming an unrunnable remedy
# The sharp half of #4200. `artifacts: absent` PASSes citing "a run provisions
# what it needs" -- true in general, and false on a box with no SDK, because
# provisioning IS a `dotnet run`. That is a remedy the missing prerequisite has
# already removed, the shape agent_self_freshness.py's `detached` case exists to
# avoid. The verdict stays a PASS (nothing is broken); what changes is that it
# stops asserting a recovery path that cannot run.
_absent_no_sdk = pf.classify_artifacts(_art("absent"), toolchain_ok=False)
check("artifacts: with no usable SDK, the absent root no longer claims a run will provision it",
      "a run provisions what it needs" not in
      (_absent_no_sdk.summary + " " + " ".join(_absent_no_sdk.detail)),
      f"detail={_absent_no_sdk.detail!r}")
check("artifacts: ...and says provisioning needs the SDK, pointing at the toolchain check",
      "toolchain" in (_absent_no_sdk.summary + " " + " ".join(_absent_no_sdk.detail)).lower(),
      f"detail={_absent_no_sdk.detail!r}")
check("artifacts: ...while a box WITH an SDK keeps the original reasoning",
      "a run provisions what it needs" in
      " ".join(pf.classify_artifacts(_art("absent"), toolchain_ok=True).detail))
check("artifacts: ...and the toolchain argument defaults to the healthy reading",
      pf.classify_artifacts(_art("absent")).detail ==
      pf.classify_artifacts(_art("absent"), toolchain_ok=True).detail)

# ------------------------------------------ ordering: before what it gates
# A toolchain check that runs after the checks assuming a toolchain reports the
# consequences before the cause, which is the ordering that cost the reporting
# cycle three wrong turns.
_names = pf.check_names_in_order()
check("toolchain: the check runs before the artifacts check that depends on it",
      "toolchain" in _names and _names.index("toolchain") < _names.index("artifacts"),
      f"order={_names!r}")

# -------------------------------------------------- END toolchain (#4200)



# ------------------------------------------- the reap verdict (#4385)
print()
print("preflight.py -- a verdict per kept worktree (#4385)")

# `--reap` keeps a worktree whose PR merged but which holds unpushed commits,
# and says only WHICH CONDITION held it. Nobody can tell from that whether the
# work is duplicated in the squash or lost, so 18 such worktrees accumulated.
#
# Every obvious instrument false-negatives here, measured on the live set before
# this was written (#4385, and the coordinator's comment on it):
#
#   git diff origin/main...<tip>   never empty -- the branches are weeks behind,
#                                  so a three-dot diff shows the whole fork delta
#   git cherry origin/main <br>    "+" for every branch: it compares patch-ids,
#                                  and a squash collapses N commits into one new
#                                  patch, so the ids never match
#   git diff origin/main..<tip>    DIFFERS for all 50 files on one branch, because
#     -- <file>                    main moved since the merge
#
# What discriminates is narrower than any of those, and it is what
# classify_unpushed_work() below implements: compare the TIP's blob for each file
# the unpushed commits touched against the MERGE COMMIT's blob -- the snapshot of
# main as the squash landed it, not main as it is now.

_v_tmp = tempfile.mkdtemp(prefix="preflight-verdict-")
try:
    _venv = dict(os.environ,
                 GIT_AUTHOR_NAME="t", GIT_AUTHOR_EMAIL="t@e",
                 GIT_COMMITTER_NAME="t", GIT_COMMITTER_EMAIL="t@e",
                 GIT_CONFIG_GLOBAL=os.path.join(_v_tmp, "nogitconfig"),
                 GIT_CONFIG_SYSTEM=os.path.join(_v_tmp, "nogitconfig"))
    _vrepo = os.path.join(_v_tmp, "r")
    os.makedirs(_vrepo)

    def _vg(*args, cwd=_vrepo):
        return subprocess.run(["git", *args], cwd=cwd, check=True,
                              capture_output=True, text=True, env=_venv)

    def _vwrite(rel, text, cwd=_vrepo):
        path = os.path.join(cwd, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(text)

    _vg("init", "-q", "-b", "main")
    _vwrite("base.txt", "base\n")
    _vg("add", "base.txt")
    _vg("commit", "-qm", "base")
    _base_commit = _vg("rev-parse", "HEAD").stdout.strip()

    # --- branch A: the benign shape. The agent committed locally after the
    # squash landed, and every file its unpushed commits touched is byte-identical
    # to the merge commit. This is the GREEN CONTROL: a verdict function that
    # always answered "cannot tell" would pass every refusal arm below, and only
    # this arm catches it.
    # TWO commits, not one, and that is load-bearing for the `git cherry` arm
    # below: a ONE-commit branch squashes to the same patch it already had, so
    # the patch-ids still match and `git cherry` answers "-" (upstream) --
    # correctly. It is the collapse of N>1 patches into ONE that destroys the
    # id. Measured while writing this, in a throwaway repo: a one-commit branch
    # gives "-", a two-commit branch gives "+" for both. So the instrument is
    # not simply broken; it is blind exactly where these worktrees live, and a
    # fixture with one commit would have "refuted" it into agreeing.
    _vg("checkout", "-qb", "carried")
    _vwrite("feature.txt", "final content\n")
    _vg("add", "feature.txt")
    _vg("commit", "-qm", "feat: part one")
    _vwrite("helper.txt", "helper\n")
    _vg("add", "helper.txt")
    _vg("commit", "-qm", "feat: part two")
    _carried_tip = _vg("rev-parse", "HEAD").stdout.strip()
    _carried_first = _vg("rev-parse", "HEAD~1").stdout.strip()

    _vg("checkout", "-q", "main")
    _vg("merge", "--squash", "carried")
    _vg("commit", "-qm", "feat: the work (#1)")
    _carried_merge = _vg("rev-parse", "HEAD").stdout.strip()

    # main moves on afterwards, touching an UNRELATED file. This is what makes a
    # comparison against origin/main rather than the merge commit wrong.
    _vwrite("later.txt", "someone else\n")
    _vg("add", "later.txt")
    _vg("commit", "-qm", "chore: later work on main")

    # --- branch B: content that genuinely never reached main.
    _vg("checkout", "-qb", "lost", _carried_merge)
    _vwrite("rescue-me.txt", "never pushed anywhere\n")
    _vg("add", "rescue-me.txt")
    _vg("commit", "-qm", "feat: unlanded")
    _lost_tip = _vg("rev-parse", "HEAD").stdout.strip()

    _vg("checkout", "-q", "main")

    # ---------------------------------------------------------------- CARRIED
    _v = pf.classify_unpushed_work(_vrepo, tip=_carried_tip,
                                   unpushed=[_carried_tip, _carried_first],
                                   reference=_carried_merge)
    check("verdict: a tip whose touched files all match the merge commit is CARRIED",
          _v.verdict == "carried", f"{_v.verdict}: {_v.detail}")
    check("verdict: ...and it names how many files it compared, not just the word",
          _v.same == 2 and _v.differing == [], f"same={_v.same} differing={_v.differing}")

    # ------------------------------------------------------------------ LOST
    _v = pf.classify_unpushed_work(_vrepo, tip=_lost_tip,
                                   unpushed=[_lost_tip],
                                   reference=_carried_merge)
    check("verdict: a tip holding a file the merge commit never had is DIFFERS",
          _v.verdict == "differs", f"{_v.verdict}: {_v.detail}")
    check("verdict: ...and it names the file, so a human knows where to look",
          _v.differing == ["rescue-me.txt"], f"differing={_v.differing}")

    # ------------------------------------------ the three-dot diff, refuted here
    # Asserted against real git rather than argued: this is the instrument the
    # issue body proposed, and the reason the verdict cannot use it.
    _three = subprocess.run(["git", "diff", "--name-only", f"main...{_carried_tip}"],
                            cwd=_vrepo, env=_venv, capture_output=True, text=True)
    check("verdict: `git diff main...<tip>` is NON-empty for a fully carried branch",
          _three.stdout.strip() != "",
          "the trap did not reproduce; the carried arm above proves nothing extra")

    # ------------------------------------------------- git cherry, refuted here
    _cherry = subprocess.run(["git", "cherry", "main", "carried"],
                             cwd=_vrepo, env=_venv, capture_output=True, text=True)
    _marks = [ln.split()[0] for ln in _cherry.stdout.splitlines() if ln.strip()]
    check("verdict: `git cherry` reports BOTH squash-carried commits as NOT upstream",
          _marks == ["+", "+"], f"cherry={_cherry.stdout!r}")

    # ------------------------------------------------- reference = origin/main
    # The same carried tip judged against main's CURRENT head rather than the
    # merge commit. main moved only in an unrelated file, so this still answers
    # carried -- which is what makes the next arm the discriminating one.
    _v = pf.classify_unpushed_work(_vrepo, tip=_carried_tip,
                                   unpushed=[_carried_tip, _carried_first],
                                   reference="main")
    check("verdict: an unrelated later commit on main does not disturb the verdict",
          _v.verdict == "carried", f"{_v.verdict}: {_v.detail}")

    # ...but when main LATER EDITS one of the touched files, comparing against
    # main's head answers "differs" for work that demonstrably landed. That is
    # the whole reason the reference is the merge commit.
    _vwrite("feature.txt", "final content\nplus a later fix\n")
    _vg("add", "feature.txt")
    _vg("commit", "-qm", "fix: later edit to the same file")
    _v_head = pf.classify_unpushed_work(_vrepo, tip=_carried_tip,
                                        unpushed=[_carried_tip, _carried_first],
                                        reference="main")
    _v_merge = pf.classify_unpushed_work(_vrepo, tip=_carried_tip,
                                         unpushed=[_carried_tip, _carried_first],
                                         reference=_carried_merge)
    check("verdict: against main's HEAD a carried file reads as differing once main edits it",
          _v_head.verdict == "differs", f"{_v_head.verdict}: {_v_head.detail}")
    check("verdict: ...while against the MERGE COMMIT the same tip is still carried",
          _v_merge.verdict == "carried", f"{_v_merge.verdict}: {_v_merge.detail}")

    # ------------------------------------------------------- the third state
    # guards-need-a-third-state.md: a reference that cannot be read must not be
    # rendered as either answer. "carried" would invite a delete; "differs" would
    # cry wolf on every worktree forever.
    _v = pf.classify_unpushed_work(_vrepo, tip=_carried_tip,
                                   unpushed=[_carried_tip, _carried_first],
                                   reference="0" * 40)
    check("verdict: an unreadable reference is UNKNOWN, neither carried nor differs",
          _v.verdict == "unknown", f"{_v.verdict}: {_v.detail}")
    _v = pf.classify_unpushed_work(_vrepo, tip="0" * 40, unpushed=["0" * 40],
                                   reference=_carried_merge)
    check("verdict: an unreadable tip is UNKNOWN too",
          _v.verdict == "unknown", f"{_v.verdict}: {_v.detail}")
    check("verdict: no unpushed commits at all is UNKNOWN, not a silent carried",
          pf.classify_unpushed_work(_vrepo, tip=_carried_tip, unpushed=[],
                                    reference=_carried_merge).verdict == "unknown")

    # ------------------------------- a path ABSENT from both sides is not "same"
    # `_blob_at` reads `git rev-parse --verify -q`, and the flags are the arm.
    # Without them git ECHOES the unresolvable argument back on stdout ("HEAD:
    # gone.txt") and exits 128, so two absent paths yield the SAME string and
    # compare equal -- a file the tip deleted and the reference still has would
    # be the near miss, but the reachable one is a file absent from both, which
    # a bare string compare calls carried. Measured in a throwaway repo:
    # `git rev-parse HEAD:gone.txt` prints `HEAD:gone.txt`, twice, identically.
    #
    # A commit that DELETES a file is ordinary, so this is not a corner: it is
    # what a cleanup commit looks like, and the verdict must not read its own
    # error message as evidence that the work landed.
    _vg("checkout", "-qb", "deleter", _carried_merge)
    _vg("rm", "-q", "helper.txt")
    _vg("commit", "-qm", "chore: drop the helper")
    _del_tip = _vg("rev-parse", "HEAD").stdout.strip()
    # The reference is an EARLIER commit that also lacks helper.txt -- the base,
    # before the feature landed. helper.txt is absent from both sides.
    _v = pf.classify_unpushed_work(_vrepo, tip=_del_tip, unpushed=[_del_tip],
                                   reference=_base_commit)
    check("verdict: a path absent from BOTH sides is differing, not silently same",
          _v.verdict == "differs" and "helper.txt" in _v.differing,
          f"{_v.verdict}: same={_v.same} differing={_v.differing}")

    # The arm above RIDES ALONG under the obvious mutation and this one does not,
    # which is why both are here. Strip `--verify -q` and git echoes the whole
    # argument -- "<sha>:helper.txt" -- so two absent paths under DIFFERENT revs
    # still produce different strings and the compare lands on "differs" for the
    # wrong reason. Measured: only the check below goes red. Same rev on both
    # sides is the shape where the echoes are byte-identical.
    check("verdict: ...and _blob_at answers None for an absent path, never git's echo",
          pf._blob_at(_vrepo, _del_tip, "helper.txt") is None,
          repr(pf._blob_at(_vrepo, _del_tip, "helper.txt")))
    check("verdict: ...so two absent paths under ONE rev do not compare equal either",
          pf._blob_at(_vrepo, _del_tip, "gone-a.txt")
          is pf._blob_at(_vrepo, _del_tip, "gone-b.txt") is None,
          f"{pf._blob_at(_vrepo, _del_tip, 'gone-a.txt')!r} "
          f"{pf._blob_at(_vrepo, _del_tip, 'gone-b.txt')!r}")
    _vg("checkout", "-q", "main")

    # ------------------------------ unpushed_commits: a failed read is not zero
    # `--not --remotes` is the instrument, and it is sharper than the census
    # count beside it: `unpushed_against_pr` compares HEAD against the PR's
    # headRefOid, so it answers 1 for a head that merely differs -- including one
    # that IS on a remote ref. Measured on the live set (#4385): two worktrees
    # the census called "1 UNPUSHED COMMIT" have nothing outside the remotes.
    # A remote ref has to exist for `--not --remotes` to exclude anything, so the
    # fixture makes one: main as it stands is "pushed", and a commit made after
    # it is not. Without this the arm passes for a repo with no remotes at all,
    # where every commit is trivially "unpushed" and the flag is doing no work.
    _vg("update-ref", "refs/remotes/origin/main", "main")
    _vwrite("local-only.txt", "not pushed\n")
    _vg("add", "local-only.txt")
    _vg("commit", "-qm", "chore: after the last push")
    _after_push = _vg("rev-parse", "HEAD").stdout.strip()
    _main_head = _vg("rev-parse", "refs/remotes/origin/main").stdout.strip()
    _uc = pf.unpushed_commits(_vrepo)
    check("verdict: unpushed_commits lists the commit no remote ref contains",
          _uc == [_after_push], f"{_uc!r} vs [{_after_push}]")
    check("verdict: ...and EXCLUDES everything the remote ref already reaches",
          _uc is not None and _main_head not in _uc, f"{_uc!r}")
    _vg("reset", "-q", "--hard", "HEAD~1")
    _vg("update-ref", "-d", "refs/remotes/origin/main")
    # And the third state, which is the one that decides a verdict: a read that
    # FAILED must be None, never []. An empty list renders as "nothing unpushed",
    # which is the success state for a measurement that never happened
    # (`guards-need-a-third-state.md`), and classify_unpushed_work would then
    # answer "unknown -- no unpushed commits" for a worktree it could not read at
    # all, hiding the git failure behind a plausible sentence.
    check("verdict: a rev-list that cannot run answers None, not an empty list",
          pf.unpushed_commits(os.path.join(_v_tmp, "not-a-repo")) is None,
          repr(pf.unpushed_commits(os.path.join(_v_tmp, "not-a-repo"))))
    _vfail = pf.verdict_for_worktree(os.path.join(_v_tmp, "not-a-repo"),
                                     {"number": 9, "mergeCommit": {"oid": _carried_merge}})
    check("verdict: ...and an unreadable worktree reaches the report as UNKNOWN",
          _vfail.verdict == "unknown", f"{_vfail.verdict}: {_vfail.detail}")

    # ---------------------------- verdict_for_worktree needs a merge commit
    # A PR with no mergeCommit (never merged, or the field absent) has no
    # snapshot of main to compare against. Deliberately NOT falling back to
    # origin/main: the arms above measured that main's head answers "differs"
    # for work that landed, so the fallback would manufacture false alarms.
    _vnomerge = pf.verdict_for_worktree(_vrepo, {"number": 7})
    check("verdict: a PR with no merge commit is UNKNOWN, not compared against main",
          _vnomerge.verdict == "unknown" and "merge commit" in _vnomerge.detail,
          f"{_vnomerge.verdict}: {_vnomerge.detail}")

    # --------------------------------------- the verdict never reaps anything
    # The safety property the issue is explicit about: trading a false keep for a
    # false delete is strictly worse. A CARRIED verdict is a report to a human,
    # and stays one under plain `--reap` (#4419 opted the removal in behind its
    # own flag rather than widening `--reap`, which unattended loops already run).
    _d = pf.disposition(pf.Worktree(path="/w/c", head=_carried_tip, branch="carried"),
                        pr={"number": 1, "state": "MERGED", "headRefOid": "x" * 40},
                        dirty=False, unpushed=1)
    check("verdict: a CARRIED worktree is still NOT reapable under plain --reap",
          not _d.reapable, _d.reason)

    # ------------------------------------------------- #4419: --reap-carried
    # preflight established that a worktree's content is on main and then declined
    # to act on its own finding, so 5 worktrees / 0.8 GiB survived a successful
    # reap. The verdict now REACHES the decision -- but only when asked, and only
    # when it is the verdict "carried".
    #
    # Every keep-case arm below passes `carried_ok=True`. That is deliberate and
    # load-bearing: an arm asserting "this worktree is KEPT" proves nothing if the
    # fixture would be kept under every implementation, and the cheapest way to
    # make it vacuous is to leave the flag off. With the flag ON, the ONLY thing
    # standing between each of these fixtures and removal is the property the arm
    # is named for -- so a wrong decision changes the verdict, which is what makes
    # the mutation table discriminate.
    _CARRIED = pf.UnpushedVerdict("carried", 2, [], "all 2 file(s) ... the content is on main")
    _DIFFERS = pf.UnpushedVerdict("differs", 1, ["rescue-me.txt"], "1 of 2 file(s) differ")
    _UNKNOWN = pf.UnpushedVerdict("unknown", 0, [], "the reference could not be read")
    _MERGED_PR = {"number": 1, "state": "MERGED", "headRefOid": "x" * 40}

    def _disp(verdict, *, carried_ok=True, dirty=False, unpushed=1, pr=_MERGED_PR):
        return pf.disposition(pf.Worktree(path="/w/c", head=_carried_tip, branch="carried"),
                              pr=pr, dirty=dirty, unpushed=unpushed,
                              verdict=verdict, carried_ok=carried_ok)

    # THE RED ARM: the whole point of the issue.
    _d = _disp(_CARRIED)
    check("reap-carried: a CARRIED worktree IS reapable once --reap-carried asks",
          _d.reapable, _d.reason)
    check("reap-carried: ...and the reason says the content is on main, so a reader "
          "of the log can tell this removal from an ordinary one",
          _d.reapable and "content is on main" in _d.reason, _d.reason)

    # KEEP 1 -- DIFFERS. The dangerous direction: this worktree holds content main
    # does not have. Flag ON, merged PR, clean tree: the verdict is the only thing
    # keeping it, so a decision that keyed on "has a verdict at all", or on
    # `verdict is not None`, or that inverted the comparison, deletes real work.
    _d = _disp(_DIFFERS)
    check("reap-carried: a DIFFERS worktree is kept even with --reap-carried",
          not _d.reapable, _d.reason)

    # KEEP 2 -- UNKNOWN. guards-need-a-third-state.md: a measurement that did not
    # happen must not be spelled as its success state. Same construction as KEEP 1
    # -- flag on, merged, clean -- so only the third state holds it.
    _d = _disp(_UNKNOWN)
    check("reap-carried: an UNKNOWN verdict is kept; a non-measurement is not a pass",
          not _d.reapable, _d.reason)

    # KEEP 3 -- no verdict was computed at all. `collect_worktrees(verdicts=False)`
    # is a real call path, and None must read as "nothing was established", never
    # as carried. Flag on and everything else clear, so nothing else can hold it.
    _d = _disp(None)
    check("reap-carried: a worktree with NO verdict computed is kept",
          not _d.reapable, _d.reason)

    # KEEP 4 -- uncommitted changes. Nothing could have carried these, so a
    # carried verdict about the COMMITS says nothing about the working tree. The
    # fixture deliberately pairs a CARRIED verdict with a dirty tree: an
    # implementation that checked the verdict before the dirt would delete it.
    _d = _disp(_CARRIED, dirty=True)
    check("reap-carried: a CARRIED verdict does not override uncommitted changes",
          not _d.reapable, _d.reason)
    check("reap-carried: ...and the reason still names the uncommitted changes, "
          "not the verdict, so the human is sent to the right place",
          "uncommitted" in _d.reason, _d.reason)

    # KEEP 5 -- the PR is not merged. An OPEN PR with a carried verdict is a live
    # branch whose content happens to match something; mergedness is a separate
    # question and is asked first.
    _d = _disp(_CARRIED, pr={"number": 2, "state": "OPEN", "headRefOid": "x" * 40})
    check("reap-carried: an OPEN PR is kept however carried its content looks",
          not _d.reapable, _d.reason)

    # KEEP 6 -- unpushed is None, i.e. nothing proved the commits were pushed.
    # Distinct from unpushed=0: None is the third state of that measurement, and
    # a carried verdict must not launder it into a pass.
    _d = _disp(_CARRIED, unpushed=None)
    check("reap-carried: 'nothing proves the commits were pushed' is kept",
          not _d.reapable, _d.reason)

    # CONTROL -- the flag is what gates it, not the verdict. Same CARRIED fixture
    # as the RED arm with carried_ok=False: an implementation that ignored the
    # flag and always acted on a carried verdict passes every arm above and fails
    # here. This is the arm that pins the opt-in.
    _d = _disp(_CARRIED, carried_ok=False)
    check("reap-carried: ...and plain --reap still keeps it, so the flag is the gate",
          not _d.reapable, _d.reason)

    # CONTROL -- an ordinary clean, fully-pushed, merged worktree stays reapable
    # under both flags. A guard that started refusing everything would pass all
    # six KEEP arms; only this says the reaper still does its original job.
    for _ok in (False, True):
        _d = _disp(None, unpushed=0, carried_ok=_ok)
        check(f"reap-carried: an ordinary clean merged worktree is still REAP "
              f"(carried_ok={_ok})", _d.reapable, _d.reason)

    # ---- end to end: the flag reaches the decision through collect_worktrees.
    # The arms above call disposition() directly. This one drives the real path --
    # a git worktree, a real merge commit, a real unpushed commit -- because a
    # correct disposition() that main() never passes the flag to is the same
    # defect wearing a passing test (#4419 is exactly "the verdict is computed and
    # then discarded").
    _wt_dir = os.path.join(_v_tmp, "wt-carried")
    _vg("worktree", "add", "-q", "--detach", _wt_dir, _carried_tip)
    _vgw = lambda *a: subprocess.run(["git", *a], cwd=_wt_dir, env=_venv,
                                     capture_output=True, text=True, check=True)
    _vgw("checkout", "-qb", "agent/x/issue-1", _carried_tip)
    _e2e_pr = {"agent/x/issue-1": {"number": 1, "state": "MERGED",
                                   "headRefOid": "x" * 40,
                                   "mergeCommit": {"oid": _carried_merge}}}
    _rows = pf.collect_worktrees(_vrepo, _e2e_pr, measure=False, carried_ok=True)
    _row = [r for r in _rows if os.path.realpath(r[0].path) == os.path.realpath(_wt_dir)]
    check("reap-carried: collect_worktrees finds the fixture worktree",
          len(_row) == 1, f"{[r[0].path for r in _rows]}")
    if _row:
        _, _, _, _, _dd, _ = _row[0]
        check("reap-carried: end to end, the real path marks it reapable",
              _dd.reapable, f"{_dd.reason} / verdict={_dd.verdict}")
        _rows_off = pf.collect_worktrees(_vrepo, _e2e_pr, measure=False, carried_ok=False)
        _row_off = [r for r in _rows_off
                    if os.path.realpath(r[0].path) == os.path.realpath(_wt_dir)][0]
        check("reap-carried: ...and end to end without the flag it is kept",
              not _row_off[4].reapable, _row_off[4].reason)
        # And the reaper's own dry run names it, which is what a human reads
        # before letting it delete anything.
        _log = pf.reap(_vrepo, _rows, dry_run=True)
        check("reap-carried: --dry-run names the carried worktree it would remove",
              any(_wt_dir in ln and "WOULD REMOVE" in ln for ln in _log), f"{_log}")

        # ...and the reaper RE-JUDGES the verdict rather than trusting the census,
        # for the same reason it re-reads the dirt: the census may be minutes old,
        # and this is the one removal path where a stale measurement deletes a
        # COMMIT rather than an empty directory. Simulated by committing a file the
        # merge commit never had, AFTER the row was built -- which is exactly what
        # an agent doing a last-minute edit in a reaped worktree looks like.
        _vgw("checkout", "-q", "-b", "drifted")
        with open(os.path.join(_wt_dir, "arrived-late.txt"), "w") as _fh:
            _fh.write("written after the census\n")
        _vgw("add", "arrived-late.txt")
        _vgw("commit", "-qm", "feat: work that arrived after the census")
        _log2 = pf.reap(_vrepo, _rows, dry_run=True)
        check("reap-carried: the reaper re-judges the verdict and SKIPS one that drifted",
              any(_wt_dir in ln and ln.startswith("SKIP") for ln in _log2), f"{_log2}")
        check("reap-carried: ...and says so rather than removing it silently",
              not any(_wt_dir in ln and "WOULD REMOVE" in ln for ln in _log2), f"{_log2}")
        _vgw("checkout", "-q", "agent/x/issue-1")
        _vgw("branch", "-qD", "drifted")
    _vg("worktree", "remove", "--force", _wt_dir)
finally:
    shutil.rmtree(_v_tmp, ignore_errors=True)
    shutil.rmtree(_v_tmp, ignore_errors=True)

# --------------------------------------- uncommitted work has its own verdict
# Nothing could have carried an uncommitted change, so the verdict for those
# three worktrees is a different question: WHAT is uncommitted, so a human can
# see at a glance whether it is a stray build artifact or real work.
check("verdict: uncommitted work is summarised by path, never called carried",
      pf.summarise_uncommitted(" M AlRunner/Foo.cs\n?? scratch.txt\n")
      == (2, ["AlRunner/Foo.cs", "scratch.txt"]))
check("verdict: an empty status is no uncommitted work",
      pf.summarise_uncommitted("") == (0, []))
check("verdict: a rename reports its destination, which is the path that exists",
      pf.summarise_uncommitted("R  old.txt -> new.txt\n") == (1, ["new.txt"]))

# -------------------------------------------------- END reap verdict (#4385)

# -------------------------------------------------- END artifacts probe (#3878)

# ------------------------------- --reap-carried implies --reap, in main() (#4424)
# Every arm above drives disposition(), collect_worktrees() or reap() DIRECTLY,
# so none of them observes the one statement in main() that wires the flag to the
# reaper:
#
#     if args.reap_carried:
#         args.reap = True
#
# Disabling it (`if False and args.reap_carried:`) leaves this suite fully green
# while `--reap-carried` stops reaping anything -- #4419's own shape one level up,
# a value computed correctly and never consumed. `carried_ok` still reaches
# collect_worktrees(), so the CARRIED verdict is still computed; `args.reap` is
# still false, so the block that acts on it never runs.
#
# The observable is deliberately the PRESENCE of the reap section, not its
# contents: a reaper that ran and declined prints "nothing to reap", while one
# that never ran emits no `reap` key at all. Asserting only on WOULD REMOVE lines
# could not tell those apart, and the second is the defect.
print()
print("preflight.py -- --reap-carried implies --reap, through main() (#4424)")

_m_tmp = tempfile.mkdtemp(prefix="preflight-main-reap-")
try:
    _menv = dict(os.environ,
                 GIT_AUTHOR_NAME="t", GIT_AUTHOR_EMAIL="t@e",
                 GIT_COMMITTER_NAME="t", GIT_COMMITTER_EMAIL="t@e",
                 GIT_CONFIG_GLOBAL=os.path.join(_m_tmp, "nogitconfig"),
                 GIT_CONFIG_SYSTEM=os.path.join(_m_tmp, "nogitconfig"))
    _mrepo = os.path.join(_m_tmp, "r")
    os.makedirs(_mrepo)

    def _mg(*args, cwd=_mrepo):
        return subprocess.run(["git", *args], cwd=cwd, check=True,
                              capture_output=True, text=True, env=_menv)

    def _mwrite(rel, text, cwd=_mrepo):
        path = os.path.join(cwd, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(text)

    _mg("init", "-q", "-b", "main")
    _mwrite("base.txt", "base\n")
    _mg("add", "base.txt")
    _mg("commit", "-qm", "base")

    # TWO commits before the squash, for the reason the #4385 fixture above
    # records: a one-commit branch keeps its patch-id through a squash, so it is
    # not the shape these worktrees actually have.
    _mg("checkout", "-qb", "carried")
    _mwrite("feature.txt", "final content\n")
    _mg("add", "feature.txt")
    _mg("commit", "-qm", "feat: part one")
    _mwrite("helper.txt", "helper\n")
    _mg("add", "helper.txt")
    _mg("commit", "-qm", "feat: part two")
    _m_carried_tip = _mg("rev-parse", "HEAD").stdout.strip()

    _mg("checkout", "-q", "main")
    _mg("merge", "--squash", "carried")
    _mg("commit", "-qm", "feat: the work (#1)")
    _m_merge = _mg("rev-parse", "HEAD").stdout.strip()
    # main moves on afterwards, so a comparison against main-as-it-is-now is wrong.
    _mwrite("later.txt", "someone else\n")
    _mg("add", "later.txt")
    _mg("commit", "-qm", "chore: later work on main")

    # The KEEP fixture: content that genuinely never reached main.
    _mg("checkout", "-qb", "lost", _m_merge)
    _mwrite("rescue-me.txt", "never pushed anywhere\n")
    _mg("add", "rescue-me.txt")
    _mg("commit", "-qm", "feat: unlanded")
    _m_lost_tip = _mg("rev-parse", "HEAD").stdout.strip()
    _mg("checkout", "-q", "main")

    _m_wt_carried = os.path.join(_m_tmp, "wt-carried")
    _mg("worktree", "add", "-q", "--detach", _m_wt_carried, _m_carried_tip)
    subprocess.run(["git", "checkout", "-qb", "agent/x/issue-1", _m_carried_tip],
                   cwd=_m_wt_carried, env=_menv, check=True, capture_output=True)
    _m_wt_differs = os.path.join(_m_tmp, "wt-differs")
    _mg("worktree", "add", "-q", "--detach", _m_wt_differs, _m_lost_tip)
    subprocess.run(["git", "checkout", "-qb", "agent/x/issue-2", _m_lost_tip],
                   cwd=_m_wt_differs, env=_menv, check=True, capture_output=True)

    _m_prs = {
        "agent/x/issue-1": {"number": 1, "state": "MERGED", "headRefOid": "x" * 40,
                            "mergeCommit": {"oid": _m_merge}},
        "agent/x/issue-2": {"number": 2, "state": "MERGED", "headRefOid": "y" * 40,
                            "mergeCommit": {"oid": _m_merge}},
    }

    def _drive_main(extra_argv: list) -> tuple:
        """Run main() against the fixture repo and return (rc, parsed --json doc).

        Three stubs, each for a dependency that would otherwise reach the real
        box: the repository root (main() derives it from preflight.py's OWN
        location, not the cwd), the pull-request map, and the staleness refusal,
        which returns 3 on a branch whose copy of preflight.py origin/main has
        moved -- i.e. on exactly the branch that edits it.
        """
        _saved = (pf.git_repo_root, pf.pr_map, pf.freshness_refusal)
        pf.git_repo_root = lambda start: _mrepo
        pf.pr_map = lambda slug: (_m_prs, "ok")
        pf.freshness_refusal = lambda *a, **k: None
        _buf = io.StringIO()
        _saved_stdout = sys.stdout
        sys.stdout = _buf
        try:
            _rc = pf.main(extra_argv + ["--dry-run", "--json", "--no-tools",
                                        "--no-sizes", "--no-freshness-fetch"])
        finally:
            sys.stdout = _saved_stdout
            pf.git_repo_root, pf.pr_map, pf.freshness_refusal = _saved
        return _rc, json.loads(_buf.getvalue())

    # ---- THE RED ARM: --reap-carried alone reaches the reaper.
    _rc, _doc = _drive_main(["--reap-carried"])
    check("main: --reap-carried runs the reaper without --reap being passed too",
          "reap" in _doc, sorted(_doc))
    _mlog = _doc.get("reap") or []
    check("main: ...and the CARRIED worktree is the one it would remove",
          any(_m_wt_carried in ln and "WOULD REMOVE" in ln for ln in _mlog), f"{_mlog}")
    check("main: ...naming the content-is-on-main reason, so the log distinguishes "
          "this removal from an ordinary one",
          any(_m_wt_carried in ln and "content is on main" in ln for ln in _mlog), f"{_mlog}")

    # ---- KEEP, under the same invocation. Without this the arm above is an
    # ALLOW-only control: a main() that reaped everything it was handed would
    # pass it. The DIFFERS worktree holds a file the merge commit never had.
    check("main: --reap-carried does NOT remove a DIFFERS worktree",
          not any(_m_wt_differs in ln and "WOULD REMOVE" in ln for ln in _mlog), f"{_mlog}")
    check("main: ...and it is still on disk after the dry run",
          os.path.isdir(_m_wt_differs))

    # ---- CONTROL: plain --reap runs the reaper and declines. This is what makes
    # the RED arm's observable meaningful -- "the reaper ran and removed nothing"
    # and "the reaper never ran" are different states, and only the second is the
    # defect. A broken implication turns the first row into the second.
    _rc_plain, _doc_plain = _drive_main(["--reap"])
    check("main: plain --reap still runs the reaper", "reap" in _doc_plain, sorted(_doc_plain))
    _mlog_plain = _doc_plain.get("reap") or []
    check("main: ...and it removes nothing, because CARRIED is opt-in",
          not any("WOULD REMOVE" in ln for ln in _mlog_plain), f"{_mlog_plain}")
    check("main: ...and says so, rather than printing an empty list",
          _mlog_plain == ["nothing to reap"], f"{_mlog_plain}")

    # ---- CONTROL: no reap flag at all emits no reap section. Without this,
    # "reap in doc" could be true for every invocation and the RED arm would be
    # asserting a constant.
    _rc_none, _doc_none = _drive_main([])
    check("main: with neither flag the reaper does not run at all",
          "reap" not in _doc_none, sorted(_doc_none))

    for _wt in (_m_wt_carried, _m_wt_differs):
        _mg("worktree", "remove", "--force", _wt)
finally:
    shutil.rmtree(_m_tmp, ignore_errors=True)

# ------------------------------------------- END --reap-carried in main() (#4424)


# ------------------------------------------- an EXTRACTED copy probes the cwd (#4534)
# The documented remedy for a stale copy is to extract origin/main's into a temp
# directory. That copy has no repository at its own location, so before #4534 it
# printed "not inside a git repository" and a neutral-but-stale checkout could get
# no branch-ownership verdict by either route. Driven as a real subprocess on a real
# extracted copy: stubbing the resolver would test the stub.
_x_tmp = tempfile.mkdtemp(prefix="preflight-extracted-")
try:
    _x_tools = os.path.join(_x_tmp, "copy", "tools")
    os.makedirs(_x_tools)
    for _f in ("preflight.py", "agent_self_freshness.py", "agent_stdio.py"):
        shutil.copy(os.path.join(HERE, _f), _x_tools)
    _x_nothing = os.path.join(_x_tmp, "nothing")
    _x_wt = os.path.join(_x_tmp, "repo", ".claude", "worktrees", "fbk-9-issue-1234")
    os.makedirs(_x_nothing)
    os.makedirs(_x_wt)
    subprocess.run(["git", "init", "-q", "-b", "agent/fbk-9/issue-1234", _x_wt], check=True)
    subprocess.run(["git", "-C", _x_wt, "-c", "user.name=t", "-c", "user.email=t@example.invalid",
                    "commit", "-q", "--allow-empty", "-m", "x"], check=True)
    subprocess.run(["git", "-C", _x_wt, "remote", "add", "origin",
                    "https://github.com/StefanMaron/BusinessCentral.AL.Runner.git"], check=True)
    _x_foreign = os.path.join(_x_tmp, "unrelated")
    _x_bare = os.path.join(_x_tmp, "no-origin")
    for _d, _origin in ((_x_foreign, "https://github.com/someone/unrelated.git"), (_x_bare, None)):
        os.makedirs(_d)
        subprocess.run(["git", "init", "-q", _d], check=True)
        if _origin:
            subprocess.run(["git", "-C", _d, "remote", "add", "origin", _origin], check=True)

    check("resolve_repo: no repository at either place is (None, 'none'), never a guess",
          pf.resolve_repo(_x_tools, _x_nothing) == (None, "none"),
          pf.resolve_repo(_x_tools, _x_nothing))
    _x_res = pf.resolve_repo(_x_tools, _x_wt)
    check("resolve_repo: an extracted copy falls back to the repository at the cwd",
          _x_res[1] == "cwd" and os.path.realpath(_x_res[0] or "") == os.path.realpath(_x_wt),
          _x_res)
    check("resolve_repo: the script's own repository still wins over the cwd",
          pf.resolve_repo(HERE, _x_wt)[1] == "script", pf.resolve_repo(HERE, _x_wt))

    _x_run = subprocess.run(
        [sys.executable, os.path.join(_x_tools, "preflight.py"), "--agent-id", "stma-x-1",
         "--json", "--no-tools", "--no-sizes", "--no-freshness-fetch",
         "--skip-budget-fallback"],
        cwd=_x_wt, capture_output=True, text=True, timeout=300)
    try:
        _x_doc = json.loads(_x_run.stdout)
    except ValueError:
        _x_doc = {}
    _x_own = [c for c in _x_doc.get("checks", []) if c.get("name") == "branch-ownership"]
    check("an extracted copy run from a FOREIGN worktree answers branch-ownership FAIL",
          len(_x_own) == 1 and _x_own[0].get("status") == "FAIL"
          and "fbk-9" in _x_own[0].get("summary", ""),
          f"rc={_x_run.returncode} stderr={_x_run.stderr.strip()[-300:]!r} own={_x_own}")
    check("...and says it is probing the cwd rather than its own location",
          "probes the repository at the cwd" in _x_run.stderr, _x_run.stderr[-300:])

    _x_none = subprocess.run(
        [sys.executable, os.path.join(_x_tools, "preflight.py"), "--json", "--no-tools",
         "--no-freshness-fetch"],
        cwd=_x_nothing, capture_output=True, text=True, timeout=120)
    check("an extracted copy with no repository at the cwd either refuses with exit 3",
          _x_none.returncode == 3 and "nothing was probed" in _x_none.stderr,
          f"rc={_x_none.returncode} stderr={_x_none.stderr.strip()[-200:]!r}")

    check("is_this_repository: this repository, a fork of it, and nothing else",
          pf.is_this_repository("StefanMaron/BusinessCentral.AL.Runner")
          and pf.is_this_repository("fbk/businesscentral.al.runner")
          and not pf.is_this_repository("someone/unrelated")
          and not pf.is_this_repository("StefanMaron/BusinessCentral.AL.Language.Tests")
          and not pf.is_this_repository(None), "")
    for _d, _why in ((_x_foreign, "someone/unrelated"), (_x_bare, "no GitHub origin")):
        _x_other = subprocess.run(
            [sys.executable, os.path.join(_x_tools, "preflight.py"), "--json", "--no-tools",
             "--no-freshness-fetch"],
            cwd=_d, capture_output=True, text=True, timeout=120)
        check(f"an extracted copy run from a repository that is not this one ({_why}) refuses, exit 3",
              _x_other.returncode == 3 and "not this repository" in _x_other.stderr,
              f"rc={_x_other.returncode} stderr={_x_other.stderr.strip()[-200:]!r}")
finally:
    shutil.rmtree(_x_tmp, ignore_errors=True)
# ------------------------------------------- END extracted copy (#4534)

print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
