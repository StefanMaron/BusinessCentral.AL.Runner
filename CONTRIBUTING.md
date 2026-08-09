# Contributing to BusinessCentral.AL.Runner

Thanks for your interest in contributing. This guide covers what every pull request must include before it can be merged.

---

## Before you start

Read `README.md` (architecture overview), [`docs/limitations.md`](docs/limitations.md) (hard architectural limits), [`docs/scope.md`](docs/scope.md) (per-API in/out-of-scope list), [`docs/subsystems.md`](docs/subsystems.md) (subsystem boundary map), and [`docs/expectations.md`](docs/expectations.md) (test-expectation schema). `CLAUDE.md` is the entry point for AI agents working in the repo; it points at the rules in `.claude/rules/` and the on-demand reference in `.claude/skills/`.

The goal is broad AL-language compatibility — any AL code that can run without the BC service tier should compile and execute here. A small number of hard architectural limits exist (parallel sessions, transaction isolation, service-tier rendering, real HTTP), documented in `docs/limitations.md` and `docs/scope.md`. Everything else is a gap to close. Silent workarounds are forbidden: a gap goes to a GitHub issue and (if necessary) a `tests/expectations/` entry, never a quiet patch (`.claude/rules/file-issues-for-gaps.md`, `.claude/rules/loud-failures.md`).

---

## Repo layout

```
AlRunner/                — runner source (Program.cs, BcRuntime.cs, BcCompiler.cs,
                           BcAssembler.cs, TestExecutor.cs, Patches/, Infrastructure/)
tests/al-language/       — git submodule, canonical AL test corpus (READ-ONLY)
tests/expectations/      — JSON manifest: OOS-by-design, known gaps, disabled tests
tests/runner-extras/     — runner-specific positive tests (e.g. asserts that a
                           given surface throws RunnerOutOfScopeException)
docs/                    — expectations.md, scope.md, limitations.md,
                           cecil-migration.md, subsystems.md (+ docs/archive/ for v1)
tools/                   — DownloadArtifacts (auto-used by AlRunner.csproj),
                           RuntimeApiEnumerator, telemetry-triage
scripts/                 — al-inventory.py, coverage-gen.js (auxiliary)
```

`tests/al-language/` is the read-only corpus. Never edit it. See `.claude/rules/al-language-submodule.md`.

---

## Dev loop

### Clone

```bash
git clone --recurse-submodules https://github.com/StefanMaron/BusinessCentral.AL.Runner
cd BusinessCentral.AL.Runner
```

If you already cloned without `--recurse-submodules`:

```bash
git submodule update --init --recursive
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
you already have instead.

Once the DLLs are present neither provisioning target runs, and the plain build is all
you need from then on:

```bash
dotnet build AlRunner.slnx -c Release
```

### Run the al-language corpus

```bash
dotnet run --project AlRunner -c Release -- tests/al-language/tests/al-language
```

### Run with extra options

```bash
# Verbose internal logs + show passes
dotnet run --project AlRunner -c Release -- --verbose --show-pass tests/al-language/tests/al-language

# Choose isolation
dotnet run --project AlRunner -c Release -- --isolation test tests/al-language/tests/al-language

# Cache compiled AL output
dotnet run --project AlRunner -c Release -- --cache ~/.cache/al-runner/al-out tests/al-language/tests/al-language
```

### Bump the corpus pin

The submodule is its own PR. Inspect the corpus diff before bumping:

```bash
git -C tests/al-language fetch
git -C tests/al-language log --oneline HEAD..origin/master
git -C tests/al-language diff HEAD..origin/master   # review
git -C tests/al-language checkout origin/master
git add tests/al-language
git commit -m "Bump tests/al-language to <sha>"
```

Tests that newly fail after the bump are runner gaps — patch the runner (or add an expectation entry), never the corpus.

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
| Runner can now run an AL pattern it couldn't before | A failing test in `tests/al-language/` that now passes (cite the test file in the PR body). If the corpus does not cover the pattern, write the test first against real BC in the `BusinessCentral.AL.Language.Tests` upstream repo, get it merged, then bump the submodule pin in your runner PR. |
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
- **Not** edit anything under `tests/al-language/`. The corpus is read-only; bump the submodule pin in a separate PR.
- Honour the precompiled-DLL contract: no rewriting method bodies or renaming types in MS / ISV business-logic DLLs (`.claude/rules/precompiled-dll-respect.md`). Runtime engine (`Ncl.dll`, `Types.dll`) and skeleton state are fair game.
- Make every unsupported surface **loud** — throw `RunnerOutOfScopeException` with a named API and reason from `docs/scope.md`. Never silently return a default (`.claude/rules/loud-failures.md`).
- Not be assumption-driven. If the triggering AL pattern is not clear from the issue body, ask the reporter — do not guess (`.claude/rules/no-assumption-fixes.md`).

---

## CI

Pull requests run against a matrix of BC versions. A PR cannot merge unless every job is green. Run the corpus locally before pushing:

```bash
dotnet run --project AlRunner -c Release -- tests/al-language/tests/al-language
```

Exit code 2 is "runner limitations only" — not a fail in itself, but treat it as work-to-do. Exit codes 1 and 3 always fail CI.

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
