#!/usr/bin/env python3
"""A document-count figure in docs/ must name the BC build it was measured on (#3816).

The count is build-dependent and the spread is not small. Measured over the
ground-truth bundles on a provisioned box:

    27.5.46862.53931   System Application   1166
    28.1.49838.53910   System Application   1218
    28.1.49838.54308   System Application   1218
    28.4.53241.54407   System Application   1220
    (Business Foundation is 70 on all four)

All four are fully covered -- the object set differs between builds, nothing is
missing. So a bare "1,218 documents" is not wrong, it is UNDER-SPECIFIED, which
is the same class as a zero quoted without its scope.

#3816's own worked example: PR #3815 published "1,212 objects compared across
nine kinds", a figure that reconciles against no denominator at all because its
per-kind rows mixed declared counts with compared counts. That number never
reached this repository -- docs/ has always said 1,218 -- so what this guard
protects is the half that IS here: the count being traceable to a build.

WHAT THIS DOES NOT DO. It does not check the count is right; the harness does
that, and `Every_object_in_a_compared_kind_really_was_compared` is the assertion
that would fail if it were wrong. This only checks the figure says what it is a
count OF, which no test could otherwise notice going stale.

Usage: python3 tools/test_metadata_doc_counts_state_their_build.py
Exit 0 every count names a build, 1 one does not, 3 the docs could not be read
-- which is not a pass (guards-need-a-third-state.md).
"""

import pathlib
import re
import sys

REPO = pathlib.Path(__file__).resolve().parents[1]
DOC = REPO / "docs/dependency-metadata-from-bc.md"

# A four-part BC build, e.g. 28.1.49838.53910. A bare "28.1" is exactly the
# under-specification this guard exists to refuse, so the pattern requires all four.
BUILD = re.compile(r"\b\d+\.\d+\.\d+\.\d+\b")

# The emitted-document counts this file publishes. Matched on the digits so a
# reworded sentence around them still gets checked.
#
# COMMA-FORMATTED SPELLINGS ONLY, deliberately. The doc also carries one
# unpunctuated `objects=1218` (verbatim tool output from an isolation
# experiment), which this guard therefore cannot see. Adding "1218" here would
# red the file today, and the fix is not available from here: that sentence
# says "on BC 28.1.49838", a THREE-part prefix that is ambiguous between the two
# provisioned builds 28.1.49838.53910 and 28.1.49838.54308 -- and both emit
# 1218, so the count does not disambiguate it either. Resolving it needs whoever
# ran that experiment, not a guess. Tracked rather than guessed: #4533.
COUNTS = ("1,218", "1,166", "1,220")

failures = []
passes = 0


def spans(text):
    """The spans a reader would quote a figure out of: table rows, and sentences.

    A markdown table row is one span (a row is read across, and its header row
    is where a column-wide qualifier such as "documents (28.1...)" legitimately
    lives -- so header and body rows are joined into one span per table).
    Everything else splits on sentence-ending punctuation followed by
    whitespace. A 4-part build ends in digits and is never followed by a space,
    so it cannot be split through.
    """
    out = []
    for block in re.split(r"\n\s*\n", text):
        table, prose = [], []
        for line in block.split("\n"):
            if line.lstrip().startswith("|"):
                table.append(line)
            else:
                prose.append(line)
        if table:
            out.append("\n".join(table))
        if prose:
            # Unwrap first. This file hard-wraps at ~95 columns, so splitting
            # per line would cut a sentence at its wrap point and report the
            # tail as a bare count -- a false positive on every wrapped
            # sentence, which is most of them.
            out.extend(re.split(r"(?<=[.!?:])\s+", " ".join(prose)))
    return [s for s in out if s.strip()]


def check(name, ok, detail=""):
    global passes
    if ok:
        passes += 1
        print(f"  ok   {name}")
    else:
        failures.append(name)
        print(f"  FAIL {name}")
        if detail:
            print(f"       {detail}")


def main():
    if not DOC.exists():
        print(f"::error::{DOC} does not exist -- cannot measure", file=sys.stderr)
        return 3
    try:
        text = DOC.read_text(encoding="utf-8")
    except OSError as exc:
        print(f"::error::{DOC} could not be read ({exc}) -- NOT a pass", file=sys.stderr)
        return 3
    if not text.strip():
        print(f"::error::{DOC} is empty -- cannot measure", file=sys.stderr)
        return 3

    lines = text.split("\n")
    print(f"metadata doc counts -- {len(lines)} lines of {DOC.name}")

    # Reachability first, and it must be ALL of them, not any.
    #
    # MEASURED, not assumed: an "any" form was absorbed by the obvious mutation.
    # Reverting the #3816 fix deletes the paragraph carrying 1,166 and 1,220, so
    # those two checks stopped EXISTING rather than failing -- the suite dropped
    # from 5 checks to 3 and still reported success. A guard that quietly shrinks
    # is indistinguishable from a clean file, which is the defect this file is
    # about, one level up.
    missing = [c for c in COUNTS if c not in text]
    check("every document count this guard is about is still in the file",
          not missing,
          f"missing {missing} -- the spread paragraph was removed or reworded, so "
          f"the checks for those counts would silently stop running")
    if missing:
        print(f"\n{passes} passed, {len(failures)} failed")
        return 1

    # The unit is the SENTENCE or TABLE ROW carrying the count -- not the
    # paragraph, and certainly not the file.
    #
    # MEASURED, in this order, because each granularity was absorbed by the
    # mutation that killed the next one:
    #
    #   file-wide   -- passes on any document naming any version anywhere.
    #   paragraph   -- stripping every build from the per-build spread sentence
    #                  ("1,166 on 27.5, 1,218 on both 28.1 builds, 1,220 on 28.4")
    #                  left the guard GREEN, because that sentence shares a
    #                  paragraph with the denominator sentence, which still named
    #                  28.1.49838.53910. One build anywhere in a paragraph
    #                  satisfied all three counts in it.
    #
    # A reader quotes a sentence or a row, not a paragraph, so that is the span
    # that has to stand alone.
    for count in COUNTS:
        owning = [s for s in spans(text) if count in s]
        bare = [s for s in owning if not BUILD.search(s)]
        check(f"every sentence or row carrying {count} names a full BC build",
              not bare,
              f"{len(bare)} of {len(owning)} span(s) carrying {count} name no "
              f"4-part build; first: {bare[0].strip()[:90] if bare else ''!r}")

    check("the counts are labelled as documents, not left as a bare number",
          re.search(r"documents?\b", text, re.I) is not None)

    # BUILD requires FOUR parts, and on the shipped doc a 2- or 3-part pattern
    # would agree with it on every span -- so loosening that line fails silently.
    # This pins the discrimination on a constructed span rather than on the doc,
    # which is the only way to separate them (a reviewer of #4532 raised it).
    #
    # It is not hypothetical here: `28.1.49838` is ambiguous between the two
    # provisioned builds 28.1.49838.53910 and 28.1.49838.54308, which is exactly
    # the under-specification #3816 is about.
    loose = "System Application emits 1,218 documents on 28.1.49838."
    check("a 3-part build does not satisfy the build requirement",
          not BUILD.search(loose),
          f"BUILD matched {loose!r} -- a 2- or 3-part version now counts as naming "
          f"a build, which is the under-specification this guard exists to refuse")
    tight = "System Application emits 1,218 documents on 28.1.49838.53910."
    check("...and a 4-part build does satisfy it",
          BUILD.search(tight) is not None,
          f"BUILD did not match {tight!r} -- the pattern no longer recognises a "
          f"full build, so every span would fail regardless of what it says")

    print()
    print(f"{passes} passed, {len(failures)} failed")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
