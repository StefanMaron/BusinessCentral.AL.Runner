#!/usr/bin/env python3
"""The closure list preflight uses must BE the C# one, not a copy of it (#3893).

#3893 reports that ArtifactDirState had no non-test consumer, and that
`tools/preflight.py` "re-implements the closure list in Python rather than calling
the C# type". A transcribed list is right on the day it is written and drifts
silently afterwards: both copies look authoritative, and nothing fails when they
disagree -- preflight would keep reporting a directory complete against yesterday's
definition of complete.

So preflight now READS AlRunner/Infrastructure/EngineClosure.cs. These tests pin
the three properties that makes rest on:

  1. the read actually returns the six real files (a regex that matched nothing
     would return empty and every directory would read as complete -- the
     silent-default failure this repo's rules name repeatedly);
  2. what it reads equals the transcription that used to be hardcoded, so this
     change is provably behaviour-preserving today;
  3. an unreadable source file falls back rather than refusing -- preflight runs
     from installed trees with no C# sources, and a hard error there would swap a
     false green for a false red (guards-need-a-third-state.md's constraint).
"""

import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import preflight  # noqa: E402


class EngineClosureSharedTests(unittest.TestCase):

    def test_the_cs_file_exists_where_preflight_looks_for_it(self):
        # A positive control on the path itself. If this moves, test 4's fallback
        # makes every other assertion here pass vacuously, so assert it directly.
        self.assertTrue(os.path.isfile(preflight.ENGINE_CLOSURE_CS),
                        f"EngineClosure.cs not found at {preflight.ENGINE_CLOSURE_CS}")

    def test_reading_the_cs_yields_the_six_closure_files(self):
        got = preflight.engine_closure_files()

        self.assertEqual(6, len(got), f"expected six closure files, got {got}")
        self.assertIn("Microsoft.Dynamics.Nav.Ncl.dll", got)
        self.assertIn("Microsoft.Identity.ServiceEssentials.Core.dll", got)

    def test_the_read_matches_the_transcription_it_replaces(self):
        # Behaviour-preserving today, and the assertion that catches a C# edit
        # nobody mirrored -- which is the entire point of reading rather than
        # copying. When this fails, the C# moved: update the fallback to match.
        self.assertEqual(
            sorted(preflight._ARTIFACT_CLOSURE_FALLBACK),
            sorted(preflight.engine_closure_files()))

    def test_module_level_constant_is_the_read_not_the_fallback_object(self):
        self.assertEqual(sorted(preflight._ARTIFACT_CLOSURE_FALLBACK),
                         sorted(preflight.ARTIFACT_CLOSURE_FILES))

    def test_an_unreadable_source_falls_back_rather_than_returning_empty(self):
        # The constraint: preflight runs from installed trees with no C# sources.
        # Empty here would make every directory read as a complete closure.
        got = preflight.engine_closure_files("/nonexistent/EngineClosure.cs")

        self.assertEqual(preflight._ARTIFACT_CLOSURE_FALLBACK, got)
        self.assertEqual(6, len(got))

    def test_a_readable_file_with_no_dll_names_reads_as_empty_not_as_the_fallback(self):
        # The third state, kept distinct from both the success answer and the
        # unreadable one: "I read it and it said nothing" is a different fact from
        # "I could not read it", and collapsing them hides a broken parse.
        import tempfile
        with tempfile.NamedTemporaryFile("w", suffix=".cs", delete=False) as fh:
            fh.write("namespace X; public static class EngineClosure { }\n")
            path = fh.name
        try:
            self.assertEqual((), preflight.engine_closure_files(path))
        finally:
            os.unlink(path)


if __name__ == "__main__":
    unittest.main()
