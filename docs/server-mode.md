# Server mode (`--server`)

`al-runner --server` is a long-running JSON-RPC daemon over stdin/stdout. It loads
the BC runtime patches and the dependency symbol set **once**, then serves many
test runs in the same warm process — turning a ~19 s cold run into ~4 s per
request. The VS Code extension depends on this flag. `runTests` streams the
protocol-v2 NDJSON shape (`protocol-v2.schema.json`, see #1641); `cancel` is a
side channel that can interrupt a `runTests` in progress (see below); every
other command (`execute`, `shutdown`, errors) is a single response line.

```
al-runner --server [--package-cache PATH ...] [--cache DIR]
```

## Transport

- **Newline-delimited JSON.** One JSON object per line. stdin = requests,
  stdout = responses.
- **stdout carries ONLY the protocol.** All banners, `[cache]` lines and BC patch
  logs are redirected to **stderr**. The very first line on stdout is the
  readiness signal:

  ```json
  {"ready":true}
  ```

  Wait for it before sending the first request. (On a cold start the runner may
  re-exec itself once for a clean Cecil load; the child inherits the same stdio,
  so the readiness line still arrives on the same pipe — just later.)

## Requests

```jsonc
{
  "command": "runTests",        // runTests | execute | cancel | shutdown (case-insensitive)
  "sourcePaths": ["/path/app"], // bundle dir(s); ALL are run and aggregated —
                                 // e.g. an app + its separate test app, same
                                 // shape as `al-runner MyApp MyApp.Test` on
                                 // the CLI (inter-bundle deps get wired first)
  "packagePaths": ["/extra"],   // optional: extra .app caches, augment server defaults
  "stubPaths": [],              // v1 field, ignored in v2 (no stubs layer)
  "code": "...",                // execute only (inline AL); mutually exclusive with sourcePaths
  "captureValues": false,       // execute only — see #1640
  "testIsolation": "codeunit"   // optional: "codeunit" (default) | "test"/"method" | "disabled"
                                 // — see #1616. Applies to this request only; a later
                                 // request that omits the field falls back to the
                                 // server's own startup default, not the previous
                                 // request's value.
}
```

## Responses

### `runTests` — streaming (protocol-v2)

Unlike every other command, `runTests` is **not** a single response line. It
emits zero or more `test` lines — one per completed test, in the order each
test finishes, flushed immediately so a client sees results as they happen
instead of waiting for the whole bundle — followed by exactly one terminal
`summary` line. A request naming multiple `sourcePaths` runs them in order and
streams `test` lines across all of them before the one final `summary`.

```jsonc
{"type":"test","name":"Codeunit60110.MyTest","status":"pass","durationMs":12}
{"type":"test","name":"Codeunit60110.OtherTest","status":"fail","durationMs":3,
 "message":"NavNCLDialogException: boom","errorKind":"runtime",
 "stackFrames":[{"name":"\"My Test CU\"(CodeUnit 60110).OtherTest","line":2,
                 "presentationHint":"normal"}],
 "stackTrace":"..."}
{"type":"summary","exitCode":1,"passed":1,"failed":1,"errors":0,"total":2,
 "cached":false,"changedFiles":["XRecProbe.Table.al"],"compilationErrors":null,
 "protocolVersion":2}
```

- `status` is `pass` | `fail` | `error` | `skipped`.
- `stackTrace` is the AL call stack for AL-originated errors, falling back to
  the raw C# exception for runner-internal failures (matching the normal-mode
  rule). `message`/`stackTrace` are omitted (not `null`) on a passing test.
- `errorKind` buckets the failure so a client can vary its UI:
  `runtime` · `setup` (thrown before any `[Test]` body ran — codeunit
  instantiation) · `timeout` (the per-test `--test-timeout` guard fired) ·
  `compile` · `assertion` · `unknown` (an error with no exception behind it).
  Omitted entirely on a `pass`/`skipped` — there is no error to bucket.
  Note: BC's `Assert` codeunits ultimately call AL `Error()`, which surfaces as
  the same `NavNCLDialogException` as any other AL error, so assertion failures
  currently report `runtime`; see the note in `AlRunner/ErrorClassifier.cs`.
- `stackFrames` is `stackTrace` parsed into structured frames — same order
  (deepest first), one object per frame, with `name`, and `line` when BC
  supplied one. Omitted when no AL call stack was captured (a runner-internal
  failure); never emitted as an empty array, and `source`/`column` are omitted
  rather than invented, since BC's call-stack format carries no file path.
- `exitCode`: `0` ok · `1` test fail · `2` exec · `3` compile (same ladder as
  normal mode).
- `changedFiles` is only present on a cache miss (a hit means nothing changed);
  `compilationErrors` is only present when non-empty.
- `cached: true` means the AL-output compile was skipped (assembly served from
  the on-disk cache) — the tests still ran for real and still streamed.
- A bundle that fails to compile short-circuits straight to the `summary` line
  with `exitCode: 3` and `compilationErrors` set — no `test` lines for that
  bundle (there was nothing to run).
- `cancelled: true` is present on the summary only when a concurrent `cancel`
  command actually stopped the run before every test ran (see `cancel` below);
  omitted otherwise (never emitted as `false`). `capturedValues` and `coverage`
  still need the Cecil instrumentation pass tracked on #1640.

A request-level problem (e.g. a missing `sourcePaths`) returns the usual single
`{"error":"..."}` line instead of a `test`/`summary` sequence — see Errors below.

### `cancel` — side channel, works mid-stream

```json
{"command":"cancel"}
```

```json
{"type":"ack","command":"cancel","noop":false}
```

Cooperative cancellation for an in-flight `runTests` request. Unlike every
other command, `cancel` is answered **immediately**, even while `runTests` is
still streaming `test` lines — a dedicated stdin-reader thread recognises it
the instant it is read, independent of the normal one-request-at-a-time
dispatch loop. That is the whole point: cancellation is only useful if the
signal can reach the runner mid-run rather than being queued behind it.

- `noop: false` — a run was active and this cancel signalled it. The already-
  running test still finishes (a test body is never interrupted mid-flight);
  the *next* test does not start. The terminating `summary` line for that run
  carries `cancelled: true`.
- `noop: true` — nothing to cancel: no `runTests` request was active, the
  active one had already finished, or an earlier `cancel` already signalled it.
  Sending `cancel` with no run in flight is a well-defined no-op, not an error.
- `cancel` accepts and ignores any extra fields on the request object
  (forward-compatible with future protocol additions).
- There is at most one active run at a time; `cancel` has no request/run id to
  target — it always addresses whichever `runTests` request is currently
  streaming, matching v1's shape (#1613/#1614).

### `execute`

Runs each bundle's first `OnRun`-bearing codeunit (run-mode). Unlike `runTests`
this is **not** streamed — one v1-shaped response line, no `type` discriminator:

```json
{"exitCode":0,"tests":[{"name":"Codeunit60110.OnRun","status":"pass","durationMs":7}]}
```

v1's `execute` also accepted an inline `code` string and a `captureValues` flag.
v2 now supports `code` too (#1917): a temp single-file bundle is synthesised
from it and run through the same compile pipeline `sourcePaths` uses. `code`
that already parses as a full AL object declaration — any object keyword
(`table`, `codeunit`, `page`, `enum`, `report`, `query`, `xmlport`,
`interface`, ... the whole AL object-keyword set, including behind a leading
`//` comment) — is used verbatim; anything else is treated as a bare statement
list and wrapped in a scratch codeunit's `OnRun` trigger body — matching v1's
CLI `-e` shape. Classification asks BC's own parser
(`SyntaxTree.ParseObjectText`) rather than matching a keyword prefix, so it
covers every object type BC supports, not just `codeunit`/`table` (#1931):

```json
{"command":"execute","code":"Message('hi');"}
```

```json
{"exitCode":0,"tests":[{"name":"AL Runner Inline Execute.OnRun","status":"pass","durationMs":4}]}
```

`code` and `sourcePaths` are mutually exclusive — sending both is a
request-level error.

`captureValues: true` (#1640) reports each test entry's AL locals as of the
last statement that ran — captured via a Cecil hook on
`NavMethodScope.Exit()`, not a pass over emitted AL output (see
`AlRunner/Infrastructure/AlValueCapture.cs`). `capturedValues` is present per
test only when the request set the flag; each entry is `{scopeName,
variableName, value, statementId}`:

```json
{"command":"execute","captureValues":true,
 "code":"codeunit 50100 X { trigger OnRun() var Msg: Text; begin Msg := 'hi'; end; }"}
```

```json
{"exitCode":0,"tests":[{"name":"X.OnRun","status":"pass","durationMs":5,
 "capturedValues":[{"scopeName":"OnRun","variableName":"Msg","value":"hi","statementId":0}]}]}
```

### `shutdown`

```json
{"status":"shutting down"}
```

The server writes this response, then exits. EOF on stdin also exits.

### Errors

Any request-level problem returns `{"error":"<message>"}` and the server keeps
running.

## The reload contract (same-bundle, in-process)

The server's value is staying warm across **edits**. .NET cannot unload an
assembly, so a re-emitted bundle is a *new* assembly loaded alongside the old one
(both under the same module name `V2_<bundle>`). Before each `runTests`, the
server calls `BcRuntime.ResetForNewBundleReload()`, which:

- drops every bundle-derived cache: record/codeunit/page/report/query/xmlport CLR
  type caches, the NCLMetaTable/metaForm/etc. caches, parsed table/extension
  schemas, the registered source dirs, the AL enum registry, and the **in-memory
  table rows** (so an edited re-run starts clean instead of seeing the previous
  run's Inserts);
- preserves the installed hooks and resolved runtime reflection handles.

AL-output type finders (`FindRecordType`, the codeunit/event finders) then prefer
`BcRuntime.CurrentTestAssembly`, and stale previous-bundle assemblies are skipped
(`BcRuntime.IsStaleBundleAssembly`), so the freshly-emitted types win over the
same-named types still loaded from the previous run.

### Covered: code / logic edits

Edits to triggers and procedure/codeunit bodies are picked up fully — the new
compiled IL runs because the CLR type is resolved fresh against the new assembly.

### Known limitation: field / table **shape** edits

The runner does **not** clear BC's own skeleton `NCLMetadata.metadataCacheEntries`
on reload (it also holds dependency BC-table metadata, and clearing it wholesale
is risky). That cache keeps the **field set** of a table from the first time it was
seen. So adding/removing/retyping a *field* (or other table-shape change) is not
reliably picked up by a warm reload — restart the server after a schema change.
Trigger/logic edits within an unchanged field layout are fine.

## Exit codes

Same ladder as normal mode: `0` all pass · `1` test failures · `2` execution
error · `3` compilation error. In server mode the code rides on each `runTests`
response's `exitCode`; the process itself exits `0` on `shutdown`/EOF.
