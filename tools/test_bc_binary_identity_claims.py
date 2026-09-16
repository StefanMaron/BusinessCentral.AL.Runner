#!/usr/bin/env python3
"""Pin CLAUDE.md's BC binary-identity example against the artifacts on this box.

CLAUDE.md warns against citing a two-part version label as though it identified a
binary, and states a worked example to make the hazard concrete. That example is
itself a countable claim about files on disk, so it drifts exactly like the claims
the surrounding section is about -- #4221 is the instance: the paragraph named
"27.0, 27.3 and 27.5" as one file when two different 27.5 builds were provisioned.

States (.claude/rules/guards-need-a-third-state.md):

  0  measured and consistent -- OR the artifacts are simply not on this box, a
     loud SKIP rather than a vacuous pass
  1  measured, and the document names a grouping the hashes refuse
  3  COULD NOT MEASURE -- the prose moved, so a claim matched nothing

The split between 0-with-a-SKIP and 3 is the whole design, and it is the constraint
that rule states: a genuinely ABSENT thing stays a pass, only an UNMEASURABLE one
refuses. Those are two different causes here and they have opposite remedies:

  not provisioned   a legitimate state -- pr-gate.yml's tools-tests job uses a bare
                    actions/checkout and never provisions BC, so this is the ORDINARY
                    case on CI. Exiting 3 there would red a required context on every
                    run for a machine that is behaving correctly. Remedy: nothing, or
                    `al-runner provision` if you want the claim checked here.
  prose moved       the document was reworded and this guard can no longer find the
                    claim. Nothing was measured and nothing says so unless it refuses.
                    Remedy: re-read the paragraph and update the pattern.

The same shape as tools/test_matrix_docs_drift.py's corpus-version claim, which SKIPs
when the corpus is not checked out, and as TestArtifacts.SkipIfMissingIn.

Deliberately NOT asserting a total count of distinct binaries: which BC versions a
box has provisioned is a property of the box, not of the repository, so a figure
like "6 distinct" is unpinnable by construction. What IS pinnable is the claim the
document actually makes -- that these named builds are one file, and those named
builds are not.
"""

import hashlib
import os
import re
import sys

EXIT_OK, EXIT_DRIFTED, EXIT_CANNOT_MEASURE = 0, 1, 3

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOC = os.path.join(REPO, "CLAUDE.md")
NCL = "Microsoft.Dynamics.Nav.Ncl.dll"


def artifacts_root():
    env = os.environ.get("AL_RUNNER_ARTIFACTS_ROOT")
    if env:
        return env
    return os.path.join(os.path.expanduser("~"), ".local", "share", "al-runner", "artifacts")


def provisioned(root):
    """build-version -> sha256 of its Ncl.dll, for every artifact directory present."""
    out = {}
    if not os.path.isdir(root):
        return out
    for name in sorted(os.listdir(root)):
        path = os.path.join(root, name, NCL)
        if not os.path.isfile(path):
            continue
        h = hashlib.sha256()
        with open(path, "rb") as fh:
            for chunk in iter(lambda: fh.read(1 << 20), b""):
                h.update(chunk)
        out[name] = h.hexdigest()
    return out


def read_doc():
    with open(DOC, encoding="utf-8") as fh:
        return fh.read()


# Each claim: a label, a regex capturing the build strings the doc groups, and
# whether the doc says that group IS one file or is NOT one file.
SAME = "same"
DIFFERENT = "different"

CLAIMS = [
    (
        "the three builds the doc calls one file",
        re.compile(
            r"builds `(27\.0\.\d+\.\d+)`, `(27\.3\.\d+\.\d+)` and `(27\.5\.\d+\.\d+)` are one\s*\n?\s*file"
        ),
        SAME,
    ),
    (
        "the two 27.5 builds the doc calls distinct",
        re.compile(
            r"`(27\.5\.\d+\.\d+)` \(`[0-9a-f]{8}\u2026`\) differs from `(27\.5\.\d+\.\d+)`"
        ),
        DIFFERENT,
    ),
]


def main():
    doc = read_doc()
    root = artifacts_root()
    have = provisioned(root)

    problems = []
    unmeasurable = []
    skipped = []
    checked = 0

    for label, pattern, relation in CLAIMS:
        found = pattern.findall(doc)
        if not found:
            unmeasurable.append(
                "%s: the sentence stating it no longer matches this guard's pattern "
                "(CLAUDE.md was reworded). Re-read the paragraph and update the regex." % label
            )
            continue

        # findall, not search: `re.search` takes the FIRST match, so a second
        # sentence stating the claim correctly would mask a drifted one further
        # down -- the guard would read the decoy and exit 0. Two matches means
        # this guard can no longer tell which sentence it is pinning, which is
        # unmeasurable rather than a pass. (Found in review of PR #4238 against
        # tools/test_partial_class_counts.py, which has the same exposure.)
        if len(found) > 1:
            unmeasurable.append(
                "%s: the pattern matches %d places in CLAUDE.md, so this guard cannot "
                "tell which one it is pinning. Make the claim unique, or narrow the "
                "pattern." % (label, len(found))
            )
            continue

        builds = list(found[0]) if isinstance(found[0], tuple) else [found[0]]
        missing = [b for b in builds if b not in have]
        if missing:
            # Absent, not unmeasurable: this box simply does not have those builds.
            skipped.append(
                "%s: not provisioned here (%s)" % (label, ", ".join(missing))
            )
            continue

        checked += 1
        hashes = {b: have[b] for b in builds}
        distinct = set(hashes.values())

        if relation is SAME and len(distinct) != 1:
            problems.append(
                "%s: the document says these are ONE file, but they hash to %d:\n%s"
                % (
                    label,
                    len(distinct),
                    "\n".join("      %-24s %s" % (b, hashes[b][:8]) for b in builds),
                )
            )
        elif relation is DIFFERENT and len(distinct) != len(builds):
            problems.append(
                "%s: the document says these DIFFER, but they hash alike:\n%s"
                % (
                    label,
                    "\n".join("      %-24s %s" % (b, hashes[b][:8]) for b in builds),
                )
            )

    if not have:
        print("  SKIP CLAUDE.md's binary-identity claims: no BC artifacts under %s" % root)
        print("       -- nothing to compare against. This is the ordinary state on CI,")
        print("       whose tools-tests job never provisions BC. Not a failure, and not")
        print("       a claim that the document is right.")
        print("       Provision with: al-runner provision --bc-version <ver>")
        return EXIT_OK

    for line in skipped:
        print("  SKIP %s" % line)

    for line in unmeasurable:
        print("UNMEASURABLE: %s" % line)

    for line in problems:
        print("FAIL: %s" % line)

    # A real disagreement outranks a pattern that stopped matching: if one claim
    # is measurably wrong, say so, whatever happened to the other.
    if problems:
        print()
        print("CLAUDE.md's binary-identity example disagrees with the artifacts on this box.")
        print("Fix the prose to match the hashes -- never the reverse (#4221).")
        return EXIT_DRIFTED

    if unmeasurable:
        print()
        print("Could not settle %d of %d claim(s) -- the prose no longer matches this"
              % (len(unmeasurable), len(CLAIMS)))
        print("guard's patterns, so nothing was measured for them. %d checked and consistent."
              % checked)
        return EXIT_CANNOT_MEASURE

    if checked == 0:
        print("  SKIP every binary-identity claim: none of the builds CLAUDE.md names")
        print("       is provisioned here, so there is nothing to compare against.")
        return EXIT_OK

    print("OK: %d binary-identity claim(s) in CLAUDE.md agree with %d provisioned artifact(s)."
          % (checked, len(have)))
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
