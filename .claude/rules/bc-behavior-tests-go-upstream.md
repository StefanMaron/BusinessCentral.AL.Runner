# Tests of plain BC behaviour belong upstream, not in this repo

A test that asserts **what Business Central does** — with nothing runner-specific
in the claim — MUST live in the upstream corpus
[`StefanMaron/BusinessCentral.AL.Language.Tests`](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests)
(the `tests/al-language/` submodule), where it is validated against a **running BC
service tier**. It must not be written as a runner-local test in
`tests/runner-extras/`. An unvalidated BC test inherits the runner's errors as
its own expectations — green only means "the runner agrees with itself". Full
argument: `docs/upstream-corpus-workflow.md`.

## The test

Ask: *if AL Runner did not exist, would this test still be a meaningful statement
about AL/BC?*

- **Yes → upstream.** `Record.Insert` semantics, FlowField calculation, key
  handling, `TestPage` field validation, `Report.Run` execution order, virtual
  tables such as `AllObj` / `Table Metadata` / `Report Metadata` answering
  truthfully, Base App codeunits resolving what they resolve. All of it is BC
  behaviour that a service tier can adjudicate — so a service tier must.
- **No → `tests/runner-extras/`.** The claim only makes sense *because* this is
  the runner: `RunnerOutOfScopeException` thrown with a specific reason on a
  specific surface, AL-output cache HIT/MISS, provisioning-gap messages,
  multi-bundle/server-mode wiring, per-emitted-assembly module identity, exit
  codes.

Mixed suite? Split it. The BC assertions go upstream; only the runner-specific
ones stay. The repo already does this — see the LEAVE-BEHIND note at the top of
`tests/al-language/.../TestReportRunExecution.al`, which migrated the execution
tests upstream and deliberately kept exactly one runner-specific
OOS-classification test behind in `tests/runner-extras/report-run-execution`.

## Workflow when a fix needs a BC-behaviour test

The order matters, and step 3 is the one that is never optional. Full detail
on each step, including the escape hatches, is in `docs/upstream-corpus-workflow.md`.

1. **Write the test** against the corpus repo's conventions (fixtures, `Assert`,
   file layout) — not as a `runner-extras` bundle you intend to move later.
2. **Verify it against real BC** — a local container, or (the normal path for
   agents) let the corpus repo's own CI adjudicate on a PR; both are real
   service tiers.
3. **Open a pull request into
   [`StefanMaron/BusinessCentral.AL.Language.Tests`](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests).**
   Mandatory — a test only becomes part of the corpus by merging into that
   repo's `main`. The orchestrator merges it, not the authoring agent, once
   both BC legs are green.
4. **After that PR merges, bump the submodule pin** in this repo, folded into
   the fix PR (see `al-language-submodule.md` — a pin bump cannot be its own
   PR, it is red by construction).
5. **Then merge the runner change here**, showing the corpus test going
   RED → GREEN against the new pin.

**No local BC container** is not a blocker — open the corpus PR and let its CI
adjudicate (step 2). **No verdict available at all** (corpus CI broken, both BC
legs failing for unrelated reasons, behaviour not expressible in the corpus) —
you may not substitute a runner-local BC-behaviour test to unblock yourself;
say so plainly and land the runner fix with whatever runner-specific coverage
is legitimately available, recording the missing upstream test as follow-up.

## Not a licence to skip TDD

`tdd.md` still applies in full. This rule decides **where** the proving test
lives, never whether one exists. "It belongs upstream and I could not run a
service tier" is not an exemption from writing a test — it is a reason the change
may not be provable yet, which is information the reviewer needs.

## Sister rules

- `al-language-submodule.md` — the corpus is read-only here; how to bump the pin
- `tdd.md` — every fix needs a RED → GREEN, and tests must prove, not just pass
- `no-assumption-fixes.md` — understand the AL pattern before patching
- `file-issues-for-gaps.md` — gaps get tracked, never silently worked around
