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
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
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
                             probe_rc=0, aliases=list(pf.DECOMPILER_ALIASES))
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

r = dec_with(aliases=[a for a in pf.DECOMPILER_ALIASES if a != "bc284"])
check("a missing BC context is reported", r.status == "WARN", r.summary)
check("the missing BC context is named", "bc284" in r.summary, r.summary)

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


print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
