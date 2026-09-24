#!/usr/bin/env python3
"""One property of `tests/expectations/metadata-equivalence/allowlist.json`
reasons, from #4513, which a reader can get wrong silently.

NO OBJECT-SCOPING LANGUAGE. An entry is keyed on `member` alone -- the schema has
no object field -- so it suppresses that member's difference on EVERY object.
Six entries said their members joined entries "already declared for this
object", which is not a claim the manifest can express. It misleads in the
hiding direction: a reader who believes an entry is object-scoped will not
expect it to mask the same member somewhere unrelated.

#4513's second half -- a 2,077-char derivation copied verbatim into all six,
leaving 83-149 chars unique -- was fixed in the same pull request and is
deliberately NOT guarded here. Measured while trying to: sharing a template
across entries that share one scope decision is this file's established
convention, at 17 groups covering ~130 entries and ~69,500 duplicated
characters, and most of those groups are byte-identical entries with NO unique
tail at all, which is a reasonable way to say "these members share one reason".
Every threshold separating the #4513 shape from the convention (700, 800, 900,
1200 chars) condemned a different arbitrary slice of the file, so any number
here would be a free parameter dressed as a rule. The remedy for a bloated
reason is review; `Doc:` pointers are already checked by
tools/test_doc_pointers.py.

Usage: python3 tools/test_allowlist_reason_hygiene.py
Exit 0 the property holds, 1 it is violated, 3 the manifest could not be read --
which is not a pass (guards-need-a-third-state.md).
"""

import json
import pathlib
import re
import sys

REPO = pathlib.Path(__file__).resolve().parents[1]
MANIFEST = REPO / "tests/expectations/metadata-equivalence/allowlist.json"

# Phrases asserting the ENTRY's scope is one object. The manifest cannot express
# that, so any of them is a false statement about the entry's own reach.
#
# Deliberately narrow, and the narrowing was forced by a false positive:
# `MetaQuery.#captionML.<presence>` reads "The SAME object as MetaQuery.CaptionML
# under a second signature", meaning the same .NET object INSTANCE -- accurate,
# and nothing to do with AL object scoping. A bare word match flagged it. So a
# hit requires the word next to a scope verb, or a reach phrase like "every
# member of that object" -- which is the shape my own first rewrite of these six
# entries used, so the fixtures below pin both.
SCOPING = (
    r"(?:declared|already declared|applies|apply|scoped|suppress\w*|limited)"
    r"[^.]{0,40}\b(?:this|that|the same)\s+object\b",
    r"\bfor (?:this|that) object\b",
    r"\bon (?:this|that) object only\b",
    r"\b(?:every|each|all|any)\s+\w+(?:\s+\w+){0,2}\s+of\s+(?:this|that)\s+object\b",
)

# The discrimination above is the whole guard, so it is tested on fixtures rather
# than only against today's manifest: a pattern that stops catching the defect
# looks identical to a manifest that is clean.
DETECTOR_CASES = (
    (True,  "entries already declared for this object, same cause"),
    (True,  "removes the shift for every member of that object at once"),
    (True,  "This entry applies to this object alone."),
    (False, "The SAME object as MetaQuery.CaptionML under a second signature"),
    (False, "the symbol entry for 2516 is an ordinary flat entry stating four properties"),
    (False, "removes the shift for every member of pageextension 2516 at once"),
)

failures = []
passes = 0


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
    if not MANIFEST.exists():
        print(f"::error::{MANIFEST} does not exist -- cannot measure", file=sys.stderr)
        return 3
    try:
        doc = json.loads(MANIFEST.read_text(encoding="utf-8"))
        entries = doc["differences"]
    except (ValueError, KeyError, OSError) as exc:
        print(f"::error::{MANIFEST} could not be read ({exc}) -- NOT a pass",
              file=sys.stderr)
        return 3

    if not entries:
        print("::error::the manifest declares no differences at all -- cannot measure",
              file=sys.stderr)
        return 3

    print(f"allowlist reason hygiene -- {len(entries)} entries")

    scanned = 0
    offenders = []
    for e in entries:
        reason = e.get("reason", "")
        scanned += 1
        for p in SCOPING:
            if re.search(p, reason, re.I):
                offenders.append((e.get("member", "?"), p))
    check("no reason claims object scoping the schema cannot express",
          not offenders,
          "; ".join(f"{m} matches {p!r}" for m, p in offenders[:6]))

    # Reachability, not a formality: a scan narrowed to nothing reports a clean
    # manifest and passes every check above, including the detector fixtures,
    # which run on their own strings and never touch the file. Measured: making
    # the loop iterate an empty list left this suite 8/8 GREEN before this
    # assertion existed.
    check("the scan actually visited every entry",
          scanned == len(entries),
          f"scanned {scanned} of {len(entries)}")

    # The control. Without it the check above is satisfied by a manifest whose
    # reasons are all blank -- vacuously clean, and useless.
    with_reason = [e for e in entries if e.get("reason", "").strip()]
    check("every entry still carries a reason, so the check above had something "
          "to measure",
          len(with_reason) == len(entries),
          f"{len(entries) - len(with_reason)} entrie(s) have an empty reason")

    for want, text in DETECTOR_CASES:
        got = any(re.search(p, text, re.I) for p in SCOPING)
        check(f"detector {'catches' if want else 'passes'}: {text[:52]!r}",
              got == want,
              f"expected {'a hit' if want else 'no hit'}, got the opposite")

    print()
    print(f"{passes} passed, {len(failures)} failed")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
