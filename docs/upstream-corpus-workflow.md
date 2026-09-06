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
Linux (via `StefanMaron/MsDyn365Bc.On.Linux`) and runs the suite on **eight BC
versions — 27.0, 27.3, 27.5, 28.0, 28.1, 28.2, 28.3 and 28.4**,
`fail-fast: false`. Not every minor in that span: 27.1, 27.2 and 27.4 are not
run. Sixteen legs, because the cloud app and the OnPrem app are built and run
separately on each version; the **eight cloud legs are the required status
contexts** on the corpus's `master`, and the eight OnPrem legs run alongside
them without gating. So a green PR check upstream *is* the service-tier
adjudication this rule demands. If you have no local container, opening the PR
and letting CI run is a legitimate way to perform step 2 — not a way to skip
it. Having no
container is the normal case for agents in web/remote sessions and is fully
handled by this workflow.

## Step 3 in full — why the orchestrator merges, not the authoring agent

An impl agent opens the corpus PR and stops there. The orchestrator reviews
it and merges once the corpus's eight required BC legs are green. This split is deliberate — an
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
run `34039189247` (push, `master`, success, 2026-09-06 14:26 UTC), all eight
cloud legs, all thirteen arms across codeunits 60455 (`TPARO Tests`, 7) and
60285 (`TPARONH Tests`, 6) PASS on every leg.

One arm carries the whole claim, because it cannot pass unless the form really
is shown and a handler really is looked up:
`RunObjectActionToAStandardDialogTargetIsAnsweredByTheModalHandler`. Its
before and after, on the same tier, both parsed per leg out of the container
log the workflow dumps on a failing run:

| | run | image | that arm, 8 cloud legs | `[StartupHook] Patch #21` line |
|---|---|---|---|---|
| before | `33994178908` (2026-09-05 21:50 UTC) | pre-fix | **FAIL** on all 8 | `ShowForm skipped (no headless UI on Linux)` on all 8 |
| after | `34013909696` (2026-09-06 05:24 UTC) | rebuilt | **PASS** on all 8 | `hooked (faithful headless show)` on all 8, zero `skipped` |

Read that as a before/after on **the arm**, not on the whole codeunit: the two
runs are the same corpus branch but not the same tree — other arms were added
in between — so it is not a controlled A/B over everything that ran. The arm's
own body is byte-identical in both trees (`22ae36f` and `e78e982`, same
17-line hash), and the second run's head commit is titled "re-run CI against
the rebuilt Linux BC image (no content change)".

So a claim about a page opened through an action no longer needs a stock
Windows tier, and a corpus result on that surface is a verdict again.

### What is still not adjudicable there

1. **Anything whose only observable is client-side UI with no AL-visible
   consequence.** Microsoft's own `catch (NavBaseException)` inside `ShowForm`
   shows the error and force-closes the form rather than rethrowing, so AL is
   never told. That mechanism is read from BC's shipped IL, not measured on a
   tier — necessarily, since being invisible to AL is the very thing that makes
   it unadjudicable. A corpus test can measure the *side effects* of that — whether
   the target's `OnOpenPage` ran, whether a row was written — but never
   "a dialog was displayed". Codeunit 60285 is the worked example: it answers
   "did the page open" by making the page record its own opening, because the
   opening itself is invisible to AL.
2. **Not yet adjudicated is not the same as not adjudicable.** Most of the
   `ActionBuilder` family still has no corpus test. That is missing coverage,
   which anyone can go and write, and it should not be quoted as a limit of
   the tier.

Point 1 is a property of Business Central rather than of the harness, so it
holds on a stock Windows tier too: the swallowing is Microsoft's own code, not
the Linux image's. Point 2 is not a limit at all — it is a coverage backlog,
listed here only because it keeps getting quoted as one.

### Measurable, but invisible on a green run

This is a **visibility** limit rather than an adjudication limit, and it is
separated from the two above on purpose — it is fixable, by surfacing the
container log on green runs or by a corpus test that asserts against it.

Patch #21 still deviates from Microsoft's body twice. A `NullReferenceException`
escaping the show call is logged with its full stack and reported as "not
shown" instead of propagating, and a null `childForm` / `uiSession` /
`formState` is reported as "not shown" instead of dereferenced. If either
fired, a test asserting "nothing opened" would go green for the wrong reason,
and nothing in the run would say so — the patch prints that stack to the
container log, and the workflow surfaces the container log **only when a leg
fails**.

Neither has been observed firing. That claim needs a *failing* run to rest on,
since a green one carries no `[StartupHook]` lines at all: run `34013909696`,
post-fix, all eight cloud legs, prints `hooked (faithful headless show)` and
**zero** occurrences of `reporting 'not shown'` or of the
`NullReferenceException in the headless UI layer` message.

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
