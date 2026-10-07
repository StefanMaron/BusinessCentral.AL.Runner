#!/usr/bin/env python3
"""scripts/corpus-version-symbols.py mirrors the corpus ci.yml include list (#5382)."""
import subprocess
import sys
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parent.parent / "scripts" / "corpus-version-symbols.py"


def run(v):
    p = subprocess.run([sys.executable, str(SCRIPT), v], capture_output=True, text=True)
    return p.returncode, p.stdout.strip()


class T(unittest.TestCase):
    def test_each_major_gets_every_symbol_up_to_its_own(self):
        self.assertEqual(run("27.0"), (0, "BC27PLUS"))
        self.assertEqual(run("27.5.46862.53931"), (0, "BC27PLUS"))
        self.assertEqual(run("28.5.54151.55132"), (0, "BC27PLUS,BC28PLUS"))
        self.assertEqual(run("29.0.54011.55816"), (0, "BC27PLUS,BC28PLUS,BC29PLUS"))

    def test_an_underivable_version_is_refused_not_answered_with_nothing(self):
        for v in ("26.0", "", "latest", "x.1"):
            rc, out = run(v)
            self.assertEqual((rc, out), (3, ""), v)

    def test_the_workflow_passes_the_symbols_in_every_corpus_step(self):
        wf = (Path(__file__).resolve().parent.parent / ".github/workflows/bc-tests.yml").read_text()
        # the full-corpus step, the xmlport step and the isolation probes
        self.assertGreaterEqual(wf.count('--preprocessor-symbols "$CORPUS_SYMBOLS"'), 3)
        self.assertGreaterEqual(wf.count("scripts/corpus-version-symbols.py"), 3)


if __name__ == "__main__":
    unittest.main()
