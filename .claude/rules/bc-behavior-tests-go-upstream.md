# Tests of plain BC behaviour belong upstream, not in this repo

A test that asserts **what Business Central does** — with nothing runner-specific in the claim
— MUST live in the upstream corpus
[`StefanMaron/BusinessCentral.AL.Language.Tests`](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests)
(the `tests/al-language/` submodule), where it is validated against a **running BC service
tier**, not as a runner-local test in `tests/runner-extras/`. An unvalidated BC test inherits
the runner's errors as its own expectations — green then only means "the runner agrees with
itself". Full argument: `docs/upstream-corpus-workflow.md`.

## The test

Ask: *if AL Runner did not exist, would this test still be a meaningful statement about AL/BC?*

- **Yes → upstream.** `Record.Insert` semantics, FlowField calculation, key handling,
  `TestPage` field validation, `Report.Run` execution order, virtual tables such as `AllObj` /
  `Table Metadata` / `Report Metadata` answering truthfully, Base App codeunits resolving what
  they resolve. All BC behaviour a service tier can adjudicate — so a service tier must.
- **No → `tests/runner-extras/`.** The claim only makes sense *because* this is the runner:
  `RunnerOutOfScopeException` thrown with a specific reason on a specific surface, AL-output
  cache HIT/MISS, provisioning-gap messages, multi-bundle/server-mode wiring,
  per-emitted-assembly module identity, exit codes.

Mixed suite? Split it — BC assertions upstream, only the runner-specific ones stay. The repo
already does this: the LEAVE-BEHIND note at the top of
`tests/al-language/.../TestReportRunExecution.al` migrated the execution tests upstream and
deliberately kept exactly one runner-specific OOS-classification test behind in
`tests/runner-extras/report-run-execution`.

## Workflow when a fix needs a BC-behaviour test

Step 3 is the one that is never optional. Full detail, including escape hatches:
`docs/upstream-corpus-workflow.md`.

1. **Write the test** against the corpus repo's conventions (fixtures, `Assert`, file layout) —
   not as a `runner-extras` bundle you intend to move later.
2. **Verify it against real BC** — a local container, or (the normal path for agents) let the
   corpus repo's own CI adjudicate on a PR; both are real service tiers.
3. **Open a pull request into
   [`StefanMaron/BusinessCentral.AL.Language.Tests`](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests).**
   Mandatory — a test only becomes part of the corpus by merging into that repo's `master`. The
   orchestrator merges it, not the authoring agent, once the corpus's eight required BC
   legs — one per version — are green. Eight OnPrem legs run alongside without gating.
4. **After that PR merges, bump the submodule pin** in this repo, folded into the fix PR
   (`al-language-submodule.md` — folding it in is the *fold* case; a bump whose fix has already
   merged is a catch-up bump and is legitimately its own PR).
5. **Then merge the runner change here**, showing the corpus test going RED → GREEN against the
   new pin.

**No local BC container** is not a blocker — open the corpus PR and let its CI adjudicate (step
2). **No verdict available at all** (corpus CI broken, the BC legs failing for unrelated
reasons, behaviour not expressible in the corpus) — you may not substitute a runner-local
BC-behaviour test to unblock yourself; say so plainly and land the runner fix with whatever
runner-specific coverage is legitimately available, recording the missing upstream test as
follow-up.

## Declare the linkage in the PR body — the gate accepts exactly one shape

`pr-gate.yml`'s `AL-observable changes must declare corpus linkage` job
(`.github/scripts/check_corpus_linkage.sh`, #3255) blocks the merge of any PR that touches
`AlRunner/Patches/`, `AlRunner/Rewriters/`, `AlRunner/Infrastructure/NclCecilRewrite*`,
`AlRunner/BcCompiler*` or `AlRunner/BcAssembler.cs` (non-`.md`) unless the **PR body** carries
one of these, each **on its own line, marker and value together**:

```
Corpus-PR: https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/226
Corpus-NA: precompiled-dependency path; a corpus test source-compiles and would pass
```

The `Corpus-PR:` line is matched by one regex: optional leading whitespace, the marker, the
full `.../BusinessCentral.AL.Language.Tests/pull/<N>` URL, optionally a trailing `/` or `.`,
nothing else. The `Corpus-NA:` reason is free text and must not be a placeholder (`n/a`,
`none`, `TBD`, `-`, …). Both forms are case-insensitive. Shapes that went red on real PRs in
one day (#3330), each pinned in `test_check_corpus_linkage.sh`:

| written | why it fails |
|---|---|
| `` The `Corpus-PR:` for this is #226 `` | mid-sentence, backticks, and no URL |
| `Corpus-PR: [#228](https://…/pull/228)` | a markdown link is not a bare URL |
| `Corpus-PR:` on one line, the URL on the next | the marker and the URL must share a line — a brief saying "a bare full URL on its own line" produces this |
| `**Corpus-PR:** https://…/pull/226` | bold markers break the marker |
| `Corpus-PR: <https://…/pull/226>` | angle-bracket autolinks break the URL |

A `Corpus-PR:` line that fails the regex is reported as *malformed*, not absent, so the log
says which of the two you have. Check before pushing — the script takes the body and the
changed paths from the environment, so this is a five-second local check:

```bash
PR_BODY="$(cat body.md)" CHANGED_FILES="$(git diff --name-only origin/main...HEAD)" \
  bash .github/scripts/check_corpus_linkage.sh
```

The gate checks that you **declared** something; whether the declaration is right is the
reviewer's call, never CI's.

## Not a licence to skip TDD

`tdd.md` still applies in full. This rule decides **where** the proving test lives, never
whether one exists. "It belongs upstream and I could not run a service tier" is not an
exemption from writing a test — it is a reason the change may not be provable yet, which is
information the reviewer needs.

## Sister rules

- `ask-the-corpus-before-claiming-bc-behavior.md` — before you act on a belief
  about what BC does, read the corpus CI's verdict; a green corpus test outranks
  reading, a container differential, the docs, and a codeunit's name
- `verify-execution-not-the-tick.md` — a green corpus leg does not prove your tests ran;
  `tools/corpus-pass-count.py` answers it, and the hand-rolled check false-zeros five ways
- `al-language-submodule.md` — the corpus is read-only here; how to bump the pin
- `tdd.md` — every fix needs a RED → GREEN, and tests must prove, not just pass
- `no-assumption-fixes.md` — understand the AL pattern before patching
- `file-issues-for-gaps.md` — gaps get tracked, never silently worked around
