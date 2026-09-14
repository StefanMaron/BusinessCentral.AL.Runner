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


def check(name: str, ok: bool, detail: str = "") -> None:
    print(f"  {'ok  ' if ok else 'FAIL'} {name}")
    if not ok:
        if detail:
            print(f"      {detail}")
        failures.append(name)


if not shutil.which("jq"):
    print("  SKIP jq is not installed; this suite needs it to run the recipe's own predicate")
    print("\nall closing-ref recipe checks skipped (jq absent)")
    sys.exit(0)

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
if failures:
    print(f"FAILED: {len(failures)} check(s): {failures}")
    sys.exit(1)
print("all closing-ref recipe checks passed")
