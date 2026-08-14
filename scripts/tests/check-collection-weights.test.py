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


if __name__ == "__main__":
    unittest.main()
