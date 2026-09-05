#!/usr/bin/env python3
"""Unit tests for scripts/ms-bucket-summary.py (issue #2724).

The manual MS-bucket workflow's deliverable is the job summary: total / pass / fail /
error and wall time, plus every line of the runner's output that means the number is
not what it looks like — a bundle lost to COMPILE FAIL, a resume that carried earlier
attempts forward, a "not run" count. RED before #2724: the script did not exist. GREEN
proves both directions: a JUnit file yields exact numbers and a "measured" verdict
(positive); a missing JUnit, a crash exit code, or a caveat line in the log each change
the verdict or the summary in a way the reader cannot miss (negative).

Run: python3 scripts/tests/ms-bucket-summary.test.py
"""
import importlib.util
import contextlib
import io
import os
import tempfile
import unittest
from pathlib import Path

SCRIPT_PATH = Path(__file__).resolve().parent.parent / "ms-bucket-summary.py"
_spec = importlib.util.spec_from_file_location("ms_bucket_summary", SCRIPT_PATH)
mbs = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(mbs)


JUNIT = """<?xml version="1.0" encoding="utf-8"?>
<testsuites tests="9496" failures="7000" errors="120" skipped="3" time="4321.500">
  <testsuite name="Codeunit134000" tests="2" failures="1" errors="0" skipped="0" time="1.0">
    <testcase name="A" classname="Codeunit134000" time="0.5" />
    <testcase name="B" classname="Codeunit134000" time="0.5"><failure message="x" /></testcase>
  </testsuite>
</testsuites>
"""

CLEAN_LOG = """al-runner — test run summary
=================================================================
Buckets:       1 total
  ran:         1
  compile-fail:0
  exec-fail:   0
Tests:         9496 total
  pass:        2373
  fail:        7000
  error:       120
  skipped:     3
Time:
  AL emit:     412.0s
  C# compile:  88.1s
  test run:    3821.4s
  total:       4321.5s
"""

META = {"bucket": "Tests-ERM", "bc_version": "28.4.53241.54318", "test_data": True, "reader": "v0.1.1"}


class ParseJunitTotalsTests(unittest.TestCase):
    def test_reads_the_root_attributes_and_derives_pass(self):
        totals = mbs.parse_junit_totals(JUNIT)
        self.assertEqual(totals, {"tests": 9496, "failures": 7000, "errors": 120, "skipped": 3, "passed": 2373})

    def test_rejects_a_document_without_a_testsuites_root(self):
        with self.assertRaises(ValueError):
            mbs.parse_junit_totals("<testsuite tests='1' failures='0' errors='0' skipped='0' />")


class ScanLogTests(unittest.TestCase):
    def test_a_clean_log_has_no_caveats_and_keeps_the_summary_block(self):
        scan = mbs.scan_log(CLEAN_LOG)
        self.assertEqual(scan["caveats"], [])
        self.assertIn("Tests:         9496 total", scan["summary_block"])
        self.assertIn("  total:       4321.5s", scan["summary_block"])

    def test_lost_bundles_resumes_and_not_run_lines_are_caveats(self):
        # The per-bundle header is written the way Reporter.PrintPerTest actually writes it —
        # "=== <bundle> \u2014 COMPILE FAIL ===". This test used to feed "COMPILE FAIL  /tmp/..."
        # instead, a shape the runner never emits, so it passed while the pattern it covered
        # matched nothing in a real log (#2779).
        log = (
            "\n=== Tests-ERM \u2014 COMPILE FAIL ===\n"
            "  Tests-ERM: COMPILE-FAIL (3): CS0246 The type or namespace 'X' could not be found\n"
            "resume: a watchdog abort ended this attempt early. Continuing in a fresh process, "
            "skipping 3 codeunit(s) already attempted or hung; 4 resume attempt(s) left after this one.\n"
            "NOT RUN: 1 bundle(s)\n"
            "  (carried from earlier attempt(s): 400 tests, 100 pass, 300 fail, 0 error)\n"
            "  compile-fail:1\n"
            + CLEAN_LOG
        )
        caveats = mbs.scan_log(log)["caveats"]
        self.assertTrue(any("COMPILE FAIL ===" in c for c in caveats), caveats)
        self.assertTrue(any("Tests-ERM" in c for c in caveats), caveats)
        self.assertTrue(any(c.startswith("resume:") for c in caveats), caveats)
        self.assertTrue(any(c.startswith("NOT RUN:") for c in caveats), caveats)
        self.assertTrue(any("carried from earlier attempt" in c for c in caveats), caveats)
        self.assertTrue(any(c.strip() == "compile-fail:1" for c in caveats), caveats)
        # The zero-count summary lines are NOT caveats.
        self.assertFalse(any("compile-fail:0" in c for c in caveats), caveats)
        self.assertFalse(any("exec-fail:   0" in c for c in caveats), caveats)

    def test_the_reason_a_bundle_failed_reaches_the_caveats(self):
        """Actions run 33967273260, verbatim. The summary named a failure and never its cause:
        the reader's own sentence is the one thing that diagnoses it, and it has to survive."""
        log = (
            "[1/1] ms-buckets/Tests-ERM \u2014 1 suites\n"
            "  \u2192 0P/0F/0E across 0 tests, 1 suite errors (59.9s)\n"
            "\n=== Tests-ERM \u2014 EXEC FAIL ===\n"
            "  Tests-ERM: EXEC-FAIL: the backup reader failed (exit 1): block 116504 of MSDA "
            "region is neither mapped by the derived extent list nor padding filler\n"
            "  exec-fail:   1\n"
            + CLEAN_LOG
        )
        caveats = mbs.scan_log(log)["caveats"]
        self.assertTrue(any("EXEC FAIL ===" in c for c in caveats), caveats)
        self.assertTrue(any("block 116504 of MSDA region" in c for c in caveats), caveats)
        self.assertTrue(any(c.strip() == "exec-fail:   1" for c in caveats), caveats)

    def test_ordinary_test_output_is_not_mistaken_for_a_bundle_error(self):
        """The negative: per-test FAIL lines and passing output must not become caveats, or
        every Microsoft bucket run drowns the real ones in thousands of lines."""
        log = (
            "  FAIL  Codeunit 134000.\"Sales Invoice\" (12ms)\n"
            "        Assert.AreEqual failed: expected 3, got 4\n"
            "  compile-fail:0\n"
            + CLEAN_LOG
        )
        self.assertEqual(mbs.scan_log(log)["caveats"], [])


class ComposeTests(unittest.TestCase):
    def test_measured_run_renders_exact_numbers_and_wall_time(self):
        md = mbs.compose(META, mbs.parse_junit_totals(JUNIT), mbs.scan_log(CLEAN_LOG), rc=1, elapsed_s=4500)
        for needle in ("Tests-ERM", "28.4.53241.54318", "| 9496 |", "| 2373 |", "| 7000 |", "| 120 |", "| 3 |",
                       "1h 15m 0s", "with --test-data", "v0.1.1", "exit code 1"):
            self.assertIn(needle, md, needle)
        self.assertIn("No caveats", md)

    def test_the_reader_line_names_the_repository_that_actually_exists(self):
        """#2780. The summary's reader line is what a human follows to go look at the reader,
        and it used to name BusinessCentral.BakReader — the repository's OLD name, which
        resolves only through GitHub's rename redirect. A redirect is not a contract, and
        nothing here covered this line, so the wrong name could sit indefinitely. Both
        directions, because the positive alone would still pass with both names present."""
        md = mbs.compose(META, mbs.parse_junit_totals(JUNIT), mbs.scan_log(CLEAN_LOG), rc=1, elapsed_s=10)
        self.assertIn("Backup reader: BusinessCentral.DbReader v0.1.1.", md)
        self.assertNotIn("BakReader", md)

    def test_no_reader_line_at_all_when_the_run_had_no_reader(self):
        """The negative arm for the line's existence: without --test-data there is no reader,
        and inventing one would mislabel the run."""
        md = mbs.compose(dict(META, test_data=False, reader=None),
                         mbs.parse_junit_totals(JUNIT), mbs.scan_log(CLEAN_LOG), rc=1, elapsed_s=10)
        self.assertNotIn("Backup reader:", md)

    def test_caveats_are_listed_verbatim_so_a_wrong_number_cannot_hide(self):
        scan = mbs.scan_log("NOT RUN: 1 bundle(s)\n" + CLEAN_LOG)
        md = mbs.compose(META, mbs.parse_junit_totals(JUNIT), scan, rc=1, elapsed_s=10)
        self.assertIn("NOT RUN: 1 bundle(s)", md)
        self.assertNotIn("No caveats", md)

    def test_missing_junit_is_reported_as_no_number(self):
        md = mbs.compose(dict(META, test_data=False), None, mbs.scan_log(""), rc=3, elapsed_s=61)
        self.assertIn("no JUnit", md)
        self.assertIn("exit code 3", md)
        self.assertIn("compile", md.lower())
        self.assertIn("without --test-data", md)
        self.assertIn("1m 1s", md)


class VerdictTests(unittest.TestCase):
    def test_zero_and_one_with_junit_are_measured(self):
        self.assertTrue(mbs.measured(rc=0, have_junit=True))
        self.assertTrue(mbs.measured(rc=1, have_junit=True))

    def test_anything_else_is_not(self):
        self.assertFalse(mbs.measured(rc=1, have_junit=False))
        self.assertFalse(mbs.measured(rc=2, have_junit=True))
        self.assertFalse(mbs.measured(rc=3, have_junit=True))
        self.assertFalse(mbs.measured(rc=139, have_junit=True))


class MainTests(unittest.TestCase):
    def _run(self, junit_text, log_text, rc):
        with tempfile.TemporaryDirectory() as d:
            log = Path(d, "run.log"); log.write_text(log_text)
            summary = Path(d, "summary.md")
            args = ["--log", str(log), "--rc", str(rc), "--elapsed", "12", "--bucket", "Tests-ERM",
                    "--bc-version", "28.4.1.2", "--test-data", "true", "--reader", "v0.1.1", "--out", str(summary)]
            if junit_text is not None:
                junit = Path(d, "junit.xml"); junit.write_text(junit_text)
                args += ["--junit", str(junit)]
            code = mbs.main(args)
            return code, summary.read_text()

    def test_measured_run_exits_zero_and_writes_the_summary(self):
        code, md = self._run(JUNIT, CLEAN_LOG, rc=1)
        self.assertEqual(code, 0)
        self.assertIn("| 9496 |", md)

    def test_missing_junit_exits_nonzero_but_still_writes_a_summary(self):
        # The header shape Reporter.PrintPerTest really writes (#2779) — the previous
        # "COMPILE FAIL x" is not a line the runner emits.
        code, md = self._run(None, "=== Tests-ERM \u2014 COMPILE FAIL ===\n", rc=3)
        self.assertEqual(code, 1)
        self.assertIn("no JUnit", md)
        self.assertIn("COMPILE FAIL ===", md)

    def test_explicit_step_summary_path_is_appended_to(self):
        with tempfile.TemporaryDirectory() as d:
            step = Path(d, "step.md"); step.write_text("existing\n")
            log = Path(d, "run.log"); log.write_text(CLEAN_LOG)
            junit = Path(d, "junit.xml"); junit.write_text(JUNIT)
            code = mbs.main(["--log", str(log), "--junit", str(junit), "--rc", "1", "--elapsed", "5",
                             "--bucket", "Tests-ERM", "--bc-version", "28.4.1.2", "--test-data", "true",
                             "--step-summary", str(step)])
            self.assertEqual(code, 0)
            text = step.read_text()
            self.assertTrue(text.startswith("existing\n"))
            self.assertIn("| 9496 |", text)

    def test_appends_to_github_step_summary_when_set(self):
        with tempfile.TemporaryDirectory() as d:
            step = Path(d, "step.md"); step.write_text("existing\n")
            old = os.environ.get("GITHUB_STEP_SUMMARY")
            os.environ["GITHUB_STEP_SUMMARY"] = str(step)
            try:
                code, _ = self._run(JUNIT, CLEAN_LOG, rc=0)
            finally:
                if old is None:
                    del os.environ["GITHUB_STEP_SUMMARY"]
                else:
                    os.environ["GITHUB_STEP_SUMMARY"] = old
            self.assertEqual(code, 0)
            text = step.read_text()
            self.assertTrue(text.startswith("existing\n"))
            self.assertIn("| 9496 |", text)


class KnownBlockerTests(unittest.TestCase):
    """#2780 recognition — what makes the knowingly-red nightly readable instead of noise."""

    # The reader's own words, as they reach the log through the runner's EXEC-FAIL line (#2782).
    READER_REFUSAL = (
        "=== Tests-SMB \u2014 EXEC FAIL ===\n"
        "  Tests-SMB: EXEC-FAIL: the backup reader failed (exit 1): block 116504 of MSDA region is "
        "neither mapped by the derived extent list nor padding filler \u2014 backup layout differs "
        "from the derived model, refusing to guess\n")

    def test_reader_refusal_is_recognised_and_names_the_issue(self):
        blocker = mbs.known_blocker(self.READER_REFUSAL)
        self.assertIsNotNone(blocker)
        self.assertIn("#2780", blocker["detail"])

    def test_an_unrelated_failure_is_not_a_known_blocker(self):
        # The negative direction: without this, ANY red run would claim to be the known one,
        # which is exactly the "says nothing" failure the recognition exists to avoid.
        self.assertIsNone(mbs.known_blocker("=== Tests-SMB \u2014 COMPILE FAIL ===\n"))

    def _run_blocked(self):
        with tempfile.TemporaryDirectory() as d:
            log = Path(d, "run.log"); log.write_text(self.READER_REFUSAL)
            summary = Path(d, "summary.md")
            buf = io.StringIO()
            with contextlib.redirect_stdout(buf):
                code = mbs.main(["--log", str(log), "--rc", "2", "--elapsed", "60",
                                 "--bucket", "Tests-SMB", "--bc-version", "28.4.53241.54318",
                                 "--test-data", "true", "--reader", "v0.1.1", "--out", str(summary)])
            return code, summary.read_text(), buf.getvalue()

    def test_blocked_run_still_reports_no_measurement_and_leads_with_the_blocker(self):
        code, md, _ = self._run_blocked()
        # The exit contract does not change: no number is still no number.
        self.assertEqual(code, 1)
        self.assertIn("Known blocker", md)
        self.assertIn("#2780", md)

    def test_blocked_run_names_the_repository_that_actually_exists(self):
        """The BLOCKED path's own name check. The clean-path test above
        (test_the_reader_line_names_the_repository_that_actually_exists) covers the summary's
        reader line, and it passes whatever the blocker detail says, because that detail only
        appears when a refusal is in the log. That is exactly how "BusinessCentral.BakReader"
        survived here after #2863 removed it everywhere else: no test rendered this path's text.
        Both directions, so a revert to the redirect name cannot pass in silence."""
        _, md, stdout = self._run_blocked()
        self.assertIn("StefanMaron/BusinessCentral.DbReader", md)
        self.assertNotIn("BakReader", md)
        self.assertNotIn("BakReader", stdout)

    def test_blocked_run_does_not_claim_the_fixed_limitation_is_current(self):
        """v0.1.2 reads BC 28.2, 28.3 and 28.4 (#2863 moved READER_TAG to it), so the detail
        must not still assert that --test-data cannot produce a number on those versions. It
        said so until this test existed, which would have printed three wrong facts into CI the
        first time the blocker fired."""
        _, md, _ = self._run_blocked()
        self.assertNotIn("cannot produce a number on", md)
        self.assertIn("v0.1.2", md)

    def test_blocked_run_emits_a_named_annotation(self):
        _, _, stdout = self._run_blocked()
        self.assertIn("::error title=Known blocker", stdout)
        self.assertIn("#2780", stdout)

    def test_unexplained_failure_emits_a_DIFFERENT_annotation(self):
        # A red run whose cause is not understood must not look like the expected one.
        with tempfile.TemporaryDirectory() as d:
            log = Path(d, "run.log"); log.write_text("=== Tests-SMB \u2014 COMPILE FAIL ===\n")
            buf = io.StringIO()
            with contextlib.redirect_stdout(buf):
                code = mbs.main(["--log", str(log), "--rc", "3", "--elapsed", "9",
                                 "--bucket", "Tests-SMB", "--bc-version", "28.4.53241.54318",
                                 "--test-data", "true"])
            out = buf.getvalue()
        self.assertEqual(code, 1)
        self.assertIn("::error title=No measurement", out)
        self.assertNotIn("Known blocker", out)

    def test_a_measured_run_emits_no_annotation_at_all(self):
        with tempfile.TemporaryDirectory() as d:
            log = Path(d, "run.log"); log.write_text(CLEAN_LOG)
            junit = Path(d, "junit.xml"); junit.write_text(JUNIT)
            buf = io.StringIO()
            with contextlib.redirect_stdout(buf):
                code = mbs.main(["--log", str(log), "--junit", str(junit), "--rc", "1",
                                 "--elapsed", "9", "--bucket", "Tests-SMB",
                                 "--bc-version", "28.1.1.1", "--test-data", "true"])
            out = buf.getvalue()
        self.assertEqual(code, 0)
        self.assertNotIn("::error", out)


if __name__ == "__main__":
    unittest.main()
