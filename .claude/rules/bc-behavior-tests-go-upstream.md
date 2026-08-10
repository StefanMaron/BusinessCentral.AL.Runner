# Tests of plain BC behaviour belong upstream, not in this repo

A test that asserts **what Business Central does** — with nothing runner-specific
in the claim — MUST live in the upstream corpus
[`StefanMaron/BusinessCentral.AL.Language.Tests`](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests)
(the `tests/al-language/` submodule), where it is validated against a **running BC
service tier**. It must not be written as a runner-local test in
`tests/runner-extras/`.

## Why — a runner-local BC test proves nothing

The corpus is the spec *because* every test in it has been run against real BC.
A BC-behaviour test that has only ever run against AL Runner is not evidence about
BC; it is a transcript of **our belief** about BC, written by the same reasoning
that wrote the runtime.

So when the runner is wrong, such a test does not fail — it was authored to match
what the runner did. It goes green, the bug is now pinned as intended behaviour,
and every future change is measured against the wrong baseline. The suite gets
louder and less trustworthy at the same time.

That is the whole argument in one line: **an unvalidated BC test cannot prove the
runner correct, because it inherits the runner's errors as its expectations.**
Green means "the runner agrees with itself".

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

The order matters, and step 3 is the one that is never optional.

1. **Write the test** against the corpus repo's conventions (fixtures, `Assert`,
   file layout) — not as a `runner-extras` bundle you intend to move later.
2. **Verify it against real BC.** A local BC container with the BC repository is
   a perfectly good way to do this: publish the app, run the test, confirm it
   passes for the reason you think it does. This step exists to stop you sending
   a broken or wrongly-asserted test upstream — it does *not* by itself put the
   test in the corpus.
3. **Open a pull request into
   [`StefanMaron/BusinessCentral.AL.Language.Tests`](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests).**
   This is the mandatory step. A test only becomes part of the corpus by being
   merged into that repo's `main` — a test verified locally and never PR'd is
   not published, not reviewed, and not available to anyone else.
4. **After that PR merges, bump the submodule pin** in this repo — its own PR,
   diff inspected first (see `al-language-submodule.md`).
5. **Then merge the runner change here**, showing the corpus test going
   RED → GREEN against the new pin.

So a runner fix for a BC-behaviour gap is normally **two PRs in two repos, in
that order**: corpus first, runner second. Do not merge the runner change and
leave the upstream test as a promise — once the fix is in, nothing forces the
test to follow, and the gap quietly becomes untested behaviour.

**If you cannot verify against real BC at all** (no container, no service tier),
you may not substitute a runner-local BC-behaviour test to unblock yourself. Say
so plainly in the PR/issue and stop at the boundary: land the runner fix with
whatever runner-specific coverage is legitimately available, and record the
missing upstream test as follow-up. An unvalidated stand-in is worse than an
acknowledged gap, because it looks like coverage.

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
