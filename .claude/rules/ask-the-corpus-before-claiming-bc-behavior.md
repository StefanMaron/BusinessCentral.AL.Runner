# Settle a claim about BC by asking the corpus CI, not by reading the corpus

`bc-behavior-tests-go-upstream.md` says where a **test** about BC's behavior must live.
This rule is about a **claim**: before you change runner behavior because you believe BC
does X, find out whether a real service tier has already answered it. It usually has — the
al-language corpus runs on real BC on every push, and its CI log prints a PASS/FAIL line
per test per BC version. That log is a measurement. Reading the AL and deciding what BC
"must" do is not.

## The check

```bash
# newest corpus runs
gh run list --repo StefanMaron/BusinessCentral.AL.Language.Tests --limit 5 \
  --json databaseId,conclusion,createdAt --jq '.[]|"\(.databaseId) \(.conclusion) \(.createdAt)"'

# what real BC did with the tests you care about
gh run view <run-id> --repo StefanMaron/BusinessCentral.AL.Language.Tests --log \
  | grep -E "<TestName1>|<TestName2>"
```

If no corpus test covers the shape, write one and let the corpus CI adjudicate — step 2 of
the workflow in `bc-behavior-tests-go-upstream.md`, and it takes minutes, not a local
container.

## What outranks what

A corpus test green on a real service tier beats, in this order, every one of:

1. reading the AL and reasoning about what the platform must do,
2. a differential measured through a harness against a BC container,
3. Microsoft's documentation,
4. the name of a BC codeunit, or a comment naming one.

**And one thing outranks the corpus CI itself: the Windows nightly.** The eight cloud legs
run on `MsDyn365Bc.On.Linux`, which is one particular patched container rather than Business
Central; the nightly runs an official Microsoft container on Windows. Where the two disagree,
Windows is right by definition and the Linux result is an image bug — see the next section
for the dispatch and the three outcomes.

**One qualifier on that ranking**, and it is not a footnote: the tier is patched. On a
surface an unfaithful patch covers, a corpus result measures the patch, not BC — read
"The tier is patched, so check before quoting it on a UI surface" below before resting a
UI-side claim on a corpus result.

## When the Linux tier is the thing in doubt, ask Windows — do not reason about it

The qualifier above says a corpus result can be measuring the patch rather than BC. It does
not say what to do about it, and the answer is not more reading: **dispatch the Windows
nightly against the branch and let it adjudicate.**

The ordering, which is the repository owner's standing instruction:

> **The corpus pins what the Windows pipeline says. `MsDyn365Bc.On.Linux` and AL Runner
> follow it — never the reverse.**

This is not new policy. `.github/workflows/nightly-windows.yml`'s own header has carried it
since corpus issue #213:

> any test that fails on windows needs to be first fixed on
> https://github.com/StefanMaron/MsDyn365Bc.On.Linux

So: **a Windows failure is a real failure. A Linux-only failure is an image bug.**

```bash
gh workflow run 351779742 --repo StefanMaron/BusinessCentral.AL.Language.Tests \
  --ref <branch> -f bc_version=28.4 -f artifact_type=sandbox -f country=w1
```

Four outcomes, and only the first two need anyone to do anything here:

| Windows | meaning | fix goes |
|---|---|---|
| fails too | the assertion does not match BC | **the corpus test** — change the assertion |
| passes, Linux fails | Linux-only ⇒ image bug | **`MsDyn365Bc.On.Linux`** — the assertion stands, the PR waits |
| passes, Linux passes | settled | merge |
| **errored before running tests** | **no verdict at all** | **nothing here — the tier is broken; file it** |

None of the first three rows is a judgement call. Which is the point: a red corpus leg on a
UI-adjacent surface looks like it needs analysis, and it usually needs a dispatch.

**The fourth row is the one that will bite, and it bit on this rule's first use.** A run that
dies before executing a test still reports `conclusion: failure`. Read that as a verdict and
you land on row 1 — *change the corpus assertion* — which is precisely what the last paragraph
of this section forbids, arrived at by following the table. So the conclusion is not the thing
to read:

```
##[error]parsed 0 tests from the supplied XUnit files. That is not a green run -- it means the
         tests never executed, or the result file never got written. Refusing to report a verdict.
```

That refusal is the signal. **A `failure` with zero tests parsed is not Windows disagreeing
with you; it is Windows not having been asked.** Measured 2026-09-08: two dispatches against
corpus PRs #272 and #273 both died in the nightly's tenant-encryption-key step, before any test
ran (corpus #288). Three runs earlier the same day had succeeded, so this is a thing that
happens to a working workflow, not a permanent state — which is exactly why it has to be
recognised rather than assumed away.

Do not re-dispatch to see whether it clears: two runs twelve minutes apart failing identically
is a deterministic fault, and another attempt spends an hour of the account's shared Actions
queue reproducing it.

**It adjudicates; it does not gate.** The nightly takes 1-2 hours and is deliberately not a
required status context — a nightly that gates merges stalls the repository. The eight
`BC <ver> / test` legs remain the merge gate, so a PR stays blocked on those either way.

**Do not adjust a corpus assertion to match the Linux tier, and never to match the runner.**
The second is the more tempting error, because it turns a red leg green and looks like
progress; it is how the corpus stops being evidence about BC at all.

### The cost of not reaching for this first

Measured 2026-09-08. Two corpus PRs (#272, #273) sat red on their cloud legs. A Linux tier
defect had just been fixed upstream (`2b0d91f8`, forcing `CommunicationBroker.Async = false`
and so disabling BC's own notification coalescing), and the failures matched its shape
closely — one of them read `Expected:<1> Actual:<2>`, a message delivered twice, which is
exactly what disabled coalescing produces.

The inference was written up on both PRs as a hypothesis, with single-leg re-runs attached.
Both re-runs failed **identically** on the fixed tier, and the hypothesis was retracted.

The reasoning was sound and the conclusion was wrong: **a symptom matching a mechanism is not
evidence that mechanism produced it.** What made it recoverable was attaching the check to the
claim rather than publishing a finding. What would have avoided it entirely was dispatching
Windows first — one command, against a documented authority, instead of an argument about
which tier to believe.

## The incidents this rule is made of

**#2144 — the container differential lost, and a self-inflicted failure got classified
instead of reverted.** The differential said `TestIsolation = Codeunit` rolls the database
back per test; Microsoft's documentation said per codeunit; the corpus test agreed with the
documentation, and the container measurement was an artifact of a harness that invoked tests
one at a time and could not tell a platform rollback from a new transaction. The same change
cited a codeunit 130452 "Test Runner - Isol. Test" that does not exist — 130452 is "Test
Runner - Get Methods". A name is not evidence. Its 20 `expect-fail-known-gap` entries existed
only because that same PR had changed the default isolation mode in a way real BC does not;
reverting the change made all 20 pass. **Revert a self-inflicted failure — do not classify it.**

**#2170 — "the corpus contradicts itself" was falsified by the corpus CI.** Three tests
looked identical (uncommitted `Insert`, unrelated `asserterror`, then a read) and were read
as contradictory; all three pass on BC 27.5 and 28.3. If two corpus tests look like they
assert opposite things about the same AL shape and both pass upstream, the shape is not the
same and you have not found the distinction yet — a fact about your reading, not about the
corpus. **An entry whose `Note` asserts something about BC that no service tier has confirmed
is a guess wearing a schema.**

So: **never propose inverting an upstream assertion that is green on a service tier** — a PR
into the corpus that flips a passing test is asking a service tier to disagree with itself.
Name the mechanism you found, not the symptom you could not explain.

## What an `expect-fail-known-gap` entry may rest on

Every test in the corpus passes on real BC by construction, so *every* known-gap entry is for
a test green upstream. The mode means exactly: the surface is in scope, real BC does it, the
runner does not do it yet, and `Issue` tracks the work. (An earlier version of this rule said
never to declare a known gap for a test green upstream; that was wrong and contradicted
`docs/expectations.md`.) An entry is honest when it says that. It is dishonest when it
converts a live question, or a self-inflicted regression, into settled classification.

## The tier is patched, so check before quoting it on a UI surface

The corpus CI boots a Linux BC image that installs ~30 numbered patches into BC's own
assemblies at startup. Most are faithful. One that is not turns a corpus result on that
surface into a measurement of the patch, and the green direction is the one nobody notices —
a test asserting "nothing happens" records the patch as BC behaviour. That is not
hypothetical: Patch #21 no-opped `NavOpenTaskPageAction.ShowForm` and blinded every route
that opens a page through an action (#2986). It has since been fixed and the surface
re-measured open on all eight legs, so it is a verdict again.

Before resting a UI-side claim on a corpus result, read `src/StartupHook/StartupHook.cs` in
`StefanMaron/MsDyn365Bc.On.Linux` for the surface you are asking about. The same applies
off the UI: Windows identity (`ALDatabase.ALSid`, `WindowsPrincipal`), report rendering
(`CustomReportingServiceClient`), encryption key resolution, Azure AD and service topology
are all patched on that tier, and a green there measures the patch. The list, with patch
numbers, is in `docs/upstream-corpus-workflow.md` § "How to find out whether a surface you
care about is patched" — and the same list is why "the bc-linux container passes it" is not
by itself evidence of a runner gap when triaging Microsoft's test buckets (#2314).
`docs/upstream-corpus-workflow.md` § "What the corpus tier can and cannot adjudicate" has the
worked case, what remains out of reach, and the `SingleInstance`-probe technique for an
observable a rollback would otherwise destroy.

## When no verdict is available

This covers the fourth row of the table above — a reference-tier run that errored before
executing anything is *no verdict*, not a negative one, and the rules here apply unchanged.

Say so plainly, name what would settle it, and land the runner change with whatever coverage
is legitimately available — the escape hatch in `bc-behavior-tests-go-upstream.md` applies
here too. What is not acceptable is substituting confident reasoning for the measurement and
writing it into a comment, a doc table, or an issue as though it were established.

## Sister rules

- `bc-behavior-tests-go-upstream.md` — where a BC-behavior test must live, and how to
  get a verdict out of the corpus CI
- `verify-execution-not-the-tick.md` — a green corpus test is evidence only if it *ran*;
  the check for that has produced a false zero five ways
- `no-assumption-fixes.md` — understand the AL pattern before patching
- `al-language-submodule.md` — the corpus is read-only here; how to bump the pin
- `file-issues-for-gaps.md` — gaps get tracked, never silently worked around
