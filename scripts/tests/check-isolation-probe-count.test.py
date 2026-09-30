#!/usr/bin/env python3
"""Unit tests for scripts/check-isolation-probe-count.py (#4826).

The probe step runs with --strict, so a failing probe test already reds the leg; this
checker is what reds a probe test that never ran, and says "unmeasurable" (3) rather than
passing when the counts cannot be read.

Run: python3 scripts/tests/check-isolation-probe-count.test.py
"""
import importlib.util
import io
import json
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path

SCRIPT = Path(__file__).resolve().parent.parent / "check-isolation-probe-count.py"
_spec = importlib.util.spec_from_file_location("check_isolation_probe_count", SCRIPT)
cip = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(cip)

FIXTURE = """codeunit 61302 F
{
    Subtype = Test;
    [Test]
    procedure T1() begin end;

    [Test]
    procedure T2() begin end;

    // [Test] in a comment does not start a line with the attribute
    procedure Helper() begin end;
}
"""


class CheckIsolationProbeCount(unittest.TestCase):
    def _setup(self, tmp, tests):
        probe = Path(tmp) / "al-language-isolation-probe"
        (probe / "src").mkdir(parents=True)
        (probe / "src" / "F.Codeunit.al").write_text(FIXTURE)
        counts = Path(tmp) / "counts.json"
        if tests is not None:
            counts.write_text(json.dumps({"suites": {"al-language-isolation-probe": {"tests": tests}}}))
        return probe, counts

    def _run(self, probe, counts):
        with redirect_stdout(io.StringIO()) as out:
            rc = cip.main(["--counts", str(counts), "--probe-dir", str(probe)])
        return rc, out.getvalue()

    def test_declared_tests_counted_from_source(self):
        with tempfile.TemporaryDirectory() as tmp:
            probe, _ = self._setup(tmp, 2)
            self.assertEqual(2, cip.declared_tests(str(probe)))

    def test_every_declared_test_ran_is_zero(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.assertEqual(0, self._run(*self._setup(tmp, 2))[0])

    def test_fewer_ran_than_declared_is_one(self):
        with tempfile.TemporaryDirectory() as tmp:
            rc, out = self._run(*self._setup(tmp, 1))
            self.assertEqual(1, rc)
            self.assertIn("ran 1 test(s), its source declares 2", out)

    def test_unreadable_counts_is_three(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.assertEqual(3, self._run(*self._setup(tmp, None))[0])

    def test_probe_suite_absent_from_counts_is_three(self):
        with tempfile.TemporaryDirectory() as tmp:
            probe, counts = self._setup(tmp, 2)
            counts.write_text(json.dumps({"suites": {"al-language": {"tests": 2}}}))
            self.assertEqual(3, self._run(probe, counts)[0])


if __name__ == "__main__":
    unittest.main(verbosity=2)
