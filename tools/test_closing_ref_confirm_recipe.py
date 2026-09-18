#!/usr/bin/env python3
"""Execute `check-open-prs-before-claiming.md`'s zero-confirmation recipe (#4111).

The rule's claim: `closingIssuesReferences` lags PR creation, so an empty read
is "not parsed yet", not "free". The confirmation reads the BODY instead:

    jq '.[] | select(.body | test("(?i)closes +#<N>\\b")) | .number'

and the rule warns it OVER-reports, because a PR quoting the rule carries the
keyword too. That warning is the pinnable part: no network is needed to show
the pattern matching a body that merely documents a closing reference.

The `gh` call itself is not reproduced here -- it needs the live API. What is
executed is the jq-side predicate the recipe turns on, against bodies that
reproduce both the real declaration and the documented-but-not-declaring case.
"""
import json
import shutil
import subprocess
import sys

failures: list[str] = []
ran: list[str] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    ran.append(name)
    print(f"  {'ok  ' if ok else 'FAIL'} {name}")
    if not ok:
        if detail:
            print(f"      {detail}")
        failures.append(name)


unmeasured: list[str] = []

# jq gates the PREDICATE checks and nothing else. The rule-text checks at the bottom read a
# markdown file and need no jq, and an early exit skipped them too -- so a reworded rule went
# undetected: exit 1 with jq, exit 0 without, on the same tree (#4355).
#
# And the exit code was 0, the SUCCESS state, for a run that measured nothing. That is worse than
# the exit-1 conflation #4346 fixed in a sibling guard: 1 at least makes a sweep stop and look,
# while 0 is what a passing run returns, so nothing ever looks
# (guards-need-a-third-state.md -- the third state must differ from BOTH measured answers).
HAVE_JQ = shutil.which("jq") is not None
if not HAVE_JQ:
    unmeasured.append(
        "jq is not installed, so the recipe's own predicate could not be run. The rule-text "
        "checks below still ran and are reported; the predicate checks asserted nothing.")
    print("  UNMEASURED jq is absent — the predicate checks below cannot run")

# Three bodies: a real declaration, a body that only DOCUMENTS the form, and a
# body declaring a different issue.
PRS = [
    {"number": 1, "body": "Closes #4111\n\nthe real declaration"},
    {"number": 2, "body": "Part of #4059\n\nquoting the recipe: select(.body | test(\"(?i)closes +#<N>\"))\n"
                          "and an example of the shape, `Closes #4111`, as documentation"},
    {"number": 3, "body": "Closes #9999"},
]


def run_recipe(n: str) -> list[int]:
    """The recipe's jq predicate, substituted, run through real jq."""
    filt = f'[.[] | select(.body | test("(?i)closes +#{n}\\\\b")) | .number]'
    out = subprocess.run(["jq", "-c", filt], input=json.dumps(PRS),
                         capture_output=True, text=True)
    if out.returncode != 0:
        raise AssertionError(f"jq failed: {out.stderr.strip()}")
    return json.loads(out.stdout)


if HAVE_JQ:
    hits = run_recipe("4111")

    check("the recipe finds the PR that really declares the issue",
          1 in hits, f"got {hits}")

    # The documented trap, and the reason the rule calls this a confirmation only.
    check("...and ALSO matches a PR that merely documents the form (the over-report)",
          2 in hits, f"got {hits}")

    check("a PR declaring a different issue is not matched",
          3 not in hits, f"got {hits}")

    # The discriminating half: substituting a DIFFERENT number must move the answer,
    # or the pattern would not be keyed on the issue at all.
    other = run_recipe("9999")
    check("substituting a different issue number selects a different PR",
          other == [3], f"got {other}")

    # And the generic form the rule warns against: left unsubstituted it matches
    # anything, which is the failure the rule says to avoid.
    generic = subprocess.run(
        ["jq", "-c", '[.[] | select(.body | test("(?i)closes +#")) | .number]'],
        input=json.dumps(PRS), capture_output=True, text=True).stdout
    check("the UNsubstituted pattern over-matches, as the rule warns",
          json.loads(generic) == [1, 2, 3], generic.strip())

rule = open(".claude/rules/check-open-prs-before-claiming.md", encoding="utf-8").read()
check("the rule still tells the reader to substitute the real issue number",
      "Substitute the real issue number" in rule)
check("...and still frames this as a confirmation of a zero, never a claim",
      "confirmation of a zero" in rule)

print()
# A real failure outranks an unmeasured half: both can happen at once -- the rule-text checks run
# with or without jq and can catch a reworded rule on a box that has none -- and reporting 3 there
# would hide a measured negative behind "could not measure".
if failures:
    print(f"FAILED: {len(failures)} check(s): {failures}")
    sys.exit(1)
if unmeasured:
    # The UNMEASURED note claims the rule-text checks still ran. Assert that rather than saying
    # it: re-widening the gate to cover them again makes the claim false, and the only thing that
    # changes is an exit code nobody would look twice at (3 either way). Measured in review of
    # #4355 — the message became a lie with nothing catching it.
    #
    # Counting checks that RAN, not the gate's own flag: a flag would restate the branch it is
    # about, while the count is what the sentence actually promises.
    if not ran:
        print("  FAIL the UNMEASURED note says other checks still ran, and NONE did — the gate "
              "is wider than its stated reason, which is the defect #4355 fixed")
        sys.exit(1)
    for note in unmeasured:
        print(f"  UNMEASURED {note}")
    print(f"{len(unmeasured)} check group(s) could not be measured; nothing here is a pass")
    sys.exit(3)
print("all closing-ref recipe checks passed")
