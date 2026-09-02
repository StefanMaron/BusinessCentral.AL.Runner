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
- Run a coordinator session (delegation, identity reuse, corpus-PR authority, the merge bar,
  measurement rules, environment traps) → skill `orchestrating-a-session` — **invoke it at the
  start of any session that drives work through subagents**
- Act as orchestrator or implementation agent → sub-agents `orchestrator` / `impl-agent` in `.claude/agents/`
- Drive a full work cycle (triage → parallel impls in worktrees → orchestrator merge pass, until the queue is empty) → slash command `/work-cycle`

### Code navigation: use these before grepping

Finding and reading code is the single biggest token cost in this repo. **Re-measured
2026-09-02 across 17 subagents in one session: 3,545 tool calls, of which 3,266 were Bash,
and 2,775 of those (85%) were `grep`/`sed`/`cat`/`head`/`find` over the source tree.
`tools/lsp-query.py` was called ONCE in total; `graphify` twice.** Agents doing this ran two
hours and 300k tokens on a single cluster.

The cost driver is the **number** of round trips, not the size of any one result — the
average result was 1.3 KB, but every call re-sends the whole accumulated conversation, so
200 small greps cost far more than 20 targeted ones. `AlRunner/` is ~81,000 lines across
194 files with two files over 8,000 lines each, so a grep hit usually costs several
follow-up reads to interpret, and returns comment and string matches you then discount by
hand.

Re-measure with `tools/agent-cost.py <tasks-dir>` rather than trusting this paragraph — the
previous figure here ("63 greps + 50 file reads out of 180") sat stale for a long time
because nobody re-ran it.

**0. `tools/context-pack.py` — one round trip, many answers.**

```bash
tools/context-pack.py <Name> [<Name>...]   # definition + source + call sites for each
```

Prefer it whenever you have more than one symbol to resolve; that is the whole point of it.
A `PreToolUse` hook (`.claude/hooks/prefer-code-navigation.py`) prints a reminder when a
shell search targets `AlRunner/**/*.cs`. It is advisory and never blocks — grep stays right
for logs, JSON, TRX, markdown and `.al` sources.

**1. Knowledge graph — this is the one a subagent has.**

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
question.

**2. Language server via `tools/lsp-query.py` — works everywhere, subagents included.**

```bash
tools/lsp-query.py callers <SymbolName>   # what calls it (no line/col needed)
tools/lsp-query.py symbol  <SymbolName>   # where it is defined
```

~8.5s per query, one process, no daemon. Exit 0 = answered, 1 = a genuine
not-found you may rely on, **2 = the server failed and the result means nothing** —
never read a 2 as "nothing calls this". Full guidance: skill `find-code`.

**2b. The built-in `LSP` tool — main session only.**

The `LSP` tool answers `findReferences`, `incomingCalls`, `goToDefinition` and `workspaceSymbol`
for `.cs`, and it is the sharpest instrument here: `findReferences` on
`GetDataAccessForTableCore` returns its three call sites across two partial-class files in one
call.

**The harness disables `LSP` inside subagents on this build (v2.1.252).** Measured: a subagent
calling it gets `No such tool available: LSP. LSP is disabled for this session, in subagents as
well as here.` Adding `LSP` to the agent's `tools:` frontmatter does not help, and neither does
`ENABLE_LSP_TOOL=1`. It did work in subagents on v2.1.152
(anthropics/claude-code#62904), so this is a harness change, not a property of language servers —
which is why `tools/lsp-query.py` above exists. If you are a subagent, use that script; do not
spend calls rediscovering this.

When you are the main session briefing a subagent, resolve its symbols first and paste the
answers into the brief as `# LSP CONTEXT (pre-resolved)`, so it does not have to go looking.

If you are the main session, use it. Setup is in the README's tooling section
(`mise use -g dotnet:csharp-ls` plus the `csharp-lsp` plugin); if `LSP` reports no server for
`.cs`, the plugin is not active — that is a setup answer, never a "nothing calls this" answer.
