# DAP Debugger Support for al-runner

This document describes the architecture and usage of al-runner's Debug Adapter Protocol (DAP) server, which enables VS Code and other DAP-compliant IDEs to set breakpoints and inspect variables during AL test execution — with no BC service tier required.

## Feasibility Summary

DAP is fully implementable on top of al-runner's existing instrumentation:

| Infrastructure              | Role in Debugger                                                |
|-----------------------------|-----------------------------------------------------------------|
| `StmtHit(N)` / `CStmtHit(N)` | Every AL statement is a potential breakpoint                   |
| `SourceLineMapper`          | Maps (scope, stmtId) ↔ (AL source file, line, column)         |
| `ValueCapture`              | Captures local variable values after each assignment           |
| `AlScope.LastStatementHit`  | Tracks the most recently executed statement for stack traces   |

No BC service tier, no IL offsets, no external debugger protocol adapter. Pure .NET.

## Architecture

```
VS Code / DAP client
       │ TCP (port 4711)
       ▼
┌──────────────────────────────────────────────────────┐
│                   DapServer                          │
│  ┌─────────────┐     ┌────────────────────────────┐  │
│  │  ReadLoop   │     │    AL Pipeline (thread)    │  │
│  │  (I/O)      │     │  AlRunnerPipeline.Run()    │  │
│  └──────┬──────┘     └──────────────┬─────────────┘  │
│         │                           │                 │
│    requests / responses        StmtHit(N) calls       │
│         │                           │                 │
│  ┌──────▼────────────────────────────▼───────────┐    │
│  │             BreakpointManager                 │    │
│  │  RegisterBreakpoint(scopeName, stmtId)        │    │
│  │  CheckHit(scopeName, stmtId) → SemaphoreSlim  │    │
│  │  Continue() → release semaphore               │    │
│  └───────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────┘
```

### Execution Flow

1. **Start**: `al-runner --dap 4711 ./src ./test`
2. **Handshake**: IDE connects via TCP, sends `initialize` → server responds with capabilities + `initialized` event
3. **Breakpoints**: IDE sends `setBreakpoints` with (file, line) pairs → server translates to (scopeName, stmtId) via `SourceLineMapper.FindStatementsForAlLine()` → registers in `BreakpointManager`
4. **Run**: IDE sends `configurationDone` → server releases pipeline to start
5. **Breakpoint Hit**: `StmtHit(N)` fires → `BreakpointManager.CheckHit()` blocks the execution thread → `BreakpointHit` event fires → server sends DAP `stopped` event to IDE
6. **Inspection**: IDE sends `stackTrace`, `scopes`, `variables` → server reads `AlScope.LastStatementHit` and `ValueCapture.GetCaptures()`
7. **Resume**: IDE sends `continue` → server calls `BreakpointManager.Continue()` → execution thread unblocks → pipeline continues

### Key Design Decisions

**Single SemaphoreSlim for pause/resume**: al-runner runs tests on a single thread (per test). Only one thread is ever paused at a time, making a simple semaphore sufficient. A future multi-threaded executor would need a per-thread pause mechanism.

**BreakpointManager is always wired**: `AlScope.StmtHit` always calls `BreakpointManager.CheckHit()`. When the manager is disabled (the default), `CheckHit` is a near-zero-cost volatile read. No overhead for normal test runs.

**Source mapping via StmtHit IDs**: The BC compiler emits `StmtHit(N)` at every statement. `SourceLineMapper` builds a bidirectional map between these N values and AL source (file, line, column). `FindStatementsForAlLine()` does the reverse lookup needed for `setBreakpoints`.

**Variable capture via ValueCapture**: `ValueCaptureInjector` already injects `ValueCapture.Capture()` calls after every assignment in scope classes. At a breakpoint, the server returns all captured values for the current scope. This gives "post-assignment" variable state, not the exact state at the paused line (a common debugger approximation).

**No IL offset mapping**: Unlike a full .NET debugger that maps source lines to IL opcodes, al-runner uses `StmtHit(N)` as the resolution boundary. This means breakpoints can only be set at lines where the BC compiler emits a `StmtHit` call (every executable statement). Non-executable lines (blank, comment, declaration) are reported as unverified.

## DAP Messages Supported

| Command            | Status   | Notes                                                   |
|--------------------|----------|---------------------------------------------------------|
| `initialize`       | ✓        | Returns capabilities, fires `initialized` event         |
| `launch`           | ✓        | Acknowledged; pipeline starts after `configurationDone` |
| `attach`           | ✓        | Same as launch                                          |
| `setBreakpoints`   | ✓        | Translates (file, line) → (scopeName, stmtId)          |
| `configurationDone`| ✓        | Gates pipeline start                                    |
| `threads`          | ✓        | Returns single "Main Thread"                            |
| `stackTrace`       | ✓        | Returns AL source location from `LastStatementHit`      |
| `scopes`           | ✓        | Returns "Locals" scope                                  |
| `variables`        | ✓        | Returns values from `ValueCapture`                      |
| `continue`         | ✓        | Releases `BreakpointManager`                            |
| `next`/`stepIn`/`stepOut` | Partial | Currently treated as `continue` (step-over not yet implemented) |
| `disconnect`       | ✓        | Cancels and unblocks                                    |
| `evaluate`         | ✗        | Not supported (expression evaluation requires interpreter) |

## Usage

```bash
# Start al-runner in DAP mode
al-runner --dap 4711 ./src ./test

# VS Code launch.json (vscode-al or generic DAP extension)
{
    "type": "al",
    "request": "attach",
    "name": "Attach to al-runner",
    "port": 4711,
    "hostname": "127.0.0.1"
}
```

Or with any generic DAP client (e.g. netcoredbg client mode):
```bash
# Example: headless DAP client for CI verification
nc 127.0.0.1 4711
```

## Known Limitations

- **Step-over/step-into**: Not yet implemented. `next`, `stepIn`, `stepOut` all behave as `continue`.
- **Variable state is post-assignment**: Variables are captured after assignment, not at the exact paused statement. The value shown is the most recent assignment for each variable in scope.
- **Single thread only**: Only one thread can be paused at a time. This matches al-runner's single-threaded test executor.
- **No expression evaluation**: `evaluate` is not supported. The debugger cannot compute expressions.
- **No conditional breakpoints**: All breakpoints are unconditional.
- **AL source files must be named uniquely**: `FindStatementsForAlLine` matches by filename (case-insensitive). Duplicate filenames in different directories may map to wrong scopes.

## Future Work

- Step-over/step-into: requires tracking "next statement in same scope" vs "step into sub-scope"
- Conditional breakpoints: evaluate a condition string before pausing
- Hot-reload: re-register breakpoints when source files change in server mode
- VS Code extension: a dedicated `vscode-al-runner` extension with launch configuration support
