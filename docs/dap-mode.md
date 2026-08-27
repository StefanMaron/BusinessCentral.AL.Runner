# `--dap` — Debug Adapter Protocol server

`al-runner --dap [PORT] <bundle-dir>` starts a real [Debug Adapter
Protocol](https://microsoft.github.io/debug-adapter-protocol/overview) server
over a TCP socket (default port `4711`, matching v1). `al-runner --dap stdio
<bundle-dir>` starts the identical session over the process's own
stdin/stdout instead — for a client that launches al-runner itself rather
than connecting to a port (issue #2058). Either way, it compiles the given
bundle, waits for a DAP client, and lets that client set breakpoints on AL
source lines, pause execution at them, step through the paused code
(`next`/`stepIn`/`stepOut`), and inspect the AL locals in scope at each pause
— with no BC service tier, no PDB, and no IL-offset mapping.

This started as the first slice of issue #1642 (breakpoints only); issue
#2045 added real step granularity; issue #2058 added the stdio transport.
See "What's not in this slice" below before assuming some other capability
exists.

## Mechanism

No new AL→source mapping was needed. BC's own AL compiler already instruments
every AL statement with `NavMethodScope.StmtHit(N)` (or `CStmtHit(N)` for an
`if`/`while`/`repeat` condition), and every generated scope class carries a
`[SourceSpans(...)]` attribute mapping each index `N` to an AL (file, line,
column) span — the same instrumentation `--coverage` (#1922) and
`--capture-values` (#1640) already consume. `--dap` adds a third,
unconditional Cecil prepend on those same methods
(`AlDapSession.OnStmtHit`, see `NclCecilRewrite.cs`) that blocks the AL
execution thread when the fired `(scope type, statement index)` pair matches
a registered breakpoint.

**Why pausing at `StmtHit(N)` is the correct boundary, not an approximation**:
BC calls `StmtHit(N)` *before* statement `N`'s own side effect runs. A
mainstream debugger's "stopped at line L" already means exactly that —
statement `L-1`'s effects are visible, statement `L`'s are not yet — so no
`Exit()`-style redesign (the fix `--capture-values` needed for its *final*
value snapshot) is required for pausing. See
`AlRunner/Infrastructure/AlDapSession.cs`'s file header for the full argument,
and `AlRunner/Infrastructure/AlDapStackWalker.cs` for a related, genuine gotcha
this issue's implementation hit and fixed: the paused frame's own
`StatementNumber` field is still the *previous* statement's index at the
instant the hook fires (the Cecil prepend runs before `StmtHit`'s own
assignment), so the stack walker uses the hook's `currentStatementNumber`
parameter for the topmost frame instead of the (stale) live property.

## Usage

```
al-runner --dap 4711 ./tests/some-bundle
```

The process prints `[dap] listening on 127.0.0.1:4711 — waiting for a debug
client to connect...` on stdout, then blocks until a client connects.

### stdio transport (`--dap stdio`)

```
al-runner --dap stdio ./tests/some-bundle
```

Speaks the identical DAP session over stdin/stdout instead of a socket — the
shape `vscode.DebugAdapterExecutable(command, args)` expects, so a client can
launch al-runner directly instead of spawning it, polling for a free port,
and connecting a `DebugAdapterServer(port)`. Chosen over a second flag
(`--dap-stdio`) because both forms are the same session over a different
transport, and `--dap [PORT|stdio]` says that at the call site: one flag,
one argument that's either "which port" or "no port, use my stdio".

**In this mode stdout carries ONLY the DAP wire format — Content-Length-framed
JSON, nothing else.** Every line of startup/diagnostic output that the TCP
path prints to stdout (including this loop's own readiness line) goes to
stderr instead, the same redirection `--server` already does for its own
protocol (`docs/server-mode.md`). A DAP client reading stdout as a strict
byte stream must never see anything but well-formed frames — see
`AlRunner.Tests/DapStdioClient.cs` for the harness that checks this
byte-for-byte, not just "the handshake succeeded".

Session lifecycle is identical to the TCP transport from here on:

1. `initialize` → capabilities, then an `initialized` event.
2. `launch`/`attach` → compiles the bundle. The response does not return until
   compilation finishes (success or failure), so a `setBreakpoints` request
   right after has real statement indices to resolve against.
3. `setBreakpoints` (per source file) → resolves each requested line to an
   AL-compiler-instrumented statement via an exact absolute-line match — no
   "nearest line" heuristic. A line with no exact instrumented statement comes
   back `verified: false` rather than silently relocated.
4. `configurationDone` → AL execution begins.
5. When a breakpointed statement's `StmtHit` fires, the AL execution thread
   blocks and a `stopped` event (`reason: "breakpoint"`) is sent.
6. `threads` / `stackTrace` / `scopes` / `variables` — read the paused call
   stack and each frame's `[NavName]`-tagged AL locals, live, via
   `AlScopeInspector`.
7. `continue` — resumes execution; only a registered breakpoint pauses it
   again. `next` (step over) / `stepIn` / `stepOut` (issue #2045) each arm a
   depth-based condition instead — the AL execution thread stops at the first
   subsequent `StmtHit` that "qualifies" for the command sent, and the
   `stopped` event's `reason` is `"step"` rather than `"breakpoint"`. See
   `AlRunner/Infrastructure/AlDapSession.cs`'s file header for exactly what
   "qualifies" means for each. The depth signal is a manual walk of
   `NavMethodScope.ParentScope` (the same chain `AlDapStackWalker` already
   walks to build stack frames), NOT the scope's own (internal) `StackDepth`
   property — measured directly: a local procedure call within the same
   codeunit gets its own `NavMethodScope` (a genuinely nested frame) but
   `StackDepth` on it comes back identical to its caller's, so a StackDepth-
   based check could not tell "entered a nested call" apart from "next
   statement, same scope" for exactly the case this feature needs to get
   right. The `ParentScope` walk is correct through recursion too.
8. `disconnect` / `terminate` — releases any paused thread (never leaves it
   stuck) and ends the session.

## Trying it without a DAP client

Any TCP client that speaks Content-Length-framed JSON can drive a session —
see `AlRunner.Tests/DapClient.cs` for a minimal one, or connect a raw socket
and write `Content-Length: <n>\r\n\r\n<json>` frames by hand.

## What's not in this slice

- **A VS Code launch configuration.** There is no `type` contribution a
  `launch.json` can point at without an installed extension; wiring this up
  belongs in the (separate-repo) AL Runner VS Code extension. Tracked as
  follow-up in #2046. See "VS Code integration status" below — the
  `debugServer` workaround this section used to suggest does not actually
  work with the currently shipped AL extension.
- **Multiple bundles in one session.** `--dap` currently refuses more than one
  bundle path.
- **`setVariable` / expression evaluation / conditional breakpoints.**
  `setVariable` is explicitly tracked separately (#2017); the others are not
  yet planned.

## VS Code integration status (#2046)

**Stock VS Code cannot attach to `--dap` from a plain `launch.json`.** A
`launch.json` entry's `type` must be contributed by an installed extension's
`contributes.debuggers` — VS Code has no generic "point me at a TCP DAP
server" debug type. This was already known going into #2046; it is confirmed
here rather than re-derived, since VS Code's extension model has not changed
in a way that would affect it.

**The previously-suggested `debugServer` workaround does not work.** This
document used to suggest borrowing the AL extension's own `type: "al"` with
`debugServer: 4711` to bypass its adapter and connect directly to al-runner's
DAP port, as a way to try the adapter without installing anything new. That
does not function with the currently shipped `ms-dynamics-smb.al` extension.
Verified by decompiling the installed extension bundles (17.0.2273547 and the
newer 18.0.2498801, both checked — same result in each) and VS Code's own
`extensionHostProcess.js`, rather than by trying it and guessing why it
failed:

- VS Code core's debug-adapter resolution (`getAdapterDescriptor` in
  `extensionHostProcess.js`) does exactly what the workaround assumes: if
  `session.configuration.debugServer` is a number, it connects a raw TCP
  socket to that port and never asks the extension for a debug adapter
  executable at all.
- But `session.configuration` there is the **resolved** configuration — the
  output of every registered `DebugConfigurationProvider.resolveDebugConfiguration`
  for that `type`, chained in sequence — not the literal object written in
  `launch.json`. `ms-dynamics-smb.al` registers exactly such a provider for
  `type: "al"` (`AlDebugConfigurationProvider`), and for both `launch` and
  `attach` requests it rebuilds the configuration object field-by-field from
  an explicit whitelist (`name`, `type`, `request`, `authentication`, `port`,
  `server`, `serverInstance`, `tenant`, ... — dozens of named fields). Neither
  build's whitelist includes `debugServer`, and the extension's bundled JS
  contains zero occurrences of the string `"debugServer"` anywhere — it has no
  special-case handling for it, so nothing preserves the field. By the time
  VS Code core checks `session.configuration.debugServer`, the field is gone,
  and it falls through to asking the AL extension for its own real debug
  adapter instead of al-runner's.
- This is a property of the currently-installed AL extension, not of VS Code:
  a debug `type` with **no** competing `resolveDebugConfiguration` (i.e. a
  type the AL extension does not own) would not have this problem, because
  there would be nothing rebuilding the configuration out from under
  `debugServer`.

**What this means for the follow-up in the (separate) AL Runner VS Code
extension**, restated from #1642/#2046 with the workaround option removed
since it does not hold up:

1. **A minimal extension contributing a new `debuggers` type needs no
   TypeScript**, and would not hit the problem above, because a fresh `type`
   name has no other extension's `resolveDebugConfiguration` in the way.
   `package.json` alone can declare `contributes.debuggers: [{ "type":
   "al-runner", "label": "AL Runner", "languages": ["al"],
   "configurationAttributes": {...} }]` plus a `configurationSnippets` entry
   whose body bakes in a fixed `debugServer` port — the user runs `al-runner
   --dap` themselves in a terminal first, same manual two-step this document
   already describes under "Trying it without a DAP client", just with a
   `launch.json` entry instead of raw sockets.
2. **Making the extension launch `al-runner --dap` itself** (instead of
   requiring the manual terminal step) needs a small amount of TypeScript: a
   `vscode.DebugAdapterDescriptorFactory` for that new type. As of #2058, the
   simplest shape is `return new vscode.DebugAdapterExecutable(alRunnerPath,
   ['--dap', 'stdio', bundleDir])` — VS Code launches the process and speaks
   DAP over its stdio directly, no port to pick and no readiness race to poll
   for. (The TCP form — spawning the process, waiting for it to report
   listening, then returning `vscode.DebugAdapterServer(port)` — still works
   and is what the extension already needs for `--server`
   (`docs/server-mode.md`), but stdio removes that extra step for `--dap`
   specifically.)
3. Whether the existing (separate-repo, not-in-this-repo) AL Runner VS Code
   extension can take either contribution cheaply is **not verified here** —
   this repo has no access to that extension's source. That remains the open
   question #2046's issue body raised, and is unchanged by this finding: it
   is a decision, and a change, for whoever owns that extension's repository.

## See also

- `docs/archive/dap.md` — v1's design notes for the same mechanism (some
  naming has since changed — `SourceLineMapper`/`ValueCapture` there map onto
  `AlSourceSpanCodec`/`AlScopeInspector` here — but the architecture holds).
- `AlRunner/Infrastructure/AlDapSession.cs`, `DapBreakpointResolver.cs`,
  `AlDapStackWalker.cs`, `AlScopeInspector.cs`, `DapTransport.cs`.
