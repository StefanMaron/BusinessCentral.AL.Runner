# Server mode (`--server`)

`al-runner --server` is a long-running JSON-RPC daemon over stdin/stdout. It loads
the BC runtime patches and the dependency symbol set **once**, then serves many
test runs in the same warm process — turning a ~19 s cold run into ~4 s per
request. The VS Code extension depends on this flag. `runTests` streams the
protocol-v2 NDJSON shape (`protocol-v2.schema.json`, see #1641); every other
command (`execute`, `shutdown`, errors) is a single response line.

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
  "command": "runTests",        // runTests | execute | shutdown (case-insensitive)
  "sourcePaths": ["/path/app"], // bundle dir(s); ALL are run and aggregated —
                                 // e.g. an app + its separate test app, same
                                 // shape as `al-runner MyApp MyApp.Test` on
                                 // the CLI (inter-bundle deps get wired first)
  "packagePaths": ["/extra"],   // optional: extra .app caches, augment server defaults
  "stubPaths": [],              // v1 field, ignored in v2 (no stubs layer)
  "code": "...",                // execute only (inline AL) — not yet supported
  "captureValues": false,       // execute only — not yet supported
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
- `cancelled` (on the summary) is defined by `protocol-v2.schema.json` but not
  populated yet — it lands with the `cancel` command, a separate follow-up
  slice of #1641. `capturedValues` and `coverage` need the Cecil instrumentation
  pass tracked on #1640.

A request-level problem (e.g. a missing `sourcePaths`) returns the usual single
`{"error":"..."}` line instead of a `test`/`summary` sequence — see Errors below.

### `execute`

Runs each bundle's first `OnRun`-bearing codeunit (run-mode). Unlike `runTests`
this is **not** streamed — one v1-shaped response line, no `type` discriminator:

```json
{"exitCode":0,"tests":[{"name":"Codeunit60110.OnRun","status":"pass","durationMs":7}]}
```

v1's `execute` also accepted an inline `code` string and a `captureValues` flag.
v2 has no inline-AL compile path and no value capture (the latter needs the
Cecil instrumentation pass on #1640), so both fail loudly with a structured
error rather than a silent fake, per `.claude/rules/loud-failures.md`:

```json
{"error":"execute: inline AL 'code' is not yet supported in v2 — pass 'sourcePaths' to run the bundle's OnRun codeunit. See docs/server-mode.md."}
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
