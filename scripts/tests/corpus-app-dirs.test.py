#!/usr/bin/env python3
"""Unit tests for scripts/corpus-app-dirs.py (issue #2984).

RED before #2984: this module did not exist, and `.github/workflows/bc-tests.yml`
named `tests/al-language/tests/al-language` directly, so a second corpus test app
was checked out by the submodule pin and never executed -- a leg green because it
ran nothing.

GREEN proves both directions:

  positive -- a corpus tree holding several app directories yields ALL of them,
              which is the whole claim: a new test app is executed without anyone
              editing a workflow;
  negative -- a root with no app directory exits 1 rather than printing an empty
              list, because an empty list is exactly the silent-skip this replaces;
              and a directory INSIDE an app is never reported as an app of its own,
              which would hand the runner a path it does not read as one bundle.

Run: python3 scripts/tests/corpus-app-dirs.test.py
"""
import importlib.util
import io
import json
import os
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

SCRIPT_PATH = Path(__file__).resolve().parent.parent / "corpus-app-dirs.py"
_spec = importlib.util.spec_from_file_location("corpus_app_dirs", SCRIPT_PATH)
cad = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(cad)


def make_app(root, rel, app_id="00000000-0000-0000-0000-000000000000"):
    """Create an app directory (app.json + one .al file) at root/rel."""
    d = Path(root) / rel
    d.mkdir(parents=True, exist_ok=True)
    (d / "app.json").write_text(json.dumps({"id": app_id, "name": rel}))
    (d / "Some.Codeunit.al").write_text("codeunit 60000 X { }")
    return d


class EnumerateAppDirs(unittest.TestCase):
    def test_every_app_directory_is_reported_not_just_the_first(self):
        # The defect itself: the corpus offers three apps, all three must come back.
        with tempfile.TemporaryDirectory() as tmp:
            corpus = Path(tmp) / "al-language"
            make_app(corpus, "tests/al-language")
            make_app(corpus, "tests/al-language-internals-fixture")
            make_app(corpus, "tests/al-language-onprem")

            found = cad.enumerate_app_dirs(str(corpus))

            self.assertEqual(
                [
                    str(corpus / "tests/al-language"),
                    str(corpus / "tests/al-language-internals-fixture"),
                    str(corpus / "tests/al-language-onprem"),
                ],
                found,
            )

    def test_subdirectories_of_an_app_are_part_of_that_app(self):
        # An app's category sub-directories are not separate bundles. Reporting
        # `record/` as an app would make the runner compile a fragment with no
        # manifest, and the app's real tests would run under a suite key nothing
        # in the count baseline names.
        with tempfile.TemporaryDirectory() as tmp:
            corpus = Path(tmp) / "al-language"
            app = make_app(corpus, "tests/al-language")
            (app / "record").mkdir()
            (app / "record" / "Rec.Codeunit.al").write_text("codeunit 60001 Y { }")

            self.assertEqual([str(app)], cad.enumerate_app_dirs(str(corpus)))

    def test_a_root_that_is_itself_one_app_is_that_one_app(self):
        # Mirrors ProgramSupport.EnumerateSuites' root-first check, so pointing
        # this at a single app directory keeps meaning what it means today.
        with tempfile.TemporaryDirectory() as tmp:
            app = make_app(tmp, "al-language")
            self.assertEqual([str(app)], cad.enumerate_app_dirs(str(app)))

    def test_src_test_split_counts_as_an_app_without_an_app_json(self):
        # The runner's LooksLikeSuite accepts this shape; if this script did not,
        # a corpus app using it would be enumerated as its own sub-directories.
        with tempfile.TemporaryDirectory() as tmp:
            corpus = Path(tmp) / "al-language"
            split = corpus / "tests" / "split-app"
            (split / "src").mkdir(parents=True)
            (split / "test").mkdir(parents=True)

            self.assertEqual([str(split)], cad.enumerate_app_dirs(str(corpus)))

    def test_dot_directories_are_never_apps(self):
        # `.alpackages` holds Microsoft's own app.json files, and `.github` /
        # `.git` are not AL at all. Reporting one would hand the runner a
        # Microsoft symbol package as a test bundle.
        with tempfile.TemporaryDirectory() as tmp:
            corpus = Path(tmp) / "al-language"
            real = make_app(corpus, "tests/al-language")
            make_app(corpus, "tests/al-language/.alpackages/Microsoft_BaseApp")
            make_app(corpus, ".github/probe")

            self.assertEqual([str(real)], cad.enumerate_app_dirs(str(corpus)))


class MainExitCodes(unittest.TestCase):
    def test_finding_nothing_is_a_hard_failure_not_an_empty_list(self):
        with tempfile.TemporaryDirectory() as tmp:
            empty = Path(tmp) / "al-language"
            (empty / "docs").mkdir(parents=True)

            out, err = io.StringIO(), io.StringIO()
            with redirect_stdout(out), redirect_stderr(err):
                rc = cad.main([str(empty)])

            self.assertEqual(1, rc)
            self.assertEqual("", out.getvalue())
            self.assertIn("no app directory found", err.getvalue())

    def test_a_missing_root_names_the_submodule_as_the_likely_cause(self):
        with tempfile.TemporaryDirectory() as tmp:
            out, err = io.StringIO(), io.StringIO()
            with redirect_stdout(out), redirect_stderr(err):
                rc = cad.main([os.path.join(tmp, "nope")])

            self.assertEqual(1, rc)
            self.assertEqual("", out.getvalue())
            self.assertIn("git submodule update --init", err.getvalue())

    def test_success_prints_one_path_per_line_and_exits_zero(self):
        with tempfile.TemporaryDirectory() as tmp:
            corpus = Path(tmp) / "al-language"
            make_app(corpus, "tests/al-language")
            make_app(corpus, "tests/al-language-onprem")

            out, err = io.StringIO(), io.StringIO()
            with redirect_stdout(out), redirect_stderr(err):
                rc = cad.main([str(corpus)])

            self.assertEqual(0, rc)
            self.assertEqual("", err.getvalue())
            self.assertEqual(
                [
                    str(corpus / "tests/al-language"),
                    str(corpus / "tests/al-language-onprem"),
                ],
                out.getvalue().splitlines(),
            )


if __name__ == "__main__":
    unittest.main(verbosity=2)
