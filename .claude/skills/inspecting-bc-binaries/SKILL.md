---
name: inspecting-bc-binaries
description: Reference for reading BC's own assemblies — setting up and loading the bc-decompiler MCP server, reading list_contexts correctly, citing a BC binary by build and hash rather than by version label, and why `strings`/`strings -el` cannot answer "is this member referenced". Load it before you decompile, compare two BC versions, or byte-scan a .NET assembly. The directives live in CLAUDE.md § "Code navigation" (2c, 3b).
---

# Inspecting BC binaries

`CLAUDE.md` § 2c and § 3b state the rules; this is the reference behind them.

## Setting up the `bc-decompiler` MCP server

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

## `list_contexts`: registered is not loaded

`registeredAliases` lists every alias that *can* be activated; `items` lists the contexts
actually **loaded**. An alias in the first and not the second means nothing has been read
through it, so a cross-version comparison resting on it is an inference, not a measurement.
Measured: an agent read `bc270`/`bc273` sharing an MVID from `items`, saw `bc275` in
`registeredAliases`, and concluded 27.5 was a distinct binary; hashing the files showed all
three `Ncl.dll` byte-identical. Hash the artifact, or load the context, before claiming two
versions differ.

## Cite the build and the hash, never the version label

**The mirror error is the commoner one: citing ONE binary under a version label that implies
independence.** The builds `27.0.38460.53934`, `27.3.44313.53909` and `27.5.46862.53931` are one
file (sha256 `affa03c9…`, 10716984 bytes), so "measured on 27.5" and "27.0, 27.3 and 27.5 agree"
can be the same single measurement wearing three labels — and nothing in the first phrasing looks
like a claim about independence, which is why it passes review.

**But the version label does not identify the binary either way, so neither does a `27.x`/`28.x`
boundary.** A two-part version covers several builds and they are not all the same file:
`27.5.46862.48827` (`0a6ce45e…`) differs from `27.5.46862.53931` (`affa03c9…`), and the
`28.4.53241` builds are not one binary either. So "one anywhere inside 27.x is one measurement"
is false in both directions — two 27.5 results can be two binaries, and `28.0` through `28.4` can
be one. **Cite the build, and the hash**: `27.5.46862.53931 (affa03c9)` is a measurement, `27.5`
is not. `sha256sum` over the artifact directories is the whole check, and
`tools/test_bc_binary_identity_claims.py` pins the groupings this section states against whatever
is provisioned. Measured three times: #3372 (the over-claim above), #3859, where an agent deleted
a stale waiver recording exactly this hazard and then made the error the waiver had described, and
#4221, where this section's own example was the wrong shape.

## `strings` cannot tell you whether a member is referenced

"C# string literals are UTF-16, so `strings | grep` false-negatives on .NET assemblies; use
`strings -a -el`" circulates in agent briefs here and is half right — and the wrong half is the
half people reach for it with. A .NET assembly keeps the two in different heaps:

| what | heap | encoding | the flag that finds it |
|---|---|---|---|
| **member / type / namespace names** | `#Strings` | **UTF-8** | `strings -a` |
| **user string literals** (`"out-of-scope: ..."`) | `#US` | UTF-16 | `strings -a -el` |

Measured on `28.1.49838.53910/Microsoft.Dynamics.Nav.Ncl.dll`, for the metadata name
`RunRequestPageAsync`: `strings -a -el` finds **0**, `strings -a` finds **4**. So an agent told to
use `-el` and asked whether a *member* is referenced gets a clean zero and reads it as a finding
(`tools/test_strings_heap_claim.py` pins that sentence).

**A non-zero from `-el` does not rescue the method.** Some identifier-like `#Strings` names also
occur verbatim in `#US` — `ALDownloadFromStream` answers **1** under `-el` — because some
*literal* happens to spell the same text. That hit is never the metadata entry, and a scan that is
usually right by accident is worse than one that is always wrong, because the failures look like
data.

**For "is this member referenced", read the `MemberReference` table, not bytes.** A
`MemberReference` means this assembly **calls** the member, a `MethodDefinition` that it
**defines** it. On PR #4224 that split was load-bearing — the `.app` chunk referencing
`NavReport::RunRequestPageAsync` is also the one defining `RunReportRequestPage`, and only the
table distinguishes them (#4229). Use the `mcp__bc-decompiler__*` tools (`find_callers`,
`search_members`) or read the tables with `System.Reflection.Metadata`.
