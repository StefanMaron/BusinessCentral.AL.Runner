#!/usr/bin/env python3
"""Plain-Python unit tests for scripts/phase-log-report.py (#1888).

No test framework wired for Python scripts in this repo yet (mirrors
scripts/tests/coverage-gen.test.js's own convention: plain assert, PASS/FAIL
printed per case, run manually — `python3 scripts/tests/phase-log-report.test.py`).
Not currently invoked from any CI workflow; see the #1888 PR body for why that's
an explicit, stated gap rather than a silent one.

#1888 fixed a false positive: cohort_report() used to compare whole-PROCESS wall
clock between the dep_assemblies_loaded==0/>0 cohorts and label a >=2x ratio
"dependency loading dominates — target the closure". That is not evidence about
dependency loading specifically — whole-process wall clock also contains engine
boot, JIT, register-source-dirs, emit, compile and run. The negative test below
(test_print_dep_load_verdict_negative_matches_production_shape) reproduces the
exact production shape that motivated the fix (run 31753389980, main @ 8bc39224,
BC 28.1: cohort ratio 3.33x, dep-load stage only 2.7% of wall clock) and asserts
the new verdict function does NOT call it a dependency-loading finding. Without
this negative case, a test suite for this bug proves nothing — the positive case
alone would pass against the OLD, buggy cohort-ratio-gated logic too.
"""
import contextlib
import importlib.util
import io
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
MODULE_PATH = os.path.join(HERE, "..", "phase-log-report.py")

spec = importlib.util.spec_from_file_location("phase_log_report", MODULE_PATH)
plr = importlib.util.module_from_spec(spec)
spec.loader.exec_module(plr)

passed = 0
failed = 0


def test(name, fn):
    global passed, failed
    try:
        fn()
        print(f"  PASS  {name}")
        passed += 1
    except Exception as e:  # noqa: BLE001 - test harness, want to catch everything
        print(f"  FAIL  {name}\n        {e}")
        failed += 1


def captured(fn, *args, **kwargs):
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        fn(*args, **kwargs)
    return buf.getvalue()


print("dep_load_totals")


def test_dep_load_totals_sums_exact_and_prefixed_names():
    rows = [
        {"kind": "bundle", "stages": {"dep-load": 100, "dep-load:Foo": 200, "other": 999}},
        {"kind": "bundle", "stages": {"dep-load:Bar": 50}},
        {"kind": "app", "stages": {"dep-load:ShouldNotCount": 12345}},  # wrong kind
        {"kind": "bundle", "stages": {}},
    ]
    total = plr.dep_load_totals(rows)
    assert total == 350, f"expected 350, got {total}"


test("sums 'dep-load' and 'dep-load:<name>' stages across bundle rows only", test_dep_load_totals_sums_exact_and_prefixed_names)


def test_dep_load_totals_zero_when_no_dep_load_stages():
    rows = [{"kind": "bundle", "stages": {"register-source-dirs": 500}}]
    assert plr.dep_load_totals(rows) == 0


test("returns 0 when no dep-load stage is present (not a crash, not a default nonzero)", test_dep_load_totals_zero_when_no_dep_load_stages)


print()
print("print_dep_load_verdict")


def test_verdict_positive_dependency_loading_dominates():
    # Positive: dep-load really is the majority of wall clock.
    bundles = [{"kind": "bundle", "stages": {"dep-load:Base Application": 4000}}]
    procs = [{"wall_ms": 10000}]
    out = captured(plr.print_dep_load_verdict, bundles, procs)
    assert "40.0%" in out, out
    assert "dependency loading dominates — target the closure" in out, out
    assert "flat tax" not in out, out


test("dep-load stage >= threshold share of wall clock prints the 'dominates' verdict", test_verdict_positive_dependency_loading_dominates)


def test_verdict_negative_matches_production_shape():
    # Negative — THE bug this issue is about. Reproduces the real production
    # shape from run 31753389980 (main @ 8bc39224, BC 28.1): a large cohort
    # ratio (3.33x, computed separately by cohort_report, not asserted here)
    # coexists with a dep-load stage that is a tiny share of total wall clock.
    # A correct verdict function must say "flat tax", not "dominates" — the
    # old cohort-ratio-gated logic would have said "dominates" here, which is
    # exactly the false positive #1888 reports.
    zero_cohort = [{"wall_ms": 11440} for _ in range(50)]  # dep_assemblies_loaded == 0
    some_cohort = [{"wall_ms": 38110} for _ in range(18)]  # dep_assemblies_loaded > 0
    procs = zero_cohort + some_cohort
    total_wall = sum(r["wall_ms"] for r in procs)
    assert total_wall == 1257980, total_wall

    # cohort ratio really is >= 2.0 here (3.33x in the real run) — confirm the
    # premise of the false positive is present before proving the fix rejects it.
    ratio = 38110 / 11440
    assert ratio >= 2.0, f"test setup doesn't reproduce a large cohort ratio: {ratio}"

    # dep-load stage totals to ~2.7% of total wall clock, same as the real run.
    dep_load_ms = round(total_wall * 0.027)
    bundles = [{"kind": "bundle", "stages": {"dep-load:Base Application": dep_load_ms}}]

    out = captured(plr.print_dep_load_verdict, bundles, procs)
    assert "dependency loading dominates" not in out, (
        f"false positive reproduced — verdict wrongly blamed the closure:\n{out}"
    )
    assert "flat tax — the cost is NOT the dependency closure" in out, out
    assert "2.7%" in out, out


test(
    "large cohort ratio + tiny dep-load share does NOT print the false 'dominates' verdict (the whole bug)",
    test_verdict_negative_matches_production_shape,
)


def test_verdict_no_process_wall_clock_prints_no_crash_message():
    out = captured(plr.print_dep_load_verdict, [{"kind": "bundle", "stages": {"dep-load": 10}}], [])
    assert "no verdict possible" in out, out
    assert "dominates" not in out, out


test("zero total wall clock degrades to an explicit 'no verdict possible', not a ZeroDivisionError", test_verdict_no_process_wall_clock_prints_no_crash_message)


print()
print("cohort_report (must no longer emit a verdict itself)")


def test_cohort_report_is_descriptive_only_even_with_large_ratio():
    # Same large-ratio shape as the production negative case above. cohort_report
    # must describe it (median ratio line) without concluding causation — that
    # conclusion is print_dep_load_verdict's job now, gated on a different signal.
    rows = (
        [{"wall_ms": 11440, "dep_assemblies_loaded": 0} for _ in range(50)]
        + [{"wall_ms": 38110, "dep_assemblies_loaded": 3} for _ in range(18)]
    )
    out = captured(plr.cohort_report, rows, "process", "dep_assemblies_loaded")
    assert "3.33x" in out, out
    assert "dependency loading dominates" not in out, (
        f"cohort_report still concludes a verdict from the ratio alone:\n{out}"
    )
    assert "flat tax — the cost is NOT the dependency closure" not in out, (
        "cohort_report should not print either verdict phrasing — that's print_dep_load_verdict's job"
    )
    assert "descriptive only" in out, out


test("cohort_report prints the ratio but draws no dependency-loading conclusion from it", test_cohort_report_is_descriptive_only_even_with_large_ratio)


print()
print(f"{passed} passed, {failed} failed")
sys.exit(1 if failed else 0)
