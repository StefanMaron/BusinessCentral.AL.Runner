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
Linux (via `StefanMaron/MsDyn365Bc.On.Linux`) and runs the suite against **BC
27.5 and 28.3**, `fail-fast: false`. So a green PR check upstream *is* the
service-tier adjudication this rule demands, on two BC versions. If you have
no local container, opening the PR and letting CI run is a legitimate way to
perform step 2 — not a way to skip it. Having no container is the normal case
for agents in web/remote sessions and is fully handled by this workflow.

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
