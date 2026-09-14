# Incidents behind CLAUDE.md

Narrative and measurements moved verbatim out of CLAUDE.md (#3728). CLAUDE.md keeps the commands and the traps; re-measure with `tools/agent-cost.py <tasks-dir>` rather than trusting any number here.

## Code navigation: use these before grepping

Finding and reading code is the single biggest token cost in this repo. **Re-measured
2026-09-02 across 17 subagents in one session: 3,545 tool calls, of which 3,266 were Bash,
and 2,775 of those (85%) were `grep`/`sed`/`cat`/`head`/`find` over the source tree
(an earlier count of the same session: 3,237 Bash, 2,716 of them — 84% — such searches).
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

### 1. Knowledge graph

Both commands default to `graphify-out/graph.json` **relative to the current directory**, so
a rebuild run from one directory and a query run from another silently use different files —
that mismatch is why an earlier root-level copy sat 13 days stale while the documented
rebuild appeared to work. Rebuilding takes ~2 seconds, so rebuild rather than wonder whether
it is current; in a worktree the graph only drifts by your own edits.

**Phrase queries as bare symbols or `Symbol callers` — never as an English question.** The
start-node resolver matches on the words you type, so `graphify query "what calls
GetDataAccessForTableCore"` matches **CallSiteArgWrap** on the word *calls*, returns 2 unrelated
nodes, and gives no sign it failed. The same question as `"GetDataAccessForTableCore callers"`
returns the correct 66-node neighbourhood.

### 2b. The built-in `LSP` tool

**The harness disables `LSP` inside subagents on this build (v2.1.252).** Measured: a subagent
calling it gets `No such tool available: LSP. LSP is disabled for this session, in subagents as
well as here.` Adding `LSP` to the agent's `tools:` frontmatter does not help, and neither does
`ENABLE_LSP_TOOL=1`. It did work in subagents on v2.1.152
(anthropics/claude-code#62904), so this is a harness change, not a property of language servers —
which is why `tools/lsp-query.py` above exists. If you are a subagent, use that script; do not
spend calls rediscovering this.

### 3. `grep` here is a shell function

Measured in this environment: `grep` resolves to a shell **function**, not `/usr/bin/grep`.
It rejects `-E`, `--include` and some pipelines with `error: unknown option '-G'` — and
**exits 0 with no output**, which reads exactly like "no matches found". That is a false
negative, not an error you will notice; an agent burned several calls on it before running
`type grep`, and it silently corrupted intermediate results before that.

### 3b. `rg` skips dot-directories

This bites harder here than in most repos, because nearly everything that governs agent
behaviour lives under `.claude/` — `rules/`, `skills/`, `agents/`, `hooks/`, `commands/`. So
"where is this instruction written down?" is exactly the search that comes back empty while
being wrong. Measured while correcting a false claim in two `SKILL.md` files: `rg "clean
status" .` found **nothing**, with the phrase sitting in two files the command never opened.


One more empty-looking answer that is not: **`mise` prints a banner on stdout**, so
`x=$(gh ... )` captures `mise ~/.config/mise/config.toml tools: gh@2.100.0` alongside — or
instead of — the value you wanted. It has now corrupted a `$(...)` capture, a `preflight`
health check, and a PR-existence guard that reported "a PR already exists" when none did.
Filter to the shape you expect (`| command grep -E '^[0-9]+$'`) rather than testing whether
the capture is non-empty.

## Residue moved from CLAUDE.md (#3728 review round 3)

Re-measure with `tools/agent-cost.py <tasks-dir>` rather than trusting that paragraph — the
previous figure there ("63 greps + 50 file reads out of 180") sat stale for a long time because
nobody re-ran it.

## Backticks in `--body "..."` silently delete code spans (2026-09-13, #4110)

Posting an issue comment with `--body` and a double-quoted shell string whose prose contained
backticked code spans: the shell evaluated each span as command substitution before `gh` ever
saw it.

Published text:

```
Reading the oldest queued run's jobs (, queued since ):
```

Intended text named run `34738478169`, queued since `2026-09-13T04:41:30Z`. Both were consumed;
`bash` wrote `34738478169: command not found` to stderr, `gh` exited 0 and printed the comment
URL, and the comment was live and wrong.

Three properties make it the same class as the `grep -E` and `rg --hidden` traps already in
`CLAUDE.md`:

- the command **succeeds** — exit 0, a real URL returned;
- the loss is **invisible from the caller's side**, since the stderr line scrolls past among
  tool output and the posted body is never echoed back;
- it is only findable by **re-reading the artifact**, which is the same remedy those traps need.

It is worse in one respect: the other two produce a wrong *answer* to yourself, and this
produces a wrong *publication* to everyone else, already sent by the time you look.

Caught here by re-reading the comment after noticing two `command not found` lines in the tool
output. The remedy in the rule — a quoted heredoc to a file, then `--body-file` — also removes
the neighbouring hazards (`$` expansion, `!` history), and is what every long body in this
session already used; the failure came from the one that was written inline because it was
"short".
