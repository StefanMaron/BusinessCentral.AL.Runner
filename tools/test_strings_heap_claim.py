#!/usr/bin/env python3
"""Pin the `strings -el` false-negative claim in .claude/skills/inspecting-bc-binaries/SKILL.md (moved from CLAUDE.md, #4542) against a real assembly.

The document states that a .NET assembly keeps member names in the UTF-8
`#Strings` heap and user string literals in the UTF-16 `#US` heap, so `strings -el`
-- the flag an agent is told to reach for -- returns ZERO for a member name that
is genuinely referenced. It backs that with a measurement:

    28.1.49838.53910/Ncl.dll, metadata name RunRequestPageAsync:
        strings -a -el -> 0        strings -a -> 4

That is a countable claim about a file, so it drifts like any other (#4229, and
the class in .claude/rules/verify-execution-not-the-tick.md).

States (.claude/rules/guards-need-a-third-state.md):

  0  the claim holds -- OR the artifact / `strings` is not here, a loud SKIP
  1  the claim is refuted: -el found the name, or -a did not
  3  COULD NOT MEASURE -- the prose moved, so the claim matched nothing

Absent-vs-unmeasurable, as in tools/test_bc_binary_identity_claims.py: a box
without that BC build (the ordinary case on CI, whose tools-tests job uses a bare
checkout) is a legitimate SKIP, while a reworded sentence is a broken measurement
and must refuse. The remedies differ, so the verdicts must.

Deliberately asserts the DIRECTION and the zero, not the exact count of 4: the
number of `#Strings` hits is a property of that build, while "el finds none, plain
finds some" is the property the document actually teaches.
"""

import os
import re
import shutil
import subprocess
import sys

EXIT_OK, EXIT_REFUTED, EXIT_CANNOT_MEASURE = 0, 1, 3

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOC = os.path.join(REPO, ".claude", "skills", "inspecting-bc-binaries", "SKILL.md")
NCL = "Microsoft.Dynamics.Nav.Ncl.dll"

# Captures the build, the member name, and both counts the document states.
CLAIM = re.compile(
    r"Measured on `(?P<build>[\d.]+)/" + re.escape(NCL) + r"`, for the metadata name\s*\n?\s*"
    r"`(?P<member>\w+)`: `strings -a -el` finds \*\*(?P<el>\d+)\*\*, "
    r"`strings -a` finds \*\*(?P<plain>\d+)\*\*"
)


def artifacts_root():
    return os.environ.get("AL_RUNNER_ARTIFACTS_ROOT") or os.path.join(
        os.path.expanduser("~"), ".local", "share", "al-runner", "artifacts"
    )


def count(args, path, needle):
    out = subprocess.run(["strings"] + args + [path], capture_output=True, text=True)
    if out.returncode != 0:
        return None
    return sum(line.count(needle) for line in out.stdout.splitlines())


def main():
    with open(DOC, encoding="utf-8") as fh:
        doc = fh.read()

    found = CLAIM.findall(doc)
    # findall, not search: a second copy of the sentence would let a drifted one
    # hide behind a correct one (#4241).
    if not found:
        print("UNMEASURABLE: the skill's `strings -el` measurement sentence no longer matches")
        print("  this guard's pattern -- the prose was reworded, so nothing was measured.")
        print("  Re-read the paragraph and update the pattern.")
        return EXIT_CANNOT_MEASURE
    if len(found) > 1:
        print("UNMEASURABLE: the pattern matches %d places in the skill, so this guard"
              % len(found))
        print("  cannot tell which sentence it is pinning. Make the claim unique.")
        return EXIT_CANNOT_MEASURE

    m = CLAIM.search(doc)
    build, member = m.group("build"), m.group("member")
    said_el, said_plain = int(m.group("el")), int(m.group("plain"))

    if shutil.which("strings") is None:
        print("  SKIP the skill's `strings -el` claim: `strings` is not installed here")
        print("       (binutils). Nothing to measure with; not a failure.")
        return EXIT_OK

    path = os.path.join(artifacts_root(), build, NCL)
    if not os.path.isfile(path):
        print("  SKIP the skill's `strings -el` claim: %s is not provisioned here." % build)
        print("       This is the ordinary state on CI, which never provisions BC for this job.")
        print("       Provision with: al-runner provision --bc-version %s" % build)
        return EXIT_OK

    el = count(["-a", "-el"], path, member)
    plain = count(["-a"], path, member)
    if el is None or plain is None:
        print("UNMEASURABLE: `strings` failed on %s" % path)
        return EXIT_CANNOT_MEASURE

    problems = []
    # The direction is the claim. A UTF-16 scan must not find a #Strings name...
    if el != 0:
        problems.append(
            "`strings -a -el` found %d occurrence(s) of `%s`, but the skill's whole point is "
            "that a UTF-16 scan finds NONE of a metadata name." % (el, member)
        )
    # ...and the plain scan must, or the example proves nothing.
    if plain <= 0:
        problems.append(
            "`strings -a` found %d occurrence(s) of `%s`; the example needs a member the UTF-8 "
            "scan actually finds, or it demonstrates nothing." % (plain, member)
        )

    if problems:
        for p in problems:
            print("FAIL: %s" % p)
        print()
        print("Measured on %s:" % build)
        print("  strings -a -el -> %d   (the skill says %d)" % (el, said_el))
        print("  strings -a     -> %d   (the skill says %d)" % (plain, said_plain))
        print("Fix the prose to match the assembly -- never the reverse (#4229).")
        return EXIT_REFUTED

    note = ""
    if (el, plain) != (said_el, said_plain):
        # Not a failure: the exact count is a property of the build, and the
        # document's claim is the direction. Say so rather than silently passing.
        note = "  (document states %d/%d; the direction is what is pinned)" % (said_el, said_plain)

    print("OK: on %s, `strings -a -el` finds %d and `strings -a` finds %d for `%s`.%s"
          % (build, el, plain, member, note))
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
