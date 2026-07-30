# Server mode (`--server`)

`al-runner --server` is a long-running JSON-RPC daemon over stdin/stdout. It loads
the BC runtime patches and the dependency symbol set **once**, then serves many
test runs in the same warm process — turning a ~19 s cold run into ~4 s per
request. The VS Code extension depends on this flag; the protocol below is kept
byte-compatible with the v1 server so the existing extension keeps working.

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
  "sourcePaths": ["/path/app"], // bundle dir(s); uses the first
  "packagePaths": ["/extra"],   // optional: extra .app caches, augment server defaults
  "stubPaths": [],              // v1 field, ignored in v2 (no stubs layer)
  "code": "...",                // execute only (inline AL) — not yet supported
  "captureValues": false        // execute only — not yet supported
}
```

## Responses

### `runTests`

```jsonc
{
  "tests": [
    { "name": "Codeunit60110.MyTest", "status": "pass",   // pass | fail | error
      "durationMs": 12, "message": null, "stackTrace": null }
  ],
  "passed": 1, "failed": 0, "errors": 0, "total": 1,
  "exitCode": 0,                          // 0 ok · 1 test fail · 2 exec · 3 compile
  "compilationErrors": null,              // or [{ "file": "...", "errors": [...] }]
  "cached": false,                        // true = served from the AL-output cache
  "changedFiles": ["XRecProbe.Table.al"] // miss only: files changed vs the prior request
}
```

`stackTrace` is the AL call stack for AL-originated errors, falling back to the
raw C# exception for runner-internal failures (matching the normal-mode rule).

### `execute`

Not yet implemented in v2. Returns a structured error rather than a silent
fake (per `.claude/rules/loud-failures.md`):

```json
{"error":"execute: inline AL execution / run-mode is not yet implemented in v2 — use 'runTests'. See docs/server-mode.md."}
```

v1's `execute` ran inline AL or the first codeunit's `OnRun`. v2 has no inline-AL
execution / run-mode pipeline yet; this is tracked as a follow-up.

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
