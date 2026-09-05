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

print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
