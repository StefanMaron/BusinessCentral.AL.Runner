# CLAUDE.md

Run Business Central AL unit tests in milliseconds — no service tier, no Docker, no SQL, no license. The goal is broad AL compatibility: any AL codeunit that can run without the BC service tier should compile and execute here. See `README.md` for architecture and `docs/limitations.md` for the hard architectural limits.

## Test corpus

The canonical test corpus is the **`tests/al-language/` git submodule** pointing at
[`StefanMaron/BusinessCentral.AL.Language.Tests`](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests).
That repo is the AL-language spec, validated against a real BC service tier. The
runner consumes it read-only — **never modify files under `tests/al-language/`**.

Tests that exercise surfaces the runner cannot support in-process (report
rendering, SMTP, HTTP egress, etc.) are declared in
[`tests/expectations/`](tests/expectations/README.md). See
[`docs/expectations.md`](docs/expectations.md) for the schema and result-classification
table. Runner-specific positive tests (e.g. proving `RunnerOutOfScopeException`
is thrown with the right reason on the right surface) live in `tests/runner-extras/`.

`tests/archive/` holds the legacy `bucket-1` / `bucket-2` / `excluded` etc.
test trees; they are no longer wired into CI and will be deleted once the
al-language corpus + expectations cover their cases.

## Operating rules and skills

Operating rules live in `.claude/rules/` and are auto-loaded. Task-specific reference is on-demand:

- Pipeline / architecture / key files → skill `al-runner-architecture`
- Fixing gaps by reusing BC's service tier / patching the runtime engine (proven: BC's compiler runs headless on Linux) → [`docs/service-tier-reuse.md`](docs/service-tier-reuse.md)
- Writing AL tests, bucket layout, running the matrix → skill `al-runner-tests`
- `--guide` flag, full agent workflow contract → skill `al-runner-workflow`
- Triage new untriaged issues → sub-agent `triager` (Opus, runs once at the start of a cycle)
- Act as orchestrator or implementation agent → sub-agents `orchestrator` / `impl-agent` in `.claude/agents/`
- Drive a full work cycle (triage → parallel impls in worktrees → orchestrator merge pass, until the queue is empty) → slash command `/work-cycle`

### Code navigation: use these before grepping

Finding and reading code is the single biggest token cost in this repo — measured at **63% of
one implementation agent's tool calls** (63 greps + 50 file reads out of 180). `AlRunner/` is
~81,000 lines across 194 files, and two files are over 8,000 lines each, so a grep hit usually
costs several follow-up reads to interpret. Reach for a real navigation tool first.

**1. Language server (best for "who calls this", "where is this defined").**

The `LSP` tool answers `findReferences`, `incomingCalls`, `outgoingCalls`, `goToDefinition`,
`goToImplementation` and `workspaceSymbol` for `.cs`. It is provided by the `csharp-lsp`
plugin driving `csharp-ls`; both are installed on the maintainer's machine
(`mise use -g dotnet:csharp-ls`, `claude plugin install csharp-lsp@claude-plugins-official`).

Verified against this repo: it loads `AlRunner.slnx`, does not trip on the
`EnsureBCServiceTierDlls` target, and returns full signatures —
`workspace/symbol "GetDataAccessForTableCore"` resolves to
`RecordPatches.GetDataAccessForTableCore(object self, NCLMetaTable table, bool isTemporary)` at
`AlRunner/Patches/RecordPatches.cs:1314`.

If `LSP` reports no server for `.cs`, the plugin is not enabled in that session — say so and
fall back, do not treat it as "no results".

**2. Knowledge graph (best for "what is near this", orientation in an unfamiliar area).**

Rebuild AND query from `AlRunner/`, not the repo root:

```bash
cd AlRunner && graphify AlRunner --update     # ~2 seconds, 191 files
cd AlRunner && graphify query "SomeSymbol callers"
```

Both commands default to `graphify-out/graph.json` **relative to the current directory**, so a
rebuild run from one directory and a query run from another silently use different files. That
mismatch is why an earlier root-level copy sat 13 days stale while the documented rebuild
appeared to work.

**Phrase queries as bare symbols or `Symbol callers` — never as an English question.** The
start-node resolver matches on the words you type, so `graphify query "what calls
GetDataAccessForTableCore"` matches **CallSiteArgWrap** on the word *calls*, returns 2 unrelated
nodes, and gives no sign it failed. The same question as `"GetDataAccessForTableCore callers"`
returns the correct 66-node neighbourhood.

Rebuilding takes ~2 seconds, so rebuild rather than wonder whether it is current. In a worktree
the graph only drifts by your own edits.

The graph maps **static** structure only: which types and files reference which. It cannot tell
you whether a `Hook(...)` registration or a Cecil rewrite actually fires at runtime — an
orphaned hook and a live one look identical in it. Use `AL_RUNNER_HOOK_AUDIT=1` for that
question, and see the README's Knowledge graph section for which graphify build to install.
