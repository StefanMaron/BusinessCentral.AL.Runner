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
- Find a C# symbol's definition and callers without grepping → skill `find-code`
- Decompiler setup, citing a BC binary by build and hash, why `strings` cannot find a member → skill `inspecting-bc-binaries`

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
figure here; the one this sentence used to carry was met by only a few of the rules its own
introducing commit rewrote (#3952), so it cost an argument per rule and settled nothing.

## Code navigation: use these before grepping

Finding and reading code is the single biggest token cost in this repo, and the driver is the
**number** of round trips — every call re-sends the whole conversation, so 200 small greps cost
far more than 20 targeted ones. Use the tools below before a grep sweep over
`AlRunner/**/*.cs`; re-measure with `tools/agent-cost.py <tasks-dir>` (measurements:
docs/incidents/CLAUDE.md.md).

**0. `tools/context-pack.py <Name> [<Name>...]`** — definition + source + call sites for each,
in one round trip. Prefer it whenever you have more than one symbol. A `PreToolUse` hook
(`.claude/hooks/prefer-code-navigation.py`) **blocks** shell reads/searches of
`AlRunner/**/*.cs` for `impl-agent` and `reviewer` and only advises elsewhere (#3707); append
`# hook:allow-grep` to override one call. Grep stays right for logs, JSON, TRX, markdown and
`.al` sources.

**1. Knowledge graph — the one a subagent has.** Rebuild AND query from `AlRunner/`:

```bash
cd AlRunner && graphify update .              # seconds
cd AlRunner && graphify query "SomeSymbol callers"
```

Both default to `graphify-out/graph.json` **relative to the current directory**, so rebuilding
in one directory and querying from another silently uses different files; rebuild rather than
wonder whether it is current. **Phrase queries as bare symbols or `Symbol callers`, never as an
English question** — the resolver matches a stray word and gives no sign it failed. The graph is
**static** only: an orphaned hook and a live one look identical in it; use
`AL_RUNNER_HOOK_AUDIT=1` for "does this fire".

**2. `tools/lsp-query.py` — works everywhere, subagents included.**

```bash
tools/lsp-query.py callers <SymbolName>   # what calls it (no line/col needed)
tools/lsp-query.py symbol  <SymbolName>   # where it is defined
```

Exit 0 = answered, 1 = not in its index, **2 = the server failed and the result means
nothing** — never read a 2 as "nothing calls this". Exit 1 is a real negative for a type or an
ordinary member, but **not for a LOCAL FUNCTION** (declared inside another method): `csharp-ls`
does not index those, so confirm a surprising zero with `rg -n "<Name>" AlRunner/`. Full
guidance: skill `find-code`.

**2b. The built-in `LSP` tool — main session only.** It is the sharpest instrument here
(`findReferences`, `incomingCalls`, `goToDefinition`, `workspaceSymbol`), but **the harness
disables it inside subagents on build v2.1.252** (anthropics/claude-code#62904) and neither
`tools:` frontmatter nor `ENABLE_LSP_TOOL=1` re-enables it; re-measure on a new build. **A
subagent uses `tools/lsp-query.py`** and does not spend calls rediscovering this. A main session
briefing a subagent resolves its symbols first and pastes them as `# LSP CONTEXT
(pre-resolved)`. Setup: the README's tooling section; "no server for `.cs`" is a setup answer,
never a "nothing calls this" answer.

**2c. The `bc-decompiler` MCP server — for "what does BC actually do".**
`mcp__bc-decompiler__*` reads `Microsoft.Dynamics.Nav.Ncl.dll` and friends directly:
`search_members` → `get_decompiled_source` → `find_callers` (resolves through async state
machines); `compare_symbols` diffs a method between two BC versions. One context per version in
`.github/bc-versions.txt`, aliased `bc<major><minor>` (e.g. `bc284`). Absent tools mean not
installed or not loaded, not broken. Setup (`tools/setup-bc-decompiler.sh`; `.mcp.json` is
gitignored and read only at session start), loading a context, and reading `list_contexts` —
**`registeredAliases` is not loaded** — are in skill `inspecting-bc-binaries`.

**The version label does not identify the binary**, in either direction: several builds under
one label can be distinct files, and several labels can be one file. **Cite the build and the
hash** (`27.5.46862.53931 (affa03c9)`), never `27.5`, and hash the artifacts before claiming two
versions agree or differ (#3372, #3859, #4221; worked example: skill `inspecting-bc-binaries`).

**2d. A `private` member in another file is usually still reachable — the file is not the class.**

`RecordPatches` is ONE `partial class` spread over **many files**, and so are `BcRuntime`,
`NclCecilRewrite`, `ProgramSupport`, `LiveNavTestPage` and `RunnerPageInstance`. So a `private`
member declared in one of those files is accessible from every other file declaring the same
class; "private, and in a different file" does not mean unreachable (#3933, #3945).

```bash
command grep -n "partial class" <the-calling-file>   # same class? then you already have access
```

**3. `grep` here is a shell function, and it fails silently.** It rejects `-E`, `--include` and
some pipelines with `error: unknown option '-G'` and **exits 0 with no output**, which reads
exactly like "no matches". **Never conclude "nothing matches" from a bare `grep -E` here** — use
`command grep -E`, `rg --hidden`, or a python scan.

**3b. Four tools whose failure looks like "no matches".**

| tool | failure | looks like |
|---|---|---|
| `grep -E` (the shell function) | rejects the flag, exits **0**, prints nothing | no matches |
| `rg` without `--hidden` | skips dot-directories — all of `.claude/` | no matches |
| `gh <thing> list --limit N` | returns the first N and says nothing | the thing does not exist |
| `strings -el` on a .NET assembly | reads UTF-16 only, so a **member name** matches only by luck | the member is unreferenced |

- **`rg --hidden`** (plus `--glob '!.git'` when noisy) for anything under `.claude/` — rules,
  skills, agents, hooks — which is exactly where "where is this written down?" searches go.
- **`--limit` is a cap, never a page**, and it bites existence *checks* (#4170). Use
  `gh api <path> --paginate`, and `?per_page=1 --jq '.total_count'` to ask "how many"
  (`ci-verdicts.md`, the paged-count trap).
- **For "is this member referenced", read the `MemberReference` table** (the `bc-decompiler`
  tools, or `System.Reflection.Metadata`), never any `strings` form — names live in the UTF-8
  `#Strings` heap, literals in UTF-16 `#US` (#4229; table and measurement: skill
  `inspecting-bc-binaries`).
- **`mise` prints a banner on stdout**, so `x=$(gh ...)` can capture it instead of the value.
  Filter a capture to the shape you expect (`| command grep -E '^[0-9]+$'`), never test only
  that it is non-empty.

**Never conclude "this text is not in the repo" from one tool alone** — confirm an empty result
with the other.

**3c. `--body "..."` silently deletes your backticked spans.** A double-quoted string runs each
`` `span` `` as command substitution; `gh` exits 0 and the damage is public before you notice
(#4110). **Write markdown bodies through a quoted heredoc to a file and pass `--body-file`**;
never `--body "…"` for prose containing backticks, `$` or `!`, and re-read what you posted.
**Pick a delimiter the text cannot contain** — a heredoc ends at the first matching line,
including one inside the content. Writing the file from Python avoids both.

```bash
cat > "$f" <<'MDEOF'      # quoted delimiter: no substitution at all
... `code spans` and $vars stay literal ...
MDEOF
gh issue comment <N> --body-file "$f"
```

History: docs/incidents/CLAUDE.md.md
