# Contributing to BusinessCentral.AL.Runner

Thanks for your interest in contributing. This guide covers what every pull request must include before it can be merged.

---

## Before you start

Read `README.md` (architecture overview), [`docs/limitations.md`](docs/limitations.md) (hard architectural limits), [`docs/scope.md`](docs/scope.md) (per-API in/out-of-scope list), and [`docs/expectations.md`](docs/expectations.md) (test-expectation schema). `CLAUDE.md` is the entry point for AI agents working in the repo; it points at the rules in `.claude/rules/` and the on-demand reference in `.claude/skills/`.

The goal is broad AL-language compatibility — any AL code that can run without the BC service tier should compile and execute here. A small number of hard architectural limits exist (parallel sessions, transaction isolation, service-tier rendering, real HTTP), documented in `docs/limitations.md` and `docs/scope.md`. Everything else is a gap to close. Silent workarounds are forbidden: a gap goes to a GitHub issue and (if necessary) a `tests/expectations/` entry, never a quiet patch (`.claude/rules/file-issues-for-gaps.md`, `.claude/rules/loud-failures.md`).

---

## Repo layout

```
AlRunner/                — runner source (Program.cs, BcRuntime.cs, BcCompiler.cs,
                           BcAssembler.cs, TestExecutor.cs, Patches/, Infrastructure/)
tests/al-language/       — canonical AL test corpus, resolved per run, gitignored (READ-ONLY)
tests/expectations/      — JSON manifest: OOS-by-design, known gaps, disabled tests
tests/runner-extras/     — runner-specific positive tests (e.g. asserts that a
                           given surface throws RunnerOutOfScopeException)
docs/                    — expectations.md, scope.md, limitations.md,
                           cecil-migration.md (+ docs/archive/ for v1)
tools/                   — DownloadArtifacts (auto-used by AlRunner.csproj),
                           RuntimeApiEnumerator, telemetry-triage
scripts/                 — al-inventory.py, coverage-gen.js (auxiliary)
```

`tests/al-language/` is the read-only corpus. Never edit it. See `.claude/rules/al-language-submodule.md`.

---

## Dev loop

### Clone

```bash
git clone https://github.com/StefanMaron/BusinessCentral.AL.Runner
cd BusinessCentral.AL.Runner
```

The corpus is not in git — check it out (it prints the SHA it resolved):

```bash
tools/corpus-checkout.py
```

### Build

The build references the BC service-tier DLLs, which are not in the repo. The runner
**never auto-downloads** them: on a fresh clone the build fails loud, naming the exact
download command. Provision them once, either as part of the build:

```bash
dotnet build AlRunner.slnx -c Release -p:AllowBcArtifactDownload=true
```

or explicitly, ahead of the build:

```bash
dotnet run --project tools/DownloadArtifacts -- service-tier <version> <artifact-dir>
```

The version is `_BCVersion` in `AlRunner/AlRunner.csproj`, and the default artifact dir is
`<user-home>/.local/share/al-runner/artifacts/<version>` (that same layout on Windows, under
`%USERPROFILE%`). You do not have to look either up — the failing build prints the whole
command, filled in, ready to paste. Set `-p:ServiceTierPath=...` to point at an artifact dir
you already have instead, or `AL_RUNNER_ARTIFACTS_ROOT=<dir>` to move the whole cache (the
root the per-version subdirectories live under) somewhere else — the build and the runner
both read it, so the two never disagree about where the artifacts are.

Once the DLLs are present neither provisioning target runs, and the plain build is all
you need from then on:

```bash
dotnet build AlRunner.slnx -c Release
```

### Run the al-language corpus

The corpus is more than one app — the Cloud-target coverage app, its internals fixture, and
(from corpus PR #179 on) an OnPrem-target app for the `Scope = OnPrem` system tables a
Cloud app cannot name at all. Each is a separate bundle root. Let the enumeration find them,
the same way CI does, so a local run covers what CI covers:

```bash
mapfile -t CORPUS_APPS < <(python3 scripts/corpus-app-dirs.py tests/al-language)
dotnet run --project AlRunner -c Release -- "${CORPUS_APPS[@]}"
```

Naming `tests/al-language/tests/al-language` directly still works and is the right thing when
you want that one app. It is not the corpus: it is whichever app the path named when it was
written, which is how a second corpus app arrived and was never executed (#2984). The corpus
moves on its own now (#3737), so enumerate.

### Run with extra options

```bash
# Verbose internal logs + show passes
dotnet run --project AlRunner -c Release -- --verbose --show-pass tests/al-language/tests/al-language

# Choose isolation
dotnet run --project AlRunner -c Release -- --isolation test tests/al-language/tests/al-language

# Cache compiled AL output
dotnet run --project AlRunner -c Release -- --cache ~/.cache/al-runner/al-out tests/al-language/tests/al-language
```

### Move the corpus you are testing against

There is no pin to bump (#3737). A run resolves the corpus at `master`, or at the head of the
corpus pull request the PR body's `Corpus-PR:` line names, and prints
`corpus: <full sha> (<ref>)`. Locally:

```bash
tools/corpus-checkout.py                   # master
tools/corpus-checkout.py --corpus-pr 293   # a corpus pull request's head
tools/corpus-checkout.py --print           # what this checkout holds now
git -C tests/al-language log --oneline -5  # review what arrived
```

Corpus tests that newly fail are runner gaps — patch the runner (or add an expectation entry),
never the corpus. A whole new corpus **app** runs automatically: the app list is enumerated,
not named (#2984), and its tests are covered by the per-run count comparison rather than a
committed number (#3675).

---

## TDD is non-negotiable

`.claude/rules/tdd.md`. Strict red → green for every change.

1. **RED** — write the failing test first, run it, confirm it fails for the right reason.
2. **GREEN** — implement the fix, run again, confirm it passes.

Every test must cover both directions:
- **Positive** — correct input produces the expected concrete value (`Assert.AreEqual`).
- **Negative** — invalid input fails with the specific error (`asserterror` + `Assert.ExpectedError`).

Tests must **prove**, not just pass. A test that would still pass if the implementation always returned the default value (`0`, `''`, `false`) is noise — strengthen it. `Assert.IsTrue(true, ...)` and bare `asserterror` without `Assert.ExpectedError` are not tests. The only valid exception is a "no-op stub" test where the entire claim is "this does not crash" — name it `*_NoThrow` or `*_IsNoOp` so the limited claim is explicit.

Where the test lives:

| Kind of change | Test location |
|---|---|
| Runner can now run an AL pattern it couldn't before | A failing test in `tests/al-language/` that now passes (cite the test file in the PR body). If the corpus does not cover the pattern, write the test first against real BC in the `BusinessCentral.AL.Language.Tests` upstream repo and cite it with a `Corpus-PR:` line, which is what points your PR's matrix at it. |
| Runner-specific positive assertion (e.g. surface X throws OOS with reason Y) | New suite under `tests/runner-extras/`. |
| Test is OOS-by-design and the runner correctly refuses it | New entry in `tests/expectations/oos-<area>.json` per [`docs/expectations.md`](docs/expectations.md). |
| Test is in scope but the runner cannot run it yet | Open a GH issue; add a `known-gaps-<area>.json` entry linking the issue. |

There is no C# unit-test project. The deleted `AlRunner.Tests/` is gone; runner behaviour is asserted end-to-end through AL tests.

---

## PR contract

Branch name: `agent/<your-id>/issue-<N>` for agent work, otherwise a descriptive `feat/...` / `fix/...` name. Never push directly to `main` (`.claude/rules/branch-and-pr.md`).

Every PR must:

- Cite the test that proves the change. For a fix, point at the al-language test that now passes (or the new entry in `tests/runner-extras/` or `tests/expectations/`).
- Include `Closes #N` in the body if it addresses a GH issue.
- **Not** edit `CHANGELOG.md`. It is generated post-merge from squash-commit messages (`.claude/rules/no-changelog-edits.md`).
- **Not** edit anything under `tests/al-language/`. The corpus is read-only and is not in git at all — a corpus change goes upstream, cited with a `Corpus-PR:` line.
- Honour the precompiled-DLL contract: no rewriting method bodies or renaming types in MS / ISV business-logic DLLs (`.claude/rules/precompiled-dll-respect.md`). Runtime engine (`Ncl.dll`, `Types.dll`) and skeleton state are fair game.
- Make every unsupported surface **loud** — throw `RunnerOutOfScopeException` with a named API and reason from `docs/scope.md`. Never silently return a default (`.claude/rules/loud-failures.md`).
- Not be assumption-driven. If the triggering AL pattern is not clear from the issue body, ask the reporter — do not guess (`.claude/rules/no-assumption-fixes.md`).

---

## CI

Pull requests run against a matrix of BC versions. A PR cannot merge unless every job is green. Run the corpus locally before pushing:

```bash
mapfile -t CORPUS_APPS < <(python3 scripts/corpus-app-dirs.py tests/al-language)
dotnet run --project AlRunner -c Release -- "${CORPUS_APPS[@]}"
```

Exit codes: `0` all tests passed, `1` a test failed/errored, `2` a bundle could not execute (process-level error, or a bad invocation), `3` a bundle could not compile, `4` a `--count-baseline` mismatch. All non-zero codes fail CI.

---

## Reference: the rules

All loaded automatically by `.claude/` in agent sessions. Read them once:

- `.claude/rules/precompiled-dll-respect.md` — the load-chain contract; what we may not rewrite.
- `.claude/rules/loud-failures.md` — runtime-side: throw `RunnerOutOfScopeException`, never silent defaults.
- `.claude/rules/tdd.md` — red → green; cover both directions; prove, don't just pass.
- `.claude/rules/no-assumption-fixes.md` — investigate before patching; ask for reproducers.
- `.claude/rules/branch-and-pr.md` — branch naming, PR body, `status: review-ready`, one PR per impl agent.
- `.claude/rules/no-changelog-edits.md` — never touch `CHANGELOG.md`.
- `.claude/rules/al-language-submodule.md` — the corpus is read-only.
- `.claude/rules/file-issues-for-gaps.md` — gaps go to issues + expectation entries, never silent workarounds.
- `.claude/rules/github-access.md` — `gh` is absent in web/remote sessions; detect and fall back to `mcp__github__*`.
- `.claude/rules/bc-behavior-tests-go-upstream.md` — a test of plain BC behaviour goes upstream in the corpus, never `tests/runner-extras/`.
- `.claude/rules/no-git-stash-with-worktrees.md` — the stash is shared across every worktree; never use it here.
- `.claude/rules/ci-verdicts.md` — check merge conflicts before CI status; a verdict is per-commit-SHA; never re-run a failed job; "unrelated flake" needs evidence; drive a PR to merge, don't stop at "opened".
- `.claude/rules/local-test-scope.md` — run the tests your change touches locally; the full suite/corpus is CI's job.
- `.claude/rules/no-backgrounding-long-commands.md` — a backgrounded process dies with your turn; run long commands in the foreground.
- `.claude/rules/public-posting-approval.md` — filing issues on this repo needs no approval; comments, PR review comments, and anything on another repo do.
