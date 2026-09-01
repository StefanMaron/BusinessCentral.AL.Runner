---
name: find-code
description: Find where a C# symbol is defined and what calls it in AlRunner/, using the language server instead of grep. Use BEFORE grepping for a type, method, or field name — "who calls X", "where is X defined", "what would break if I change X", "find every path that reaches X". Works inside subagents, where the built-in LSP tool does not.
---

# Find code without grepping

`AlRunner/` is ~81,000 lines across 194 files, and two files are over 8,000 lines
each. Grep gives you line numbers you then have to read windows around; measured on
one implementation agent, that loop was **63% of all its tool calls**. The language
server answers the same questions exactly, in one call.

**The built-in `LSP` tool does not work in subagents on this Claude Code build** —
it returns `No such tool available: LSP`. This script gives you the same answers
through Bash, which you do have.

## Commands

```bash
tools/lsp-query.py callers <SymbolName>   # what calls it, and where it is defined
tools/lsp-query.py symbol  <SymbolName>   # where it is defined
tools/lsp-query.py refs <file> <line> <col>   # references at a position (1-based)
tools/lsp-query.py def  <file> <line> <col>   # definition of the symbol here
```

`callers` is the one you usually want, and it needs no line or column:

```
$ tools/lsp-query.py callers GetDataAccessForTableCore
object RecordPatches.GetDataAccessForTableCore(object self, NCLMetaTable table, bool isTemporary)  [defined AlRunner/Patches/RecordPatches.cs:1314]
    AlRunner/Patches/RecordPatches.InstallBaselineDisk.cs:68:20
    AlRunner/Patches/RecordPatches.cs:1301:26
```

That is complete — including the call site in a different partial-class file, which
a grep for the name in one file would have missed.

## Read the exit code. The three outcomes are NOT the same

| exit | meaning | what to do |
|---|---|---|
| **0** | answered, results printed | use them |
| **1** | answered, genuinely nothing found | a real negative — you may rely on it |
| **2** | the server failed, timed out, or is not installed | **NOT a negative.** Say so and fall back to grep |

Never treat exit 2 as "nothing calls this". That mistake reverses the meaning of
your result and any conclusion built on it. If it says `csharp-ls is not installed`,
tell the user to install it (README, tooling section) rather than silently grepping
for the rest of the session.

## Cost

Measured on this repo, one process per query, cold: **~8.5s** for a hit, ~10s for a
genuine miss. There is no daemon and none is needed. That is far cheaper than the
grep-then-read-several-windows loop it replaces.

## What it cannot tell you

It is static analysis. It cannot tell you whether a `Hook(...)` registration or a
Cecil rewrite actually **fires at runtime** — an orphaned hook and a live one look
identical to it. Use `AL_RUNNER_HOOK_AUDIT=1` for that question. For orientation in
an unfamiliar area ("what is near this"), the knowledge graph is better; see
CLAUDE.md.

## If you are the main session briefing a subagent

Resolve the symbols first and paste the answers into the brief, so the subagent does
not have to go looking:

```
# LSP CONTEXT (pre-resolved)
GetDataAccessForTableCore — AlRunner/Patches/RecordPatches.cs:1314
callers: RecordPatches.cs:1301, RecordPatches.InstallBaselineDisk.cs:68
```
