# Settle a claim about BC by asking the corpus CI, not by reading the corpus

`bc-behavior-tests-go-upstream.md` says where a **test** about BC's behavior must live. This
rule is about a **claim**: before you change runner behavior because you believe BC does X,
find out whether a real service tier has already answered it. It usually has — the al-language
corpus runs on real BC on every push and prints a PASS/FAIL line per test per BC version. That
log is a measurement; reading the AL and deciding what BC "must" do is not.

## The check

```bash
# newest corpus runs
gh run list --repo StefanMaron/BusinessCentral.AL.Language.Tests --limit 5 \
  --json databaseId,conclusion,createdAt --jq '.[]|"\(.databaseId) \(.conclusion) \(.createdAt)"'

# what real BC did with the tests you care about
gh run view <run-id> --repo StefanMaron/BusinessCentral.AL.Language.Tests --log \
  | grep -E "<TestName1>|<TestName2>"
```

If no corpus test covers the shape, write one and let the corpus CI adjudicate — step 2 of the
workflow in `bc-behavior-tests-go-upstream.md`; it takes minutes, not a local container.

## What outranks what

A corpus test green on a real service tier beats, in this order, every one of:

1. reading the AL and reasoning about what the platform must do,
2. a differential measured through a harness against a BC container,
3. Microsoft's documentation,
4. the name of a BC codeunit, or a comment naming one.

**And one thing outranks the corpus CI itself: the Windows nightly.** The eight cloud legs run
on `MsDyn365Bc.On.Linux`, one particular patched container rather than Business Central; the
nightly runs an official Microsoft container on Windows. Where the two disagree, Windows is
right by definition and the Linux result is an image bug.

**One qualifier: the tier is patched.** On a surface an unfaithful patch covers, a corpus
result measures the patch — read "The tier is patched" below before resting a UI-side claim on
one.

## When the Linux tier is the thing in doubt, ask Windows — do not reason about it

**Dispatch the Windows nightly against the branch and let it adjudicate.** The ordering is the
repository owner's standing instruction, and `.github/workflows/nightly-windows.yml`'s header
has carried it since corpus issue #213:

> **The corpus pins what the Windows pipeline says. `MsDyn365Bc.On.Linux` and AL Runner
> follow it — never the reverse.**

So: **a Windows failure is a real failure. A Linux-only failure is an image bug.**

**On a corpus PR, label it:**

```bash
gh pr edit <N> --repo StefanMaron/BusinessCentral.AL.Language.Tests \
  --add-label run-nightly-windows
```

A `pull_request` event reads the workflow **file** from the base branch and runs it against
**your PR's merge commit**, so the label adjudicates your tests using `master`'s CI.
`workflow_dispatch` takes both from the ref, so on a branch predating a fix to the nightly it
re-runs the broken version and the failure looks like a tier fault. Dispatch only for a ref
with no pull request:

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

A red corpus leg on a UI-adjacent surface looks like it needs analysis and usually needs a
dispatch.

**The fourth row is the one that bites** (corpus #288). A run that dies before executing a test
still reports `conclusion: failure`, so read the log rather than the conclusion:

```
##[error]parsed 0 tests from the supplied XUnit files. That is not a green run -- it means the
         tests never executed, or the result file never got written. Refusing to report a verdict.
```

**A `failure` with zero tests parsed is not Windows disagreeing with you; it is Windows not
having been asked.** Do not re-dispatch to see whether it clears: two identical failures
minutes apart are a deterministic fault, and another attempt spends an hour of the account's
shared Actions queue reproducing it.

**It adjudicates; it does not gate.** The nightly takes 1-2 hours and is deliberately not a
required status context, so the corpus's own required legs remain the merge gate either way
(`verify-execution-not-the-tick.md` § "Which legs were ever going to run it").

**Do not adjust a corpus assertion to match the Linux tier, and never to match the runner.**
The second is the more tempting error, because it turns a red leg green and looks like
progress; it is how the corpus stops being evidence about BC at all.

**A symptom matching a mechanism is not evidence that mechanism produced it** (corpus PRs #272,
#273): attach the check to the claim instead of publishing the inference, and dispatch Windows
first rather than arguing about which tier to believe.

## What a claim may and may not rest on

- **Revert a self-inflicted failure — do not classify it.** An `expect-fail-known-gap` block
  that exists because your own change moved runner behavior disappears when the change is
  reverted (#2144).
- **A codeunit's name is not evidence** — #2144 cited a codeunit 130452 "Test Runner - Isol.
  Test" that does not exist.
- **Never propose inverting an upstream assertion that is green on a service tier** — a corpus
  PR that flips a passing test asks a service tier to disagree with itself. Two corpus tests
  that look like they assert opposite things about one AL shape, both green, mean you have not
  found the distinction yet (#2170). Name the mechanism you found, not the symptom you could
  not explain.
- **Support every BC claim in an expectation entry's `Note` with a service-tier result** — the
  run or the corpus test that measured it — or say plainly that no verdict exists (#2170).

## What an `expect-fail-known-gap` entry may rest on

Every corpus test passes on real BC by construction, so *every* known-gap entry is for a test
green upstream. The mode means: the surface is in scope, real BC does it, the runner does not
do it yet, and `Issue` tracks the work (`docs/expectations.md`). An entry is dishonest when it
converts a live question, or a self-inflicted regression, into settled classification.

## The tier is patched, so check before quoting it on a UI surface

The corpus CI boots a Linux BC image that installs ~30 numbered patches into BC's own
assemblies at startup. Most are faithful; an unfaithful one turns a corpus result on that
surface into a measurement of the patch, and the green direction is the one nobody notices — a
test asserting "nothing happens" records the patch as BC behaviour (#2986).

Before resting a UI-side claim on a corpus result, read `src/StartupHook/StartupHook.cs` in
`StefanMaron/MsDyn365Bc.On.Linux` for the surface you are asking about. The same applies off
the UI: Windows identity (`ALDatabase.ALSid`, `WindowsPrincipal`), report rendering
(`CustomReportingServiceClient`), encryption key resolution, Azure AD and service topology are
all patched there. The list, with patch numbers, is in `docs/upstream-corpus-workflow.md` §
"How to find out whether a surface you care about is patched" — and it is why "the bc-linux
container passes it" is not by itself evidence of a runner gap when triaging Microsoft's test
buckets (#2314). `docs/upstream-corpus-workflow.md` § "What the corpus tier can and cannot
adjudicate" has the worked case, what remains out of reach, and the `SingleInstance`-probe
technique for an observable a rollback would otherwise destroy.

## When no verdict is available

**A reference-tier run that errored before executing anything is *no verdict*, not a negative
one** — the fourth row of the table above. What to do then is
`bc-behavior-tests-go-upstream.md`'s "No verdict available at all", and it applies unchanged to
a claim: what is never acceptable is substituting confident reasoning for the measurement and
writing it into a comment, a doc table, or an issue as though it were established.

## Sister rules

- `bc-behavior-tests-go-upstream.md` — where a BC-behavior test must live, and how to
  get a verdict out of the corpus CI
- `verify-execution-not-the-tick.md` — a green corpus test is evidence only if it *ran*;
  the check for that has produced a false zero five ways
- `no-assumption-fixes.md` — understand the AL pattern before patching
- `al-language-submodule.md` — the corpus is read-only here, and resolved rather than pinned
- `file-issues-for-gaps.md` — gaps get tracked, never silently worked around

History: docs/incidents/ask-the-corpus-before-claiming-bc-behavior.md
