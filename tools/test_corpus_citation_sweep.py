#!/usr/bin/env python3
"""Unit tests for tools/corpus-citation-sweep.py (#3674).

The sweep's decision is one function over data -- which merged runner PRs cite a
corpus PR that is not MERGED -- so that is what is driven here, with the parser
and the network read injected. The prefilter gets its own checks: it is the only
thing in that file that looks at a body itself, and a prefilter that drops a body
the real parser would have matched is a silent false zero.

Run: python3 tools/test_corpus_citation_sweep.py
"""
from __future__ import annotations

import importlib.util
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location(
    "corpus_citation_sweep", os.path.join(HERE, "corpus-citation-sweep.py"))
sweep = importlib.util.module_from_spec(_spec)
sys.modules[_spec.name] = sweep
_spec.loader.exec_module(sweep)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


class _Entry:
    def __init__(self, state, detail=""):
        self.state = state
        self.detail = detail


URL = "https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/153"

print("corpus-citation-sweep.py -- the prefilter")

check("a body carrying the marker is handed to the parser",
      sweep.has_marker(f"Closes #1\n\nCorpus-PR: {URL}\n"))
check("...case-insensitively, because the gate's regex is",
      sweep.has_marker("corpus-pr: whatever"))
check("...and a MALFORMED marker line is still handed over -- refusing to read it "
      "is the parser's job, not the prefilter's",
      sweep.has_marker("Corpus-PR: [#228](https://example.invalid/pull/228)"))
check("a body with no marker is skipped", not sweep.has_marker("Closes #1\n"))
check("...and a Corpus-NA declaration is not a marker",
      not sweep.has_marker("Corpus-NA: tooling only"))
check("an empty or missing body is not a marker",
      not sweep.has_marker("") and not sweep.has_marker(None))

print("\ncorpus-citation-sweep.py -- the finding")

PRS = [
    {"number": 2318, "title": "a", "mergedAt": "2026-09-01T00:00:00Z",
     "body": f"Closes #1\n\nCorpus-PR: {URL}\n"},
    {"number": 3428, "title": "b", "mergedAt": "2026-09-08T00:00:00Z",
     "body": "Corpus-PR: https://github.com/StefanMaron/"
             "BusinessCentral.AL.Language.Tests/pull/272\n"},
    {"number": 3000, "title": "c", "mergedAt": "2026-09-02T00:00:00Z",
     "body": "Corpus-NA: tooling only\n"},
]

STATES = {153: _Entry("CLOSED-UNMERGED", "closed without merging"),
          272: _Entry("MERGED", "adjudicated")}


def _numbers(body):
    import re
    return [int(m) for m in re.findall(r"/pull/(\d+)", body)], ""


_found = sweep.unmerged_citations(PRS, _numbers, lambda n: STATES[n])
check("a merged runner PR citing a closed-unmerged corpus PR is a finding",
      [(f[0]["number"], f[1], f[2]) for f in _found]
      == [(2318, 153, "CLOSED-UNMERGED")], repr(_found))
check("...and one citing a MERGED corpus PR is not",
      all(f[0]["number"] != 3428 for f in _found), repr(_found))
check("...and a body with no declaration never reaches the parser",
      all(f[0]["number"] != 3000 for f in _found), repr(_found))

# An open corpus PR on an ALREADY MERGED runner PR is a finding too: the runner
# change shipped and nothing has adjudicated it yet. That is #3428/#3430's shape,
# and it is why this sweep asks for MERGED rather than for mergeable.
_open = sweep.unmerged_citations(
    [PRS[0]], _numbers, lambda n: _Entry("MERGEABLE", "open, legs green"))
check("a merged runner PR whose corpus PR is still OPEN is a finding",
      len(_open) == 1 and _open[0][2] == "MERGEABLE", repr(_open))

# A body the parser refuses is reported, never dropped: dropping it is the silent
# zero the whole sweep exists to avoid.
_refused = sweep.unmerged_citations(
    [PRS[0]], lambda body: (None, "malformed Corpus-PR line"), lambda n: _Entry("MERGED"))
check("a body the parser refuses is reported as UNREADABLE, not skipped",
      len(_refused) == 1 and _refused[0][2] == "UNREADABLE"
      and _refused[0][1] is None, repr(_refused))

print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
