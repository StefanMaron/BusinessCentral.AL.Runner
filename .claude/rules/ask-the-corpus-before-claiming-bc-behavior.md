# Settle a claim about BC by asking the corpus CI, not by reading the corpus

`bc-behavior-tests-go-upstream.md` says where a **test** about BC's behavior must
live. This rule is about a **claim**: before you change runner behavior because you
believe BC does X, find out whether a real service tier has already answered it.

It usually has. The al-language corpus runs on real BC on every push, and its CI log
prints a PASS/FAIL line per test per BC version. That log is a measurement. Reading
the AL and deciding what BC "must" do is not.

## The check

```bash
# newest corpus runs
gh run list --repo StefanMaron/BusinessCentral.AL.Language.Tests --limit 5 \
  --json databaseId,conclusion,createdAt --jq '.[]|"\(.databaseId) \(.conclusion) \(.createdAt)"'

# what real BC did with the tests you care about
gh run view <run-id> --repo StefanMaron/BusinessCentral.AL.Language.Tests --log \
  | grep -E "<TestName1>|<TestName2>"
```

If no corpus test covers the shape, write one and let the corpus CI adjudicate — that
is step 2 of the workflow in `bc-behavior-tests-go-upstream.md`, and it takes minutes,
not a local container.

## What outranks what

A corpus test green on a real service tier beats, in this order, every one of:

- reading the AL and reasoning about what the platform must do,
- a differential measured through a harness against a BC container,
- Microsoft's documentation,
- the name of a BC codeunit, or a comment naming one.

That order is not theoretical. In #2144 a container differential said
`TestIsolation = Codeunit` rolls the database back per test; Microsoft's
documentation said per codeunit; the corpus test agreed with the documentation and
the container measurement was an artifact of a harness that invoked tests one at a
time and could not tell a platform rollback from a new transaction. The same change
cited a codeunit 130452 "Test Runner - Isol. Test" that does not exist — 130452 is
"Test Runner - Get Methods". A name is not evidence.

## Two green corpus tests never contradict each other

If two corpus tests look like they assert opposite things about the same AL shape and
both pass upstream, the shape is not the same and you have not found the distinction
yet. That is a fact about your reading, not about the corpus.

In #2170 three tests looked identical — uncommitted `Insert`, unrelated `asserterror`,
then a read — and were read as contradictory. All three pass on BC 27.5 and 28.3.

So:

- **Never propose inverting an upstream assertion that is green on a service tier.**
  A PR into the corpus that flips a passing test is asking a service tier to disagree
  with itself.
- Name the mechanism you found, not the symptom you could not explain.

## What an `expect-fail-known-gap` entry may and may not rest on

An earlier version of this rule said never to declare a known gap for a test that is
green upstream. That was wrong, and it contradicted `docs/expectations.md`: every test
in the corpus passes on real BC by construction, so *every* known-gap entry is for a
test green upstream. The mode means exactly "the surface is in scope, real BC does it,
the runner does not do it yet, and `Issue` tracks the work."

What the two incidents actually got wrong was the **justification**, not the mode:

- **Do not declare a known gap for a failure your own change just introduced.** In
  #2144 the 20 entries existed only because the same PR had changed the default
  isolation mode in a way real BC does not; reverting the change made all 20 pass. The
  fix for a self-inflicted failure is to revert it, not to classify it.
- **Do not declare a known gap on the strength of an unmeasured claim about BC.** In
  #2170 the entries were justified by "the corpus contradicts itself," which the corpus
  CI falsified. An entry whose `Note` asserts something about BC that no service tier
  has confirmed is a guess wearing a schema.

A known-gap entry is honest when it says: real BC passes this, the runner does not yet,
here is the issue. It is dishonest when it converts a live question, or a
self-inflicted regression, into settled classification.

## When no verdict is available

Say so plainly, name what would settle it, and land the runner change with whatever
coverage is legitimately available — the escape hatch in
`bc-behavior-tests-go-upstream.md` applies here too. What is not acceptable is
substituting confident reasoning for the measurement and writing it into a comment,
a doc table, or an issue as though it were established.

## Sister rules

- `bc-behavior-tests-go-upstream.md` — where a BC-behavior test must live, and how to
  get a verdict out of the corpus CI
- `no-assumption-fixes.md` — understand the AL pattern before patching
- `al-language-submodule.md` — the corpus is read-only here; how to bump the pin
- `file-issues-for-gaps.md` — gaps get tracked, never silently worked around
