#!/usr/bin/env python3
"""Unit tests for scripts/ms-bucket-total.py (issue #3409).

The combined total across a multi-bucket run is the deliverable of
.github/workflows/ms-surface.yml, and the ways it can be quietly WRONG are the point of
these tests, not the happy path:

  * a bucket that produced no number must not be silently dropped from a total that then
    looks complete (the whole reason the script is driven by the REQUESTED list rather
    than by whatever directories exist);
  * a runner exit code that means "did not finish" must not be counted as a measurement,
    even though a JUnit file exists;
  * a total well short of what the set is known to hold must say so.

RED before #3409: the script did not exist. Run: python3 scripts/tests/ms-bucket-total.test.py
"""
import importlib.util
import os
import tempfile
import unittest
from pathlib import Path

SCRIPT_PATH = Path(__file__).resolve().parent.parent / "ms-bucket-total.py"
_spec = importlib.util.spec_from_file_location("ms_bucket_total", SCRIPT_PATH)
mbt = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(mbt)


def junit(tests, failures=0, errors=0, skipped=0):
    return (f'<?xml version="1.0" encoding="utf-8"?>\n'
            f'<testsuites tests="{tests}" failures="{failures}" errors="{errors}" '
            f'skipped="{skipped}" time="1.0"></testsuites>\n')


class Fixture:
    """A results/ tree plus the requested-bucket file, as ms-bucket.yml writes them."""

    def __init__(self, tmp):
        self.root = Path(tmp)
        self.results = self.root / "results"
        self.results.mkdir()
        self.requested = []

    def bucket(self, name, *, rc=1, elapsed=60, xml=None):
        self.requested.append(name)
        if xml is None and rc is not None:
            xml = junit(100, failures=40)
        d = self.results / name.translate(mbt.SLUG_TRANSLATION)
        d.mkdir(parents=True, exist_ok=True)
        (d / "bucket.txt").write_text(name + "\n")
        if rc is not None:
            (d / "rc.txt").write_text(f"{rc}\n")
            (d / "elapsed.txt").write_text(f"{elapsed}\n")
        if xml is not None:
            (d / "junit.xml").write_text(xml)
        return self

    def missing(self, name):
        """A bucket that was requested and left NOTHING behind — the runner died first."""
        self.requested.append(name)
        return self

    def buckets_file(self):
        p = self.root / "buckets.txt"
        p.write_text("\n".join(self.requested) + "\n")
        return str(p)

    def run(self, *extra):
        return mbt.main(["--results", str(self.results), "--buckets", self.buckets_file(),
                         "--bc-version", "28.4.1.2", "--test-data", "true", *extra])


class SlugTests(unittest.TestCase):
    def test_bucket_names_with_spaces_become_directory_names(self):
        # Tests-Cash Flow, Tests-Data Exchange and friends are real bucket names.
        self.assertEqual("Tests-Cash_Flow", mbt.slug("Tests-Cash Flow"))
        self.assertEqual("Tests-ERM", mbt.slug("Tests-ERM"))


class CollectTests(unittest.TestCase):
    def test_totals_are_summed_across_buckets(self):
        with tempfile.TemporaryDirectory() as tmp:
            f = Fixture(tmp)
            f.bucket("Tests-A", xml=junit(1000, failures=400, errors=10, skipped=5))
            f.bucket("Tests-B", xml=junit(500, failures=100))
            records = mbt.collect(str(f.results), f.requested)
            combined = mbt.sum_totals(records)

            self.assertEqual(1500, combined["tests"])
            self.assertEqual(500, combined["failures"])
            self.assertEqual(10, combined["errors"])
            self.assertEqual(5, combined["skipped"])
            # 1000-400-10-5 = 585, plus 500-100 = 400.
            self.assertEqual(985, combined["passed"])

    def test_a_requested_bucket_with_no_directory_is_a_record_not_an_omission(self):
        with tempfile.TemporaryDirectory() as tmp:
            f = Fixture(tmp)
            f.bucket("Tests-A", xml=junit(100))
            f.missing("Tests-Vanished")
            records = mbt.collect(str(f.results), f.requested)

            self.assertEqual(2, len(records))
            self.assertEqual("Tests-Vanished", records[1]["bucket"])
            self.assertFalse(records[1]["measured"])
            self.assertIsNone(records[1]["totals"])

    def test_a_junit_file_with_a_crash_exit_code_is_not_a_measurement(self):
        # The trap: the file EXISTS, so a naive "did it write JUnit" check says yes.
        with tempfile.TemporaryDirectory() as tmp:
            f = Fixture(tmp)
            f.bucket("Tests-A", rc=139, xml=junit(30))
            records = mbt.collect(str(f.results), f.requested)

            self.assertFalse(records[0]["measured"])
            self.assertEqual(30, records[0]["totals"]["tests"])

    def test_exit_codes_0_and_1_both_count_as_measured(self):
        with tempfile.TemporaryDirectory() as tmp:
            f = Fixture(tmp)
            f.bucket("Tests-A", rc=0, xml=junit(10))
            f.bucket("Tests-B", rc=1, xml=junit(10, failures=3))
            self.assertEqual([True, True], [r["measured"] for r in mbt.collect(str(f.results), f.requested)])


class ShortfallTests(unittest.TestCase):
    def test_no_expectation_never_reports_a_shortfall(self):
        self.assertEqual((False, 0), mbt.shortfall(1, 0))

    def test_a_total_below_95_percent_is_short(self):
        short, missing = mbt.shortfall(30_000, 40_530)
        self.assertTrue(short)
        self.assertEqual(10_530, missing)

    def test_a_small_version_to_version_drift_is_not_short(self):
        self.assertFalse(mbt.shortfall(40_100, 40_530)[0])


class VerdictTests(unittest.TestCase):
    def test_every_bucket_measured_exits_zero(self):
        with tempfile.TemporaryDirectory() as tmp:
            f = Fixture(tmp)
            f.bucket("Tests-A").bucket("Tests-Cash Flow")
            self.assertEqual(0, f.run())

    def test_one_bucket_without_a_number_exits_one(self):
        with tempfile.TemporaryDirectory() as tmp:
            f = Fixture(tmp)
            f.bucket("Tests-A").missing("Tests-B")
            self.assertEqual(1, f.run())

    def test_an_empty_request_list_is_an_error_not_an_empty_success(self):
        # The vacuous case: zero buckets means every bucket trivially produced a number.
        with tempfile.TemporaryDirectory() as tmp:
            f = Fixture(tmp)
            self.assertEqual(1, f.run())

    def test_a_shortfall_does_not_fail_the_run(self):
        with tempfile.TemporaryDirectory() as tmp:
            f = Fixture(tmp)
            f.bucket("Tests-A", xml=junit(10))
            self.assertEqual(0, f.run("--expected-tests", "40530"))


class SummaryTests(unittest.TestCase):
    def _summary(self, f, *extra):
        out = f.root / "summary.md"
        rc = f.run("--step-summary", str(out), *extra)
        return rc, out.read_text()

    def test_the_table_carries_every_bucket_and_a_total_row(self):
        with tempfile.TemporaryDirectory() as tmp:
            f = Fixture(tmp)
            f.bucket("Tests-A", xml=junit(1000, failures=400))
            f.bucket("Tests-B", xml=junit(500, failures=100))
            _, md = self._summary(f)

            self.assertIn("`Tests-A`", md)
            self.assertIn("`Tests-B`", md)
            self.assertIn("**1500**", md)
            self.assertIn("Every one of the 2 requested bucket(s) produced a number.", md)

    def test_a_bucket_with_no_number_is_named_in_the_summary(self):
        with tempfile.TemporaryDirectory() as tmp:
            f = Fixture(tmp)
            f.bucket("Tests-A", xml=junit(1000))
            f.missing("Tests-Vanished")
            rc, md = self._summary(f)

            self.assertEqual(1, rc)
            self.assertIn("Tests-Vanished", md)
            self.assertIn("No number from 1 of 2 bucket(s)", md)
            self.assertIn("covers only the buckets that produced one", md)

    def test_a_shortfall_is_stated_with_both_numbers(self):
        with tempfile.TemporaryDirectory() as tmp:
            f = Fixture(tmp)
            f.bucket("Tests-A", xml=junit(30_000, failures=10_000))
            _, md = self._summary(f, "--expected-tests", "40530")

            self.assertIn("short of the 40530", md)
            self.assertIn("10530 unaccounted for", md)

    def test_a_single_bucket_gets_a_sentence_rather_than_a_one_row_table(self):
        # The nightly runs one bucket and already printed its own block; a table repeating
        # it is noise.
        with tempfile.TemporaryDirectory() as tmp:
            f = Fixture(tmp)
            f.bucket("Tests-SMB", xml=junit(1027, failures=432))
            _, md = self._summary(f)

            self.assertNotIn("| bucket |", md)
            self.assertIn("1027 tests — 595 pass", md)


if __name__ == "__main__":
    unittest.main(verbosity=1)
