# `--dap` — Debug Adapter Protocol server

`al-runner --dap [PORT] <bundle-dir>` starts a real [Debug Adapter
Protocol](https://microsoft.github.io/debug-adapter-protocol/overview) server
over a TCP socket (default port `4711`, matching v1). It compiles the given
bundle, waits for a DAP client to connect, and lets that client set
breakpoints on AL source lines, pause execution at them, and inspect the AL
locals in scope at the pause — with no BC service tier, no PDB, and no
IL-offset mapping.

This is the first slice of issue #1642. See "What's not in this slice" below
before assuming a capability exists.

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
client to connect...` on stdout, then blocks until a client connects. Session
lifecycle:

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
7. `continue` / `next` / `stepIn` / `stepOut` — resumes execution (all four
   behave identically in this slice; see below).
8. `disconnect` / `terminate` — releases any paused thread (never leaves it
   stuck) and ends the session.

## Trying it without a DAP client

Any TCP client that speaks Content-Length-framed JSON can drive a session —
see `AlRunner.Tests/DapClient.cs` for a minimal one, or connect a raw socket
and write `Content-Length: <n>\r\n\r\n<json>` frames by hand.

## What's not in this slice

- **Real step granularity.** `next`/`stepIn`/`stepOut` all behave like
  `continue` — none of them pause at the very next statement the way a real
  single-step would. Tracked as follow-up.
- **A VS Code launch configuration.** There is no `type` contribution a
  `launch.json` can point at without an installed extension; wiring this up
  belongs in the (separate-repo) AL Runner VS Code extension. Tracked as
  follow-up. Until then, `{"type": "al", "request": "attach", "debugServer":
  4711}` (borrowing the AL extension's own dev-mode DAP attach point) is a
  workaround for manual trying-out, not a supported story.
- **Multiple bundles in one session.** `--dap` currently refuses more than one
  bundle path.
- **`setVariable` / expression evaluation / conditional breakpoints.**
  `setVariable` is explicitly tracked separately (#2017); the others are not
  yet planned.

## See also

- `docs/archive/dap.md` — v1's design notes for the same mechanism (some
  naming has since changed — `SourceLineMapper`/`ValueCapture` there map onto
  `AlSourceSpanCodec`/`AlScopeInspector` here — but the architecture holds).
- `AlRunner/Infrastructure/AlDapSession.cs`, `DapBreakpointResolver.cs`,
  `AlDapStackWalker.cs`, `AlScopeInspector.cs`, `DapTransport.cs`.
