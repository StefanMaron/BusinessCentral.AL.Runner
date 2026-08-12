#!/usr/bin/env python3
"""Aggregate an AL_RUNNER_PHASE_LOG JSONL file into a human-readable report.

Issue #1825. The runner appends one JSON object per line (see
AlRunner/Infrastructure/PhaseLog.cs) at three granularities:

  kind=app                   one emitted module: emit + compile + run
  kind=bundle                one bundle argument: aggregates its app rows
  kind=process               one OS process: engine boot, wall clock, peak RSS
  kind=process-reexec-parent an outer process that re-exec'd and waited for a child.
                             Its wall clock CONTAINS the child's, so it is reported
                             separately and never summed with kind=process.

The question this exists to answer is which of two things dominates:
a fixed per-process/per-unit tax (→ the fix is process reuse via a warm --server
instance) or dependency-closure loading (→ the fix is making specific suites stop
pulling the Microsoft closure). Hence the cohort split is printed first.

Usage:
  scripts/phase-log-report.py <phase-log.jsonl> [--label NAME] [--step-seconds N]
"""
import argparse
import json
import statistics
import sys


def pct(values, p):
    if not values:
        return 0
    s = sorted(values)
    return s[min(len(s) - 1, int(round((p / 100.0) * (len(s) - 1))))]


def stats_line(label, values, width=34):
    if not values:
        return f"  {label:<{width}} (none)"
    return (
        f"  {label:<{width}} n={len(values):<5} "
        f"total={sum(values) / 1000:8.1f}s  mean={statistics.mean(values) / 1000:6.2f}s  "
        f"median={statistics.median(values) / 1000:6.2f}s  p90={pct(values, 90) / 1000:6.2f}s  "
        f"max={max(values) / 1000:6.2f}s"
    )


def phases(row):
    return row.get("emit_ms", 0) + row.get("compile_ms", 0) + row.get("run_ms", 0)


def cohort_report(rows, unit, dep_field):
    """The decisive output: does the Microsoft dependency closure explain the cost?"""
    zero = [r["wall_ms"] for r in rows if r.get(dep_field, 0) == 0]
    some = [r["wall_ms"] for r in rows if r.get(dep_field, 0) > 0]
    print(f"  cohort split by {dep_field} ({unit} wall clock)")
    print(stats_line(f"  {dep_field} == 0", zero))
    print(stats_line(f"  {dep_field} >  0", some))
    if zero and some:
        ratio = statistics.median(some) / max(1.0, statistics.median(zero))
        verdict = (
            "dependency loading dominates — target the closure"
            if ratio >= 2.0
            else "flat tax — the cost is NOT the dependency closure"
        )
        print(f"    median ratio deps/no-deps = {ratio:.2f}x  →  {verdict}")
    else:
        print("    only one cohort present — no comparison possible")


def occupancy_report(rows, step_seconds=None, buckets=48):
    """Issue #1829: turn per-row (start_ms, wall_ms) intervals into a picture of WHEN the
    workers were busy.

    Summed wall clock answers "how much work"; it cannot tell a run that is short of
    threads apart from one that is saturated and then trails off single-threaded. Those
    have nothing in common as fixes — the first wants a bigger cap or less memory per
    spawn, the second wants the longest unit dispatched earlier. This section is what
    separates them, and it is why AlRunner.Tests' 1.83x turned out to be "4.0/4 for two
    thirds of the run, then 1.0 for the last 157 s".

    Intervals are the host-observed spawns: a re-exec parent's interval CONTAINS its
    child's, so counting both would double-count. Parents are preferred where present.
    """
    spans = [(r["start_ms"], r["start_ms"] + r.get("wall_ms", 0))
             for r in rows if r.get("start_ms", 0) > 0]
    if len(spans) < 2:
        return
    t0 = min(s for s, _ in spans)
    t1 = max(e for _, e in spans)
    span_s = (t1 - t0) / 1000.0
    if span_s <= 0:
        return

    print("── OCCUPANCY TIMELINE " + "─" * 55)
    print(f"  {len(spans)} intervals over {span_s:.1f}s"
          + (f" (CI step: {step_seconds:.1f}s)" if step_seconds else ""))
    width = (t1 - t0) / buckets
    busy = [0.0] * buckets
    for s, e in spans:
        i = int((s - t0) / width)
        while i < buckets and t0 + i * width < e:
            lo = max(s, t0 + i * width)
            hi = min(e, t0 + (i + 1) * width)
            busy[i] += max(0.0, hi - lo) / width
            i += 1

    peak = max(busy)
    print(f"  bucket = {width / 1000:.1f}s, value = mean concurrent processes, peak = {peak:.2f}")
    for chunk in range(0, buckets, 24):
        row = busy[chunk:chunk + 24]
        print(f"  t={(chunk * width) / 1000:6.0f}s " + "".join(f"{v:5.1f}" for v in row))
    mean = sum(busy) / buckets
    print(f"  mean concurrency over the span      {mean:8.2f}")
    # The tail is the actionable half of the picture: a long stretch below half of peak
    # means work was still queued when it should already have been running.
    tail = 0
    for v in reversed(busy):
        if v > peak / 2:
            break
        tail += 1
    print(f"  trailing buckets below half peak    {tail:8d}  ({tail * width / 1000:.0f}s)")
    if tail * width / 1000 > 0.15 * span_s:
        print("    → RAMP-DOWN: the run ends underloaded. Dispatch the longest unit earlier;")
        print("      raising the thread cap cannot help a stretch that has no work to give it.")
    print()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("path")
    ap.add_argument("--label", default="")
    ap.add_argument(
        "--step-seconds",
        type=float,
        default=None,
        help="wall clock of the CI step, to compute achieved concurrency",
    )
    args = ap.parse_args()

    try:
        with open(args.path) as fh:
            rows = [json.loads(line) for line in fh if line.strip()]
    except FileNotFoundError:
        print(f"phase log '{args.path}' not found — nothing to report")
        return 0

    apps = [r for r in rows if r["kind"] == "app"]
    bundles = [r for r in rows if r["kind"] == "bundle"]
    procs = [r for r in rows if r["kind"] == "process"]
    parents = [r for r in rows if r["kind"] == "process-reexec-parent"]

    print("=" * 78)
    print(f"AL_RUNNER_PHASE_LOG report{' — ' + args.label if args.label else ''}")
    print("=" * 78)
    print(f"records: {len(rows)}  (app={len(apps)} bundle={len(bundles)} "
          f"process={len(procs)} reexec-parent={len(parents)})")
    print()
    print("CAVEAT: since #1818 AlRunner.Tests runs 4-way parallel, so numbers from that")
    print("step are measured UNDER CONTENTION and are not clean per-process costs. The")
    print("cohort split survives contention (it inflates both cohorts); absolute")
    print("per-spawn times do not. Do not quote them as isolated measurements.")
    print()

    # ── the decisive question ────────────────────────────────────────────────
    print("── COHORT SPLIT (the question #1825 was opened to answer) " + "─" * 20)
    if procs:
        cohort_report(procs, "process", "dep_assemblies_loaded")
    if apps:
        print()
        cohort_report(apps, "app", "dep_assemblies_loaded")
    print()

    # ── per-process ──────────────────────────────────────────────────────────
    if procs:
        print("── PER PROCESS " + "─" * 62)
        walls = [r["wall_ms"] for r in procs]
        print(stats_line("wall clock (from OS process start)", walls))
        print(stats_line("BC runtime patches (engine boot)", [r.get("patches_ms", 0) for r in procs]))
        print(stats_line("emit", [r["emit_ms"] for r in procs]))
        print(stats_line("compile", [r["compile_ms"] for r in procs]))
        print(stats_line("test run", [r["run_ms"] for r in procs]))
        # The residual is the proxy for host startup + full-opt JIT: AlRunner.csproj sets
        # <TieredCompilation>false</TieredCompilation> so JmpHooks written at tier-0
        # addresses are not clobbered by tier-1 promotion, and every process pays for it.
        residual = [r["wall_ms"] - r.get("patches_ms", 0) - phases(r) for r in procs]
        print(stats_line("residual (startup + full-opt JIT)", residual))
        rss = [r.get("peak_rss_bytes", 0) for r in procs]
        print(f"  {'peak RSS':<34} mean={statistics.mean(rss) / 2**20:8.0f} MiB  "
              f"max={max(rss) / 2**20:8.0f} MiB")
        total_proc_wall = sum(walls)
        print(f"  {'sum of process wall clock':<34} {total_proc_wall / 1000:8.1f}s")
        if args.step_seconds:
            print(f"  {'step wall clock':<34} {args.step_seconds:8.1f}s")
            print(f"  {'achieved concurrency':<34} "
                  f"{total_proc_wall / 1000 / max(0.001, args.step_seconds):8.2f}x")
        print()

    # A re-exec parent wraps its child, so its interval is the host-observed span of one
    # spawn. Where there are none (single-process steps), fall back to bundle rows, which
    # are the only intervals that exist within one process.
    occupancy_report(parents or procs or bundles, args.step_seconds)

    if parents:
        print("── RE-EXEC PARENTS " + "─" * 58)
        print("  Each runner invocation re-execs itself (DOTNET_ReadyToRun=0, and again")
        print("  after a fresh Cecil rewrite), so one 'spawn' is 2-3 OS processes. These")
        print("  rows wrap their children and are excluded from the totals above.")
        print(stats_line("re-exec parent wall clock", [r["wall_ms"] for r in parents]))
        print(f"  {'parents per completed process':<34} "
              f"{len(parents) / max(1, len(procs)):8.2f}")
        print()

    # ── per-bundle ───────────────────────────────────────────────────────────
    if bundles:
        print("── PER BUNDLE " + "─" * 63)
        print(stats_line("wall clock", [r["wall_ms"] for r in bundles]))
        print(stats_line("emit+compile+run", [phases(r) for r in bundles]))
        print(stats_line("overhead outside app work", [r["wall_ms"] - phases(r) for r in bundles]))
        print()

    # ── per-app ──────────────────────────────────────────────────────────────
    if apps:
        print("── PER APP (one emitted module) " + "─" * 45)
        print(stats_line("wall clock", [r["wall_ms"] for r in apps]))
        print(stats_line("emit", [r["emit_ms"] for r in apps]))
        print(stats_line("compile", [r["compile_ms"] for r in apps]))
        print(stats_line("test run", [r["run_ms"] for r in apps]))
        print(stats_line("residual (wall - phases)", [r["wall_ms"] - phases(r) for r in apps]))
        hits = sum(r["cache_hits"] for r in apps)
        misses = sum(r["cache_misses"] for r in apps)
        print(f"  {'AL-output cache':<34} HIT={hits} MISS={misses}")

        # A quadratic term in bundle size looks completely different from a flat
        # per-app tax and needs a different fix, so make the ordering visible.
        multi = [r for r in apps if r.get("apps_in_bundle", 0) >= 4]
        if multi:
            half = [r for r in multi if r["app_index"] * 2 <= r["apps_in_bundle"]]
            rest = [r for r in multi if r["app_index"] * 2 > r["apps_in_bundle"]]
            if half and rest:
                fh = statistics.mean([r["wall_ms"] for r in half]) / 1000
                sh = statistics.mean([r["wall_ms"] for r in rest]) / 1000
                print(f"  {'first half vs second half':<34} "
                      f"{fh:.2f}s vs {sh:.2f}s  ({sh / max(0.001, fh):.2f}x) "
                      f"— >1.5x suggests a term quadratic in bundle size")

        print()
        print("  top 10 apps by wall clock")
        for r in sorted(apps, key=lambda r: -r["wall_ms"])[:10]:
            print(f"    {r['wall_ms'] / 1000:7.2f}s  "
                  f"emit={r['emit_ms'] / 1000:6.2f}s compile={r['compile_ms'] / 1000:6.2f}s "
                  f"run={r['run_ms'] / 1000:6.2f}s deps={r.get('dep_assemblies_loaded', 0):<3} "
                  f"[{r.get('app_index', 0)}/{r.get('apps_in_bundle', 0)}] {r['app']}")
        print()

    return 0


if __name__ == "__main__":
    sys.exit(main())
