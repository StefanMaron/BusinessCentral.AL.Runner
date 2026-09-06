#!/usr/bin/env python3
"""Unit tests for scripts/check-collection-weights.py (issue #1887's loud guard).

RED before #1887: this module did not exist. GREEN proves the two directions the fix
needs: a heavy-but-unmeasured collection is flagged (positive), a genuinely light
unmeasured one is not — nor is a heavy one that is simply already in the table, however
stale its recorded number (negative, both cases — see the script's own header for why
drift-on-a-present-entry is deliberately out of scope here).

Run: python3 scripts/tests/check-collection-weights.test.py
"""
import importlib.util
import re
import tempfile
import textwrap
import unittest
from pathlib import Path

SCRIPT_PATH = Path(__file__).resolve().parent.parent / "check-collection-weights.py"
_spec = importlib.util.spec_from_file_location("check_collection_weights", SCRIPT_PATH)
ccw = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(ccw)


ORDERER_FIXTURE = textwrap.dedent("""\
    public sealed class CollectionCostOrderer
    {
        public const int UnmeasuredWeightSeconds = 30;

        public static readonly IReadOnlyDictionary<string, int> MeasuredWeightSeconds =
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["HeavyKnownTests"] = 200,
                ["LightKnownTests"] = 21,
            };
    }
""")


class LoadTableTests(unittest.TestCase):
    def test_parses_entries_and_unmeasured_weight_from_the_real_cs_syntax(self):
        with tempfile.NamedTemporaryFile("w", suffix=".cs", delete=False) as f:
            f.write(ORDERER_FIXTURE)
            path = f.name
        table, unmeasured = ccw.load_table(path)
        self.assertEqual(unmeasured, 30)
        self.assertEqual(table, {"HeavyKnownTests": 200, "LightKnownTests": 21})

    def test_raises_when_the_dictionary_cannot_be_found(self):
        with tempfile.NamedTemporaryFile("w", suffix=".cs", delete=False) as f:
            f.write("// no orderer here\n")
            path = f.name
        with self.assertRaises(ValueError):
            ccw.load_table(path)


class FindMissingHeavyTests(unittest.TestCase):
    """The load-bearing logic: an absent-but-heavy collection must be flagged; a present
    one, or a genuinely light one, must not — proving the guard targets the specific
    failure #1887 found rather than flagging everything."""

    def test_flags_a_heavy_collection_missing_from_the_table(self):
        # Positive: this is exactly InstallSeedDepCompanyCacheTests's shape before #1887 —
        # absent from the table, well above the 60s (2x30) threshold.
        observed = {"HeavyKnownTests": 200, "NewHeavyUnlistedTests": 196}
        table = {"HeavyKnownTests": 200}
        missing = ccw.find_missing_heavy(observed, table, threshold_seconds=60)
        self.assertEqual([cls for cls, _ in missing], ["NewHeavyUnlistedTests"])

    def test_does_not_flag_a_light_collection_missing_from_the_table(self):
        # Negative: an unlisted class under threshold is the "~66 collections totalling
        # 2.8s" case the orderer's file header describes as harmless — must not trip
        # the guard just because it isn't listed.
        observed = {"HeavyKnownTests": 200, "TinyUnlistedTests": 5}
        table = {"HeavyKnownTests": 200}
        missing = ccw.find_missing_heavy(observed, table, threshold_seconds=60)
        self.assertEqual(missing, [])

    def test_does_not_flag_a_heavy_collection_already_in_the_table(self):
        # Negative: a present entry, however stale its recorded number, is not this
        # guard's job — see the script's module docstring on why drift-on-present is
        # deliberately not checked (BC-leg-to-BC-leg variance would make it noisy).
        observed = {"HeavyKnownTests": 340}
        table = {"HeavyKnownTests": 200}
        missing = ccw.find_missing_heavy(observed, table, threshold_seconds=60)
        self.assertEqual(missing, [])

    def test_sorts_multiple_offenders_heaviest_first(self):
        observed = {"MediumUnlistedTests": 70, "VeryHeavyUnlistedTests": 250}
        missing = ccw.find_missing_heavy(observed, table={}, threshold_seconds=60)
        self.assertEqual(
            [cls for cls, _ in missing], ["VeryHeavyUnlistedTests", "MediumUnlistedTests"])


class LegLoadFactorTests(unittest.TestCase):
    """#3103: the gate is compared against a wall clock measured on shared runners, so the
    same collection landed at 59.4s on one PR's run and 63.7s on another's and produced
    opposite verdicts. The leg's own clock is recoverable from the run itself — every
    collection that IS in the table measures long on the same leg that measures an unlisted
    one long — so calibrate against that instead of trusting raw seconds."""

    def test_is_one_when_the_leg_measures_exactly_what_the_table_records(self):
        table = {f"C{i}Tests": 100 for i in range(10)}
        observed = dict(table)
        self.assertAlmostEqual(ccw.leg_load_factor(observed, table), 1.0, places=3)

    def test_tracks_a_uniformly_slow_leg(self):
        # Every measured collection ran 40% long; that is the leg, not the collections.
        table = {f"C{i}Tests": 100 for i in range(10)}
        observed = {k: v * 1.4 for k, v in table.items()}
        self.assertAlmostEqual(ccw.leg_load_factor(observed, table), 1.4, places=3)

    def test_never_tightens_the_gate_on_a_fast_leg(self):
        # Deliberately constructed: a leg 40% FASTER than the table. Scaling the bar down
        # would fail collections that pass today, so the factor floors at 1.0 -- this
        # change may only ever loosen the gate, never tighten it.
        table = {f"C{i}Tests": 100 for i in range(10)}
        observed = {k: v * 0.6 for k, v in table.items()}
        self.assertAlmostEqual(ccw.leg_load_factor(observed, table), 1.0, places=3)

    def test_ignores_a_few_wildly_drifted_entries(self):
        # The table is hand-maintained and some entries are stale by design (the script's
        # own header says drift-on-a-present-entry is out of scope). A median over the
        # pairs must not be dragged by two of them.
        table = {f"C{i}Tests": 100 for i in range(10)}
        observed = {k: v * 1.2 for k, v in table.items()}
        observed["C0Tests"] = 1000
        observed["C1Tests"] = 5
        self.assertAlmostEqual(ccw.leg_load_factor(observed, table), 1.2, places=3)

    def test_falls_back_to_one_without_enough_paired_collections(self):
        # Two pairs is not a leg measurement, it is two numbers. Refuse to calibrate.
        table = {"C0Tests": 100, "C1Tests": 100}
        observed = {"C0Tests": 500, "C1Tests": 500}
        self.assertAlmostEqual(ccw.leg_load_factor(observed, table), 1.0, places=3)

    def test_is_clamped_so_a_broken_table_cannot_disable_the_gate(self):
        table = {f"C{i}Tests": 10 for i in range(10)}
        observed = {k: 10_000 for k in table}
        self.assertAlmostEqual(
            ccw.leg_load_factor(observed, table), ccw.MAX_LOAD_FACTOR, places=3)


class ClassifyMissingTests(unittest.TestCase):
    """Two bands, not one: at/above the advisory line the collection is worth recording;
    only at/above the fail line does it cost enough tail to justify a red required check."""

    TABLE = {"HeavyKnownTests": 200}

    def test_a_borderline_collection_is_advisory_not_failing(self):
        failing, advisory = ccw.classify_missing(
            {"BorderlineUnlistedTests": 63.7}, self.TABLE,
            advisory_seconds=60, fail_seconds=90)
        self.assertEqual(failing, [])
        self.assertEqual([cls for cls, _ in advisory], ["BorderlineUnlistedTests"])

    def test_a_genuinely_heavy_collection_still_fails(self):
        failing, advisory = ccw.classify_missing(
            {"NewHeavyUnlistedTests": 196}, self.TABLE,
            advisory_seconds=60, fail_seconds=90)
        self.assertEqual([cls for cls, _ in failing], ["NewHeavyUnlistedTests"])
        self.assertEqual(advisory, [])

    def test_a_light_collection_is_neither(self):
        failing, advisory = ccw.classify_missing(
            {"TinyUnlistedTests": 5}, self.TABLE,
            advisory_seconds=60, fail_seconds=90)
        self.assertEqual((failing, advisory), ([], []))

    def test_a_present_entry_is_neither_however_heavy(self):
        failing, advisory = ccw.classify_missing(
            {"HeavyKnownTests": 900}, self.TABLE,
            advisory_seconds=60, fail_seconds=90)
        self.assertEqual((failing, advisory), ([], []))


class MainExitCodeTests(unittest.TestCase):
    """End-to-end through main(): a missing heavy collection in a real trx must fail the
    process (exit 1), a clean one must not (exit 0) — the actual CI contract."""

    TRX_TEMPLATE = textwrap.dedent("""\
        <?xml version="1.0" encoding="UTF-8"?>
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <TestDefinitions>
            <UnitTest id="{id}">
              <TestMethod className="AlRunner.Tests.{cls}" name="SomeFact" />
            </UnitTest>
          </TestDefinitions>
          <Results>
            <UnitTestResult testId="{id}" startTime="2026-01-01T00:00:00.000+00:00"
                             endTime="{end}" />
          </Results>
        </TestRun>
    """)

    def _write_trx(self, cls, seconds):
        end = f"2026-01-01T00:0{seconds // 60}:{seconds % 60:02d}.000+00:00" if seconds < 600 \
            else f"2026-01-01T00:{seconds // 60:02d}:{seconds % 60:02d}.000+00:00"
        with tempfile.NamedTemporaryFile("w", suffix=".trx", delete=False) as f:
            f.write(self.TRX_TEMPLATE.format(id="11111111-1111-1111-1111-111111111111",
                                              cls=cls, end=end))
            return f.name

    def _write_orderer(self, entries):
        body = "\n".join(f'["{k}"] = {v},' for k, v in entries.items())
        with tempfile.NamedTemporaryFile("w", suffix=".cs", delete=False) as f:
            f.write("public const int UnmeasuredWeightSeconds = 30;\n")
            f.write("MeasuredWeightSeconds = new Dictionary<string, int> {\n"
                     + body + "\n};\n")
            return f.name

    def test_exits_nonzero_when_a_heavy_class_is_unlisted(self):
        trx = self._write_trx("SomeVeryHeavyUnlistedTests", seconds=196)
        orderer = self._write_orderer({})
        rc = self._run(trx, orderer)
        self.assertEqual(rc, 1)

    def test_exits_zero_when_every_heavy_class_is_listed(self):
        trx = self._write_trx("KnownHeavyTests", seconds=196)
        orderer = self._write_orderer({"KnownHeavyTests": 196})
        rc = self._run(trx, orderer)
        self.assertEqual(rc, 0)

    @staticmethod
    def _run(trx, orderer):
        import sys
        old_argv = sys.argv
        try:
            sys.argv = ["check-collection-weights.py", trx, "--orderer", orderer]
            return ccw.main()
        finally:
            sys.argv = old_argv


class StraddleTheThresholdTests(unittest.TestCase):
    """#3103, end to end through main(). Every fixture here is built deliberately: the
    verdict must be a property of the collection, not of how loaded GitHub's shared runner
    happened to be while it was measured."""

    HEADER = ('<?xml version="1.0" encoding="UTF-8"?>\n'
              '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">\n')

    def _write_trx(self, per_class_seconds):
        """A trx with one test per class, each running for the given number of seconds."""
        defs, results = [], []
        for i, (cls, secs) in enumerate(per_class_seconds.items()):
            tid = f"{i:08d}-1111-1111-1111-111111111111"
            defs.append(f'    <UnitTest id="{tid}">'
                        f'<TestMethod className="AlRunner.Tests.{cls}" name="F" /></UnitTest>')
            total_ms = int(round(secs * 1000))
            end = ("2026-01-01T"
                   f"{total_ms // 3_600_000:02d}:"
                   f"{total_ms // 60_000 % 60:02d}:"
                   f"{total_ms // 1000 % 60:02d}."
                   f"{total_ms % 1000:03d}+00:00")
            results.append(f'    <UnitTestResult testId="{tid}" '
                           f'startTime="2026-01-01T00:00:00.000+00:00" endTime="{end}" />')
        xml = (self.HEADER + "  <TestDefinitions>\n" + "\n".join(defs)
               + "\n  </TestDefinitions>\n  <Results>\n" + "\n".join(results)
               + "\n  </Results>\n</TestRun>\n")
        with tempfile.NamedTemporaryFile("w", suffix=".trx", delete=False) as f:
            f.write(xml)
            return f.name

    def _write_orderer(self, entries):
        body = "\n".join(f'["{k}"] = {v},' for k, v in entries.items())
        with tempfile.NamedTemporaryFile("w", suffix=".cs", delete=False) as f:
            f.write("public const int UnmeasuredWeightSeconds = 30;\n")
            f.write("MeasuredWeightSeconds = new Dictionary<string, int> {\n"
                    + body + "\n};\n")
            return f.name

    @staticmethod
    def _run(trx, orderer, extra=None):
        import contextlib, io, sys
        old_argv = sys.argv
        buf = io.StringIO()
        try:
            sys.argv = (["check-collection-weights.py", trx, "--orderer", orderer]
                        + list(extra or []))
            with contextlib.redirect_stdout(buf):
                rc = ccw.main()
        finally:
            sys.argv = old_argv
        return rc, buf.getvalue()

    # A reference-speed leg: twelve measured collections that each land exactly on their
    # recorded value, so leg_load_factor is 1.0 and the bars are the unscaled 60s/90s.
    REFERENCE_TABLE = {f"Known{i}Tests": 100 for i in range(12)}

    def _reference_leg(self, extra, load=1.0):
        observed = {k: v * load for k, v in self.REFERENCE_TABLE.items()}
        observed.update(extra)
        return self._write_trx(observed), self._write_orderer(self.REFERENCE_TABLE)

    def test_the_same_collection_gets_the_same_verdict_on_both_sides_of_60s(self):
        # The measured instance from #3103: SuiteAbortOnTimeoutTests, untouched by either
        # PR, summed 59.4s on one run and 63.7s on another and produced opposite verdicts.
        verdicts = {}
        for secs in (59.4, 63.7):
            trx, orderer = self._reference_leg({"SuiteAbortOnTimeoutTests": secs})
            verdicts[secs] = self._run(trx, orderer)[0]
        self.assertEqual(verdicts[59.4], verdicts[63.7])
        self.assertEqual(verdicts[63.7], 0)

    def test_a_borderline_collection_is_still_named_in_the_output(self):
        # Not failing is not the same as going quiet: #1887's whole complaint was drift
        # nobody saw. It must still be reported, and as a GitHub annotation so it lands in
        # the checks UI rather than 400 lines down a log.
        trx, orderer = self._reference_leg({"SuiteAbortOnTimeoutTests": 63.7})
        rc, out = self._run(trx, orderer)
        self.assertEqual(rc, 0)
        self.assertIn("SuiteAbortOnTimeoutTests", out)
        self.assertIn("::warning", out)

    def test_a_genuinely_heavy_unlisted_collection_still_fails_the_run(self):
        # #1887's own case, InstallSeedDepCompanyCacheTests at ~196s. This is the property
        # the fix may not give up.
        trx, orderer = self._reference_leg({"InstallSeedDepCompanyCacheTests": 196})
        rc, out = self._run(trx, orderer)
        self.assertEqual(rc, 1)
        self.assertIn("InstallSeedDepCompanyCacheTests", out)

    def test_identical_seconds_are_read_against_the_legs_own_clock(self):
        # The load-bearing assertion, and the one a no-op implementation cannot pass: the
        # SAME 100s measurement fails on a leg running at the table's speed and passes on a
        # leg where every measured collection also ran 60% long -- because on that leg 100s
        # is ~62s of work, under the bar. Nothing about the collection changed; only the
        # box did.
        fast_trx, orderer = self._reference_leg({"UnlistedTests": 100}, load=1.0)
        slow_trx, _ = self._reference_leg({"UnlistedTests": 100}, load=1.6)
        self.assertEqual(self._run(fast_trx, orderer)[0], 1)
        self.assertEqual(self._run(slow_trx, orderer)[0], 0)

    def test_a_slow_leg_cannot_hide_a_collection_that_is_heavy_at_reference_speed(self):
        # The negative direction of the same knob: 400s on a leg running 60% long is still
        # 250s of real work, far above the bar, so calibration must not swallow it.
        trx, orderer = self._reference_leg({"UnlistedTests": 400}, load=1.6)
        rc, out = self._run(trx, orderer)
        self.assertEqual(rc, 1)
        self.assertIn("UnlistedTests", out)


class WorkflowFailThresholdTests(unittest.TestCase):
    """The failing line bc-tests.yml actually runs with, and why it is 2.5x rather than 3x.

    The script's own DEFAULT_FAIL_MULTIPLE stays 3 for any other caller; the corpus leg
    passes --fail-threshold explicitly. That number is a judgement about THIS repository's
    weight table, so it is pinned against the real table rather than the fixture one -- a
    later edit that drops the flag, or moves it back to 3x, fails here.
    """

    REPO = Path(__file__).resolve().parent.parent.parent
    WORKFLOW = REPO / ".github" / "workflows" / "bc-tests.yml"
    ORDERER = REPO / "AlRunner.Tests" / "CollectionCostOrderer.cs"

    FAIL_MULTIPLE = 2.5

    # Measured in #3103: the same untouched collection (SuiteAbortOnTimeoutTests) summed
    # 59.4s on one run and 63.7s on another. Properties of a shared GitHub runner, not of
    # the weight table -- so they stay fixed as the table grows.
    OBSERVED_NOISE_TOP = 63.7
    OBSERVED_NOISE_SPREAD = 4.3

    def _workflow_fail_threshold(self):
        text = self.WORKFLOW.read_text()
        line = [ln for ln in text.splitlines()
                if "check-collection-weights.py" in ln and not ln.strip().startswith("#")]
        self.assertEqual(len(line), 1,
                         "expected exactly one check-collection-weights.py invocation "
                         f"in bc-tests.yml, found {len(line)}")
        m = re.search(r"--fail-threshold\s+(\d+(?:\.\d+)?)", line[0])
        self.assertIsNotNone(
            m, "bc-tests.yml must pass --fail-threshold explicitly: the script's 3x default "
               "is looser than this repository's weight table can afford (see below)")
        return float(m.group(1))

    def test_the_workflow_passes_two_and_a_half_times_unmeasured_weight(self):
        _, unmeasured = ccw.load_table(str(self.ORDERER))
        self.assertEqual(self._workflow_fail_threshold(), self.FAIL_MULTIPLE * unmeasured)

    def test_the_chosen_line_clears_the_observed_noise_but_three_x_gives_up_too_much(self):
        """The two-sided argument, measured against the real table, not asserted.

        Below: 2x (60s) sits UNDER the top of the observed noise cluster -- the same
        untouched collection summed 59.4s and 63.7s on two runs -- which is the flake
        #3103 fixes. Above: at 3x (90s) the gate stops being enforced over 8 entries of
        the real table, among them CountBaselineIntegrationTests at 84s, which is one of
        the two collections #1887 originally FOUND with this gate. A line that cannot
        catch its own founding case is decoration.
        """
        table, unmeasured = ccw.load_table(str(self.ORDERER))
        chosen = self.FAIL_MULTIPLE * unmeasured
        weights = sorted(table.values())

        # The noise half, and the only part built from constants rather than from the
        # table: 2x sits UNDER the top of the observed cluster, the chosen line clears it.
        # OBSERVED_NOISE_TOP/SPREAD are measurements from #3103, not table properties, so
        # this pair cannot drift as collections are added.
        self.assertGreater(chosen, self.OBSERVED_NOISE_TOP)
        self.assertLess(2 * unmeasured, self.OBSERVED_NOISE_TOP)
        self.assertGreater(chosen - self.OBSERVED_NOISE_TOP, 2 * self.OBSERVED_NOISE_SPREAD)

        # The founding-case half. Deliberately a BAND, not == 84: the point is that this
        # collection is enforced at the chosen line and would not be at 3x, which stays
        # true however the entry is re-measured, and does not go red because someone else
        # added a table entry.
        founding = table["CountBaselineIntegrationTests"]
        self.assertGreaterEqual(
            founding, chosen,
            "CountBaselineIntegrationTests is one of the two collections #1887 found with "
            "this gate; if it no longer sits at or above the failing line, the argument for "
            f"{self.FAIL_MULTIPLE}x has to be re-made rather than quietly kept")
        self.assertLess(
            founding, 3 * unmeasured,
            "if this entry has grown past 3x, the 'a 90s line cannot catch its own founding "
            "case' argument no longer holds and this test should be revisited")

        # ...and the slice of the table that stops being enforced at 3x is a real one, not
        # a rounding difference. A floor, not an exact count, for the same reason.
        enforced_at = lambda bar: sum(1 for w in weights if w >= bar)
        given_up = enforced_at(chosen) - enforced_at(3 * unmeasured)
        self.assertGreaterEqual(
            given_up, 5,
            f"moving the line to 3x would give up enforcement over only {given_up} entries; "
            "if the table has shifted that far, 2.5x may no longer be buying anything")

    def test_an_eighty_four_second_collection_fails_at_the_workflows_line(self):
        """End to end through the script, so the number above is not just arithmetic."""
        bar = self._workflow_fail_threshold()
        reference = {f"Known{i}Tests": 100 for i in range(12)}
        helper = StraddleTheThresholdTests("run")
        trx = helper._write_trx({**reference, "CountBaselineIntegrationTests": 84})
        orderer = helper._write_orderer(reference)

        rc_at_workflow_line, out = helper._run(trx, orderer, extra=["--fail-threshold", str(bar)])
        self.assertEqual(rc_at_workflow_line, 1)
        self.assertIn("CountBaselineIntegrationTests", out)

        # The same run at the script's untuned 3x default: reported, but not failing.
        rc_at_default, out_default = helper._run(trx, orderer)
        self.assertEqual(rc_at_default, 0)
        self.assertIn("CountBaselineIntegrationTests", out_default)


if __name__ == "__main__":
    unittest.main()
