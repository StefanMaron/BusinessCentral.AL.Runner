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
pulling the Microsoft closure).

#1888: that question is answered by the DEP-LOAD VERDICT section, which gates on
the dep-load bundle-stage total (the runner's own direct measurement of time
spent loading dependency assemblies) as a share of total subprocess wall clock.
It is printed AFTER the cohort split, not instead of it — the cohort split (spawns
grouped by whether they touched any dependencies at all) is still genuinely useful
as a description of spawn SIZE, but it compares whole-process wall clock, which
also contains engine boot, host startup, full-opt JIT, register-source-dirs, emit,
compile and run — none of which is dependency loading. Treating that ratio as the
answer to "does the closure explain the cost" was a false positive: on run
31753389980 (main @ 8bc39224, BC 28.1) it read "median ratio deps/no-deps = 3.33x
→ dependency loading dominates" while the dep-load stage itself was 34.98s of
1292.7s of subprocess wall clock — 2.7%, and just 6.50s (0.50%) once narrowed to
the actual Microsoft closure (Base Application, System Application, Business
Foundation, Application, System) rather than the runner's own fixture apps that
also load through the same code path.

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
    """Descriptive only — NOT a dependency-loading verdict (#1888).

    Splits spawns by whether they touched any dependencies at all and compares
    their WHOLE-PROCESS/WHOLE-APP wall clock. That whole-process number also
    contains engine boot, host startup, full-opt JIT, register-source-dirs,
    emit, compile and run — none of which is dependency loading — so a large
    ratio here says only "spawns that loaded deps were also bigger spawns",
    not "dependency loading is the cost". See print_dep_load_verdict() for the
    actual verdict, which gates on the runner's own direct measurement of time
    spent loading dependency assemblies (the dep-load bundle stage).
    """
    zero = [r["wall_ms"] for r in rows if r.get(dep_field, 0) == 0]
    some = [r["wall_ms"] for r in rows if r.get(dep_field, 0) > 0]
    print(f"  cohort split by {dep_field} ({unit} wall clock)")
    print(stats_line(f"  {dep_field} == 0", zero))
    print(stats_line(f"  {dep_field} >  0", some))
    if zero and some:
        ratio = statistics.median(some) / max(1.0, statistics.median(zero))
        print(f"    median ratio deps/no-deps = {ratio:.2f}x  "
              f"(descriptive only — see DEP-LOAD VERDICT below for causation)")
    else:
        print("    only one cohort present — no comparison possible")


# #1888: the threshold the dep-load verdict gates on. The dep-load bundle stage
# is the runner's own direct measurement (see AlRunner/Infrastructure/PhaseLog.cs
# NoteDepAssembliesLoaded / the "dep-load:<Name>" stage marks in DependencyLoader)
# of wall clock spent loading dependency assemblies. 25% is a deliberately blunt
# bar: on the run that motivated this fix (31753389980, main @ 8bc39224, BC 28.1)
# the real share was 2.7% overall / 0.50% for the Microsoft closure alone, so
# anything remotely close to "dominates" should clear 25% by a wide margin —
# the bar exists to stop a single noisy leg from tripping the verdict, not to
# split hairs near a boundary.
DEP_LOAD_DOMINATES_THRESHOLD = 0.25


def dep_load_totals(rows):
    """Sum every 'dep-load' / 'dep-load:<name>' bundle-stage entry across ALL
    bundle rows. This is the number #1888 exists to gate the verdict on instead
    of the cohort ratio — see cohort_report()'s docstring for why the ratio is
    not a valid stand-in for it.
    """
    return sum(
        ms
        for r in rows
        if r.get("kind") == "bundle"
        for name, ms in r.get("stages", {}).items()
        if name == "dep-load" or name.startswith("dep-load:")
    )


def print_dep_load_verdict(bundle_rows, proc_rows):
    """#1888: THE decisive output for "does the Microsoft dependency closure
    explain the cost", replacing cohort_report()'s former (false-positive-prone)
    verdict line. Gates on the dep-load bundle-stage total as a share of total
    subprocess wall clock, not on the cohort's whole-process ratio.
    """
    dep_load_ms = dep_load_totals(bundle_rows)
    total_wall_ms = sum(r["wall_ms"] for r in proc_rows)
    if total_wall_ms <= 0:
        print("  no process wall clock recorded — no verdict possible")
        return
    share = dep_load_ms / total_wall_ms
    verdict = (
        "dependency loading dominates — target the closure"
        if share >= DEP_LOAD_DOMINATES_THRESHOLD
        else "flat tax — the cost is NOT the dependency closure"
    )
    print(f"  dep-load stage total = {dep_load_ms / 1000:.2f}s of "
          f"{total_wall_ms / 1000:.2f}s subprocess wall clock "
          f"({100 * share:.1f}%)  →  {verdict}")


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
    # #1888: cohort split is descriptive-only (spawn size), never a dep-loading
    # verdict — see cohort_report()'s docstring. The actual verdict is printed
    # separately, below, gated on the dep-load stage's share of wall clock.
    print("── COHORT SPLIT (descriptive — spawn size, NOT a dependency-loading verdict) " + "─" * 2)
    if procs:
        cohort_report(procs, "process", "dep_assemblies_loaded")
    if apps:
        print()
        cohort_report(apps, "app", "dep_assemblies_loaded")
    print()

    print("── DEP-LOAD VERDICT (the question #1825 was opened to answer) " + "─" * 16)
    if bundles and procs:
        print_dep_load_verdict(bundles, procs)
    else:
        print("  need both bundle and process rows for a verdict — none available")
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

    # ── bundle stages (#1828) ────────────────────────────────────────────────
    # "overhead outside app work" above is the number #1828 was opened on: 152.3s
    # of a 357.8s runner-extras leg. This section is what it decomposes into.
    stage_bundles = [r for r in bundles if r.get("stages")]
    if stage_bundles:
        print("── BUNDLE STAGES (work inside the bundle, outside every app group) " + "─" * 10)
        # `dep-load:<Name>` etc. — group members under their prefix so one expensive
        # dependency is distinguishable from a dozen mediocre ones, and the group
        # total is still one line.
        groups = {}
        for r in stage_bundles:
            for name, ms in r["stages"].items():
                head, _, tail = name.partition(":")
                g = groups.setdefault(head, {"total": 0, "members": {}})
                g["total"] += ms
                if tail:
                    g["members"][tail] = g["members"].get(tail, 0) + ms

        app_wall = sum(r["wall_ms"] for r in apps)
        bundle_wall = sum(r["wall_ms"] for r in stage_bundles)
        staged = sum(g["total"] for g in groups.values())
        for head, g in sorted(groups.items(), key=lambda kv: -kv[1]["total"]):
            share = 100 * g["total"] / max(1, bundle_wall)
            print(f"  {head:<34} {g['total'] / 1000:8.2f}s  {share:5.1f}% of bundle wall")
            for name, ms in sorted(g["members"].items(), key=lambda kv: -kv[1])[:12]:
                print(f"    {name:<32} {ms / 1000:8.2f}s")
        print(f"  {'STAGES TOTAL':<34} {staged / 1000:8.2f}s")
        print(f"  {'app groups (emit+compile+run+…)':<34} {app_wall / 1000:8.2f}s")
        # The honesty line. Named stages that do not add up to the bundle wall leave
        # this positive, which is the signal to add another mark rather than to
        # believe the breakdown is complete.
        print(f"  {'UNATTRIBUTED (add a stage mark)':<34} "
              f"{(bundle_wall - app_wall - staged) / 1000:8.2f}s")
        print()

    # ── app stages (#1861) ───────────────────────────────────────────────────
    # #1828's "overhead outside app work" decomposed the bundle's own turn; this is
    # the same idea one level down. #1861 measured `run_ms − Σ reported test
    # duration` at ~4.8s per app group, flat across 23 wildly different app groups
    # (110.5s of a 128.8s "test run" phase, 51% of the whole runner-extras step) —
    # a floor being paid per group regardless of how much test content it holds.
    # This section is what that floor decomposes into.
    stage_apps = [r for r in apps if r.get("stages")]
    if stage_apps:
        print("── APP STAGES (work inside each app group's run turn) " + "─" * 24)
        totals = {}
        per_app_totals = []
        for r in stage_apps:
            staged_here = 0
            for name, ms in r["stages"].items():
                totals[name] = totals.get(name, 0) + ms
                staged_here += ms
            per_app_totals.append(staged_here)

        run_total = sum(r["run_ms"] for r in stage_apps)
        staged_total = sum(totals.values())
        n = len(stage_apps)
        for name, total in sorted(totals.items(), key=lambda kv: -kv[1]):
            share = 100 * total / max(1, run_total)
            print(f"  {name:<34} {total / 1000:8.2f}s  mean/app={total / n / 1000:6.3f}s  "
                  f"{share:5.1f}% of run_ms")
        print(f"  {'STAGES TOTAL':<34} {staged_total / 1000:8.2f}s")
        print(f"  {'run_ms (all app groups)':<34} {run_total / 1000:8.2f}s")
        # The honesty line, same contract as the bundle-stage section: named stages
        # that do not add up to run_ms leave this positive, which is the signal to
        # add another mark rather than to believe the breakdown is complete.
        print(f"  {'UNATTRIBUTED (add a stage mark)':<34} "
              f"{(run_total - staged_total) / 1000:8.2f}s"
              f"  ({100 * (run_total - staged_total) / max(1, run_total):5.1f}% of run_ms)")
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
