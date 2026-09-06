# Why BC-behaviour tests go upstream, and the full workflow rationale

This is the supporting argument and detail for
`.claude/rules/bc-behavior-tests-go-upstream.md`. The rule states the
requirement; this doc carries the justification so the rule stays short
enough to load into every session.

## Why — a runner-local BC test proves nothing

The corpus is the spec *because* every test in it has been run against real
BC. A BC-behaviour test that has only ever run against AL Runner is not
evidence about BC; it is a transcript of **our belief** about BC, written by
the same reasoning that wrote the runtime.

So when the runner is wrong, such a test does not fail — it was authored to
match what the runner did. It goes green, the bug is now pinned as intended
behaviour, and every future change is measured against the wrong baseline.
The suite gets louder and less trustworthy at the same time.

That is the whole argument in one line: an unvalidated BC test cannot prove
the runner correct, because it inherits the runner's errors as its
expectations. Green means "the runner agrees with itself".

## Step 2 in full — verifying against real BC

A local BC container with the BC repository is a perfectly good way to check
a new upstream test: publish the app, run the test, confirm it passes for the
reason you think it does. This step exists to stop you sending a broken or
wrongly-asserted test upstream — it does not by itself put the test in the
corpus.

The corpus repo's own CI is also a real service tier, and is the stronger
check of the two. `.github/workflows/ci.yml` there boots a real BC sandbox on
Linux (via `StefanMaron/MsDyn365Bc.On.Linux`) and runs the suite on **every BC
minor from 27.0 to 28.4**, `fail-fast: false` — sixteen legs, because the
cloud app and the OnPrem app are built and run separately on each of the eight
versions. So a green PR check upstream *is* the service-tier adjudication this
rule demands. If you have no local container, opening the PR and letting CI
run is a legitimate way to perform step 2 — not a way to skip it. Having no
container is the normal case for agents in web/remote sessions and is fully
handled by this workflow.

## Step 3 in full — why the orchestrator merges, not the authoring agent

An impl agent opens the corpus PR and stops there. The orchestrator reviews
it and merges once both BC legs are green. This split is deliberate — an
agent merging its own test means the same reasoning that wrote the test also
clears it, which is this rule's original failure mode relocated from
"unvalidated" to "unreviewed". Green CI proves the test *runs and passes
against real BC*; an independent read is what proves it *asserts something*.

Green CI is necessary, not sufficient. A test that asserts a default value,
or that would pass against a stub returning `0` / `''` / `false`, goes green
just as reliably as a good one. Before merging, apply `tdd.md`'s test: would
this still pass if the implementation were gutted? If yes it is noise — send
it back rather than merge it. Both directions (positive + `asserterror` with
a specific expected message) still apply upstream.

## Why two PRs in that order, never one

A runner fix for a BC-behaviour gap is normally two PRs in two repos, corpus
first, runner second. Do not merge the runner change and leave the upstream
test as a promise — once the fix is in, nothing forces the test to follow,
and the gap quietly becomes untested behaviour.

## What the corpus tier can and cannot adjudicate

The corpus CI does not boot stock Windows BC. It boots
`StefanMaron/MsDyn365Bc.On.Linux`, whose `StartupHook` installs about thirty
numbered JMP patches into BC's own assemblies at startup so the server runs
headless on Linux. Most are faithful to the method they replace. When one is
not, a corpus result on the surface that patch covers measures **the patch**,
not BC — in both directions, and the dangerous direction is the green one. A
red leg is at least visible; a test asserting "nothing happens" passes,
records the patch as Business Central behaviour, and nothing in the run says
otherwise.

### The case this section is made of

Patch #21 replaced `NavOpenTaskPageAction.ShowForm` with a no-op, because the
real method raised a `NullReferenceException` on the headless client and
killed the session. `ShowForm` is the single point where a task-page open
becomes a displayed form, and the test-handler lookup hangs off it. Skipped,
the target page was built and never shown: nothing opened, no `[PageHandler]`
or `[ModalPageHandler]` was looked up, no error was raised, and the invoke
returned normally. That blinded everything `ActionBuilder` routes into
`NavOpenTaskPageAction` — an action's `RunObject`, the Edit / View /
OpenInNewWindow menu actions, the list View and Edit system actions, the
built-in New action, page views. Recorded as #2986.

It is fixed. `MsDyn365Bc.On.Linux` PR #63 re-implemented the real body,
including Microsoft's own catch order, and merged on 2026-09-06.

**Measured re-opened**, so this is not taken on the fix's word: corpus master
run `34039189247` (push, `master`, success), all eight cloud legs, six arms
across codeunits 60455 (`TPARO Tests`) and 60285 (`TPARONH Tests`) PASS on
every leg. One of them,
`RunObjectActionToAStandardDialogTargetIsAnsweredByTheModalHandler`, cannot
pass unless the form really is shown and a handler really is looked up — it
was failing while the no-op was installed.

So a claim about a page opened through an action no longer needs a stock
Windows tier, and a corpus result on that surface is a verdict again.

### What is still not adjudicable there

1. **Anything whose only observable is client-side UI with no AL-visible
   consequence.** Microsoft's own `catch (NavBaseException)` inside `ShowForm`
   shows the error and force-closes the form rather than rethrowing, so AL is
   never told. A corpus test can measure the *side effects* of that — whether
   the target's `OnOpenPage` ran, whether a row was written — but never
   "a dialog was displayed". Codeunit 60285 is the worked example: it answers
   "did the page open" by making the page record its own opening, because the
   opening itself is invisible to AL.
2. **Patch #21's two remaining deviations.** A `NullReferenceException`
   escaping the show call is logged with its full stack and reported as "not
   shown" instead of propagating, and a null `childForm` / `uiSession` /
   `formState` is reported as "not shown" instead of dereferenced. Neither has
   been observed firing — zero occurrences across a full corpus run — but if
   one did, a test asserting "nothing opened" would go green for the wrong
   reason. The patch prints that stack to the container log, and the workflow
   only surfaces the container log when a leg **fails**, so a green run cannot
   show it to you.
3. **Not yet adjudicated is not the same as not adjudicable.** Most of the
   `ActionBuilder` family still has no corpus test. That is missing coverage,
   which anyone can go and write, and it should not be quoted as a limit of
   the tier.

### How to find out whether a surface you care about is patched

Read `src/StartupHook/StartupHook.cs` in `StefanMaron/MsDyn365Bc.On.Linux`.
Every patch is numbered and carries a header saying what it replaces and why.
On a failing leg the workflow dumps the container log, where `[StartupHook]
Patch #` lines name every patch installed and every time one of them fired.

### When a rollback would destroy the observable

An error unwinding a transaction discards its uncommitted rows, so a log table
written by the page under test is missing whether the page opened or not, and
a negative assertion over it proves nothing. Put the observable somewhere a
rollback cannot reach — a `SingleInstance` codeunit's own state — and add a
control arm proving that probe is really set when the thing does happen.
Corpus PRs #185 and #194 are the worked pattern; the second was caught by its
own guard, one revision before it would have shipped an unfalsifiable
assertion.
