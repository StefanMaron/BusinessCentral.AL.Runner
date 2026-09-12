# CLAUDE.md

Run Business Central AL unit tests in milliseconds — no service tier, no Docker, no SQL, no license. The goal is broad AL compatibility: any AL codeunit that can run without the BC service tier should compile and execute here. See `README.md` for architecture and `docs/limitations.md` for the hard architectural limits.

## Test corpus

The canonical test corpus is
[`StefanMaron/BusinessCentral.AL.Language.Tests`](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests) —
the AL-language spec, validated against a real BC service tier. The runner consumes it
read-only: **never modify files under `tests/al-language/`.**

It is **resolved per run, not pinned** (#3737). CI checks it out at `master`, or at the head
of the corpus pull request a PR body's `Corpus-PR:` line names, and every run prints
`corpus: <full sha> (<ref>)`. Locally, `tools/corpus-checkout.py` clones it into
`tests/al-language/` (gitignored) and prints the same line. Quote the SHA with any corpus
result — there is no gitlink to read it off afterwards (`.claude/rules/al-language-submodule.md`).

Tests that exercise surfaces the runner cannot support in-process (report rendering, SMTP,
HTTP egress, etc.) are declared in [`tests/expectations/`](tests/expectations/README.md);
[`docs/expectations.md`](docs/expectations.md) has the schema and result-classification
table. Runner-specific positive tests (e.g. proving `RunnerOutOfScopeException` is thrown
with the right reason on the right surface) live in `tests/runner-extras/`.

`tests/archive/` holds the legacy `bucket-1` / `bucket-2` / `excluded` etc. test trees; they
are no longer wired into CI and will be deleted once the al-language corpus + expectations
cover their cases.

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
- Review a PR before merge (does the test prove anything, did a BC claim reach a service tier, is the measurement sound, can anything fail silently) → sub-agent `reviewer`
- Drive a full work cycle (triage → parallel impls in worktrees → orchestrator merge pass, until the queue is empty) → slash command `/work-cycle`
- Run **unattended** (never-idle loop, one agent at a time, fixed priority order, weekly-budget pacing, preflight that refuses a box which would produce wrong answers) → skill `autonomous-cycle`
- Run Microsoft's BaseApp test buckets to find real gaps (sources, the exact configuration, sizing, clustering, and why `--test-data` is mandatory) → skill `running-ms-test-buckets`

### How a rule is written

A rule states a direction only with two independent instances or a cited measurement behind it;
a governance tool lands before the prose that cites it; and the incident that produced a rule is
written to `docs/incidents/<rule>.md`, never into the rule. What stays in the rule is claim +
citation + trap — what to do, what settled it, and the thing a later editor gets wrong
(`.claude/rules/loud-failures.md` § "The justification is a claim plus a citation, not the
derivation behind it"). A new rule is done when it carries that shape — claim, citation, trap — with
its derivation moved out and its incidents file present. **Keep it short, and treat length as a
symptom rather than a bar**: a rule that has grown long is usually carrying a derivation that
belongs in `docs/incidents/`, so move that and the length follows. There is deliberately no byte
figure here; the one this sentence used to carry was met by 2 of the 15 rules its own introducing
commit rewrote (#3952), so it cost an argument per rule and settled nothing.

## Code navigation: use these before grepping

Finding and reading code is the single biggest token cost in this repo, and the cost driver is
the **number** of round trips, not the size of any one result: every call re-sends the whole
accumulated conversation, so 200 small greps cost far more than 20 targeted ones. Use the tools
below before a grep sweep over `AlRunner/**/*.cs`. Re-measure the cost with
`tools/agent-cost.py <tasks-dir>` rather than trusting a figure written here (measurements:
docs/incidents/CLAUDE.md.md).

**0. `tools/context-pack.py` — one round trip, many answers.**

```bash
tools/context-pack.py <Name> [<Name>...]   # definition + source + call sites for each
```

Prefer it whenever you have more than one symbol to resolve; that is the whole point of it.
A `PreToolUse` hook (`.claude/hooks/prefer-code-navigation.py`) acts when a shell read or
search targets `AlRunner/**/*.cs`: it **blocks** when the PreToolUse payload's `agent_type` is
`impl-agent` or `reviewer` and stays an advisory reminder elsewhere, so the coordinator keeps
grep (#3707). Append `# hook:allow-grep` to override one call. Grep stays right everywhere for logs, JSON, TRX, markdown and `.al` sources.

**1. Knowledge graph — this is the one a subagent has.**

Rebuild AND query from `AlRunner/`, not the repo root:

```bash
cd AlRunner && graphify update .              # ~2 seconds, 200 files
cd AlRunner && graphify query "SomeSymbol callers"
```

Both commands default to `graphify-out/graph.json` **relative to the current directory**, so a
rebuild run from one directory and a query run from another silently use different files.
Rebuilding takes ~2 seconds, so rebuild rather than wonder whether it is current; in a worktree
the graph only drifts by your own edits.

**Phrase queries as bare symbols or `Symbol callers` — never as an English question.** The
start-node resolver matches on the words you type, so an English question matches on a stray
word, returns unrelated nodes, and gives no sign it failed.

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

It answers `findReferences`, `incomingCalls`, `goToDefinition` and `workspaceSymbol` for
`.cs`, and it is the sharpest instrument here: `findReferences` on
`GetDataAccessForTableCore` returns its three call sites across two partial-class files in one
call.

**The harness disables `LSP` inside subagents on build v2.1.252** (it worked on v2.1.152;
anthropics/claude-code#62904), and neither the agent's `tools:` frontmatter nor
`ENABLE_LSP_TOOL=1` re-enables it; re-measure on a new build before assuming either way. **If you are a subagent, use
`tools/lsp-query.py`** and do not spend calls rediscovering this.

When you are the main session briefing a subagent, resolve its symbols first and paste the
answers into the brief as `# LSP CONTEXT (pre-resolved)`, so it does not have to go looking.
Setup is in the README's tooling section (`mise use -g dotnet:csharp-ls` plus the `csharp-lsp`
plugin); if `LSP` reports no server for `.cs`, the plugin is not active — that is a setup
answer, never a "nothing calls this" answer.

**2c. The `bc-decompiler` MCP server — for "what does BC actually do".**

`mcp__bc-decompiler__*` reads `Microsoft.Dynamics.Nav.Ncl.dll` and friends directly:
`search_members` → `get_decompiled_source` → `find_callers` (which resolves through async
state machines), and `compare_symbols` diffs a method between two BC versions. One cached BC
version is one registered context, aliased `bc270` … `bc284`. If the tools are absent from
your session, nothing is broken — it is not installed or not loaded. Setup:

```bash
tools/setup-bc-decompiler.sh      # needs the .NET 10 SDK; the runner itself stays on net8.0
```

That clones and publishes `pardeike/DecompilerServer` into `$DECOMPILER_SERVER_DIR`
(default `~/Documents/Repos/tools/DecompilerServer`) and prints the block to put in
`.mcp.json` at the repository root:

```json
{ "mcpServers": { "bc-decompiler": { "type": "stdio", "command": "dotnet",
    "args": ["<DEST>/publish/DecompilerServer.dll"], "env": {} } } }
```

Two things about that file. `.mcp.json` is **gitignored** — per-machine, never committed. And
it is read **only at session start**, so a freshly written one does nothing for the session
you are in: "configured" and "usable right now" are different states, and only a restart
turns the first into the second. `tools/preflight.py` tells them apart by speaking MCP to the
server itself; in-session, `mcp__bc-decompiler__status` is the check.

A context needs its BC artifacts on disk first — `load_assembly` points at
`<artifacts>/<ver>/Microsoft.Dynamics.Nav.Ncl.dll` under `~/.local/share/al-runner/artifacts/`
(or `$AL_RUNNER_ARTIFACTS_ROOT`), so a version that was never provisioned cannot be
decompiled. `al-runner provision --bc-version <ver>` fetches it; then load once per version
(contexts persist in `~/.decompilerserver/` across restarts):

```
load_assembly(assemblyPath: "<artifacts>/<ver>/Microsoft.Dynamics.Nav.Ncl.dll",
              additionalSearchDirs: ["<artifacts>/<ver>"], contextAlias: "bc284")
```

**`list_contexts` answers two different questions in one response, and the wrong one is the
easier to read.** `registeredAliases` lists every alias that *can* be activated; `items` lists
the contexts actually **loaded**. An alias in the first and not the second means nothing has
been read through it — so a cross-version comparison resting on it is an inference, not a
measurement. Measured: an agent read `bc270`/`bc273` sharing an MVID from `items`, saw `bc275`
in `registeredAliases`, and concluded 27.5 was a distinct binary; hashing the files showed all
three `Ncl.dll` byte-identical. Hash the artifact, or load the context, before claiming two
versions differ.

**The mirror error is the commoner one: citing ONE binary under a version label that implies
independence.** 27.0, 27.3 and 27.5 are one file (sha256 `affa03c9…`, 10716984 bytes); 28.4 is a
different one (`6f2cf682…`, 11294560). So "measured on 27.5" and "27.0, 27.3 and 27.5 agree" are
the same single measurement, the second wearing three labels — and nothing in the first phrasing
looks like a claim about independence, which is why it passes review. **Cite the binaries you
measured, not the versions**: a count re-run across the 27.x/28.x boundary is two measurements,
and one anywhere inside 27.x is one. Measured twice: #3372 (the over-claim above) and #3859,
where an agent deleted a stale waiver recording exactly this hazard and then made the error the
waiver had described.


**2d. A `private` member in another file is usually still reachable — the file is not the class.**

`RecordPatches` is ONE `partial class` spread over **94 files**; `BcRuntime` over 24,
`NclCecilRewrite` and `ProgramSupport` over 9 each, `LiveNavTestPage` 8, `RunnerPageInstance` 4. So
a `private` member declared in one of those files is accessible from every other file declaring the
same class, and "it is private, and it is in a different file" is two true statements whose
conjunction implies something false.

Measured cost: issue #3933 advised widening a `private` decoder to `internal` or moving it, and the
coordinator repeated that in a dispatch brief; the calling file declared the same `partial class`
and had access all along (#3945). Nothing contradicts the reading until someone tries the call, and
the edit it argues for — widening visibility, or a new shared location — is a real diff in a hot
file.

```bash
command grep -n "partial class" <the-calling-file>   # same class? then you already have access
```

**3. `grep` here is a shell function, and it fails silently.**

`grep` resolves to a shell **function**, not `/usr/bin/grep`. It rejects `-E`, `--include` and
some pipelines with `error: unknown option '-G'` — and **exits 0 with no output**, which reads
exactly like "no matches found". That is a false negative, not an error you will notice.

```bash
command grep -E "pattern" file     # bypasses the function
rg --hidden "pattern"              # ripgrep, but see below: --hidden is not optional here
python3 - <<'EOF' ... EOF          # or do the scan in python, which also batches
```

**Never conclude "nothing matches" from a bare `grep -E` in this repo.** Re-run it with
`command grep` before believing an empty result.

**3b. `rg` skips dot-directories, and in THIS repo that hides everything that governs you.**

Both obvious tools have a silent-false-negative mode, for different reasons, and they look
identical from the outside:

| tool | failure | looks like |
|---|---|---|
| `grep -E` (the shell function) | rejects the flag, exits **0**, prints nothing | no matches |
| `rg` without `--hidden` | skips dot-directories entirely | no matches |

This bites harder here than in most repos, because nearly everything that governs agent
behaviour lives under `.claude/` — `rules/`, `skills/`, `agents/`, `hooks/`, `commands/`. So
"where is this instruction written down?" is exactly the search that comes back empty while
being wrong.

```bash
rg --hidden "pattern"                          # required for .claude/**
rg --hidden --glob '!.git' "pattern"           # ...and again when .git makes it noisy
```

**Never conclude "this text is not in the repo" from one tool alone.** Confirm an empty
result with the other one before believing it.

One more empty-looking answer that is not: **`mise` prints a banner on stdout**, so
`x=$(gh ... )` captures `mise ~/.config/mise/config.toml tools: gh@2.100.0` alongside — or
instead of — the value you wanted. **Filter a capture to the shape you expect**
(`| command grep -E '^[0-9]+$'`) rather than testing whether it is non-empty.

History: docs/incidents/CLAUDE.md.md
