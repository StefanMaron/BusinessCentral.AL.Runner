# Server mode (`--server`)

`al-runner --server` is a long-running JSON-RPC daemon over stdin/stdout. It loads
the BC runtime patches and the dependency symbol set **once**, then serves many
test runs in the same warm process — turning a ~19 s cold run into ~4 s per
request. The VS Code extension depends on this flag. `runTests` streams the
protocol-v2 NDJSON shape (`protocol-v2.schema.json`, see #1641); `cancel` is a
side channel that can interrupt a `runTests` in progress (see below); every
other command (`execute`, `shutdown`, errors) is a single response line.

```
al-runner --server [--package-cache PATH ...] [--cache DIR] [--define SYM ...] [--preprocessor-symbols A,B,...]
```

Preprocessor symbols are **daemon-wide**: `--define` / `--preprocessor-symbols` at start
select the build every request compiles, and a request cannot change them (#4952).

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
  "coverage": false,            // runTests + execute — per-statement hit counts + position table, see #2042
  "perTestCoverage": false,     // runTests + execute — per-test statement attribution, see #2135
  "affectedOnly": false,        // runTests: select only tests affected by object changes since the previous run (#2441)
  "includeFailing": false,      // with affectedOnly: rerun every test that did not pass last time, whatever changed (#4978)
  "testIsolation": "codeunit"   // optional: "codeunit" (default) | "test"/"method" | "disabled"
                                 // — see #1616. Applies to this request only; a later
                                 // request that omits the field falls back to the
                                 // server's own startup default, not the previous
                                 // request's value.
}
```

### Request fields

Field names are case-sensitive. What happens to a field depends on the command (#4952):

| the field | `runTests` / `execute` | `cancel` / `shutdown` |
|---|---|---|
| read by this command | used | — |
| declared above, but read only by the other command, or by none (`stubPaths`) | the request runs; the response carries `warnings: ["'<field>' has no effect on …"]` — unless the value asks for nothing (`false`, `[]`, `""`, `null`) | ignored |
| not declared above (`preprocessorSymbols`, `testFilter`, `SourcePaths`, …) | **refused** with one `{"error": …}` line naming the field; nothing runs | ignored |

`runTests` reads `sourcePaths`, `packagePaths`, `coverage`, `perTestCoverage`, `affectedOnly`,
`includeFailing` and `testIsolation`. `execute` reads `sourcePaths`, `code`, `packagePaths`,
`captureValues`, `iterationTracking`, `coverage`, `perTestCoverage`, `affectedOnly` and
`testIsolation`.

`warnings` sits on the `runTests` summary line and on the `execute` response, and is omitted
when empty. Preprocessor symbols have no request field: start the server with `--define SYM` or
`--preprocessor-symbols A,B`.

## Responses

### `runTests` — streaming (protocol-v2)

Unlike every other command, `runTests` is **not** a single response line. It
emits zero or more `test` lines — one per completed test, in the order each
test finishes, flushed immediately so a client sees results as they happen
instead of waiting for the whole bundle — followed by exactly one terminal
`summary` line. A request naming multiple `sourcePaths` runs all of them and
streams `test` lines across all of them before the one final `summary`.

Bundles execute in **dependency order**, not the order they were listed: if one
bundle in the request declares a dependency on another, the dependency runs
first (#2614). Without that, a dependency listed last was compiled against —
the pre-pass publishes its new symbols before any bundle runs — but its runtime
assembly was not reloaded until after its consumer's tests had already run, so
the consumer dispatched freshly resolved member ids into the previous request's
assembly and the request failed where a cold run of the same sources passed.

A request whose listed order already satisfies its dependencies is unchanged,
and bundles with no dependency between them are not deliberately reordered — but
a bundle waiting on a dependency is emitted when that dependency lands, which
can put an unrelated bundle listed after it ahead of it. **Do not depend on
`test` lines arriving in `sourcePaths` order.** The `summary` totals and the
per-bundle results are unaffected: results are returned in the order the caller
listed the paths.

A request with more than one bundle compiles and loads **every** bundle before any
of them runs tests (#4850), the way a CLI run over one root, or over several paths
(#4931), loads every app group first. So an event raised by an earlier bundle's test reaches a later bundle's
subscriber, as it does on a tenant where every app is installed. Only the bundles
the request names count as installed: a module an earlier request loaded does not
answer an event in a request that omits it. A single-bundle request is unchanged.

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
  omitted otherwise (never emitted as `false`).
- `coverage` (#2042) is present on the summary only when the request set
  `coverage: true` — see "Per-statement hit counts (`coverage`)" below.
- `selection` (#2441) is present on the summary when the request set
  `affectedOnly: true`: `{mode:"affected", ran, skipped, skippedFailing,
  changedObjects[], forcedFull, reason|null}`.
  - `skippedFailing` counts the skipped tests whose last recorded result was not a
    pass (#4978). A client that keeps each test's previous result should keep
    showing those as failed; `0` on a forced-full run and with `includeFailing: true`.
  - `forcedFull:false` means the run used affected-only selection and `ran + skipped`
    equals the discovered tests for this request.
  - `forcedFull:true` means the runner deliberately ran everything and says why in
    `reason` (fail-safe policy: run too many, never too few).

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

Runs one `OnRun`-bearing codeunit per bundle (run-mode). Unlike `runTests`
this is **not** streamed — one v1-shaped response line, no `type` discriminator:

```json
{"exitCode":0,"tests":[{"name":"Codeunit60110.OnRun","status":"pass","durationMs":7}]}
```

**Which codeunit**, when a bundle holds more than one with an `OnRun` trigger: the
one with the **lowest AL object id**, preferring a codeunit that declares no `[Test]`
procedure. A `Subtype = Test` codeunit is only ever selected when the bundle has
nothing else with an `OnRun`.

This used to read "the first `OnRun`-bearing codeunit", and "first" meant first in
`Assembly.GetTypes()` — an array the CLR gives no defined order for, so two `execute`
requests holding the same codeunits could run different code (#3086). It is the same
rule test codeunits run under (#2801). Single-codeunit requests, which is nearly all
of them, are unaffected either way.

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

`captureValues: true` (#1640) reports ONE ENTRY PER STATEMENT EXECUTION that
changed a top-level AL local's value, in execution order — not a single
end-of-test snapshot (#2074) — captured via Cecil hooks on
`NavMethodScope.StmtHit(int)` (every intermediate execution) and
`NavMethodScope.Exit()` (the final one), not a pass over emitted AL output
(see `AlRunner/Infrastructure/AlValueCapture.cs`). `capturedValues` is present
per test only when the request set the flag; each entry is `{scopeName,
variableName, value, statementId, captureError|omitted}`:

```json
{"command":"execute","captureValues":true,
 "code":"codeunit 50100 X { trigger OnRun() var Msg: Text; begin Msg := 'hi'; end; }"}
```

```json
{"exitCode":0,"tests":[{"name":"X.OnRun","status":"pass","durationMs":5,
 "capturedValues":[{"scopeName":"OnRun","variableName":"Msg","value":"hi","statementId":0}]}]}
```

A local reassigned N times (e.g. inside a loop) produces N entries sharing
that assignment statement's `statementId`, each carrying the value that
execution actually produced — never collapsed to just the final one. A caller
that only wants "the final value" reads the LAST entry for a given
`variableName`. A local that is declared but never assigned produces NO entry
at all — nothing executed a value into it, so there is no execution to
report. The next example shows the SAME variable assigned twice on two
different statements — TWO entries, not one:

```json
{"command":"execute","captureValues":true,
 "code":"codeunit 50101 X2 { trigger OnRun() var Msg: Text; begin Msg := 'hi'; Msg := 'bye'; end; }"}
```

```json
{"exitCode":0,"tests":[{"name":"X2.OnRun","status":"pass","durationMs":1,
 "capturedValues":[
   {"scopeName":"OnRun","variableName":"Msg","value":"hi","statementId":0},
   {"scopeName":"OnRun","variableName":"Msg","value":"bye","statementId":1}]}]}
```

`captureError` (issue #2043) is present, non-null, only when the runtime could
not faithfully read or render this variable — either the reflective field
read itself threw, or the raw value's own `ToString()` threw. It names the
exception type (e.g. `"field read threw NotSupportedException"`). `value` is
`null` in that case, but this must not be confused with a genuinely null AL
variable: a genuinely null variable is reported with `value:null` and
`captureError` **absent**. A variable whose read failed is never simply
omitted from the array — that would be indistinguishable from "this variable
does not exist" (`.claude/rules/loud-failures.md`).

#### One record per execution, not per change

Since #2056 the series is **full fidelity**: a local gets a record every time a
statement that assigns it has executed, whether or not the value changed, and
every time its value changed for any other reason. `x := 5; x := 5;` is two
records; `for i := 1 to 3 do x := 5;` is three more. This is the contract agreed
on SShadowS/ALchemist#1: a consumer answering "what was `x` at iteration 7"
cannot reconstruct that from a change-only series.

The "assigns it" half is best-effort write inference from the bundle's own
syntax (one parse per request, same preprocessor symbols as the compile): the
target of `:=` and `+=` (the ROOT local for `Rec.Amount :=` and `arr[1] :=`),
the receiver of a method-call statement (`l.Add(5);`, `Rec.Insert();`, assumed
to mutate it), the first argument of `Clear(x);` and `Evaluate(x, s);`, the
`var` arguments of a call to a procedure of the same object (`Fill(x);`), and a
`for`/`foreach` loop variable at the start of every pass. What syntax cannot
see is left to the value diff alone: a receiver mutated inside an expression
(`n := Rec.Next();`), a call into another object, a global. Those still report
every real change; only a same-value re-assignment through one of them goes
unreported. A local neither assigned nor changed still gets nothing, so an
untouched local never appears.

Two more properties of the series, both since #2056:

- **Every scope is diffed against itself.** A procedure called from the
  run's `OnRun`, or a table trigger it fires, has its own capture state: its
  locals are compared with their own previous observation (never with a
  same-named local of the caller), its first observation is its own baseline,
  and its records carry its own statement ids. Recursion gives each activation
  its own state too. A record variable's value is its primary key.
- **A `for` variable is attributed to the `for` statement.** BC assigns a
  `for` variable before the loop statement's own hit, so at that hit the loop
  variable is read and recorded with the `for` statement's id, whatever its
  value (a leading `for i := 0 to 2` records `i = 0`); everything else observed
  there is the previous statement's effect. A `foreach` assigns its element
  after the header hit, so its variable is recorded at the first body hit,
  attributed to the `foreach` statement. A parameter's value at entry was
  produced by no statement and is never a record. One thing the observation
  model cannot separate: `i := 1; for i := 1 to 3` is seen once and attributed
  to the `for`.

### Per-statement hit counts (`coverage`)

`coverage: true` (#2042) opts into a per-statement hit-count + position table on
**both** `runTests`' terminal `summary` line and `execute`'s response — reusing
`--coverage`'s existing `StmtHit`/`CStmtHit` hook (#1922), no new instrumentation.
`coverage` is an array with one entry per AL source file; each file's
`statements` array has one entry per BC-instrumented statement:

```json
{"command":"execute","captureValues":true,"coverage":true,
 "code":"codeunit 50100 X { trigger OnRun() var Msg: Text; begin Msg := 'hi'; Msg := 'bye'; end; }"}
```

```json
{"exitCode":0,"tests":[{"name":"X.OnRun","status":"pass","durationMs":7,
 "capturedValues":[
   {"scopeName":"OnRun","variableName":"Msg","value":"hi","statementId":0},
   {"scopeName":"OnRun","variableName":"Msg","value":"bye","statementId":1}]}],
 "coverage":[{"file":"/tmp/.../Scratch.al","statements":[
   {"id":0,"scope":"OnRun","line":1,"column":57,"endLine":1,"endColumn":69,"hits":1},
   {"id":1,"scope":"OnRun","line":1,"column":70,"endLine":1,"endColumn":83,"hits":1}]}]}
```

- **`id` is the SAME id-space as `capturedValues[].statementId` for the SAME
  `scope`.** This is the feature's actual point: `--capture-values` (#2040)
  emits a `statementId` with no reliable way to place it in an editor short of
  treating it as an index into a sorted covered-lines list — a heuristic that
  breaks on multi-statement lines and skipped statements (see the upstream
  request, SShadowS/ALchemist#1). `coverage[].statements[].id` resolves it
  exactly: look up the entry whose `scope` matches `capturedValues[].scopeName`
  and whose `id` matches `capturedValues[].statementId`, and its `line`/`column`
  is the real AL source position that value was captured at.
- **Per statement, never per line.** Two statements sharing a source line are
  two separate entries with their own `hits`, not one summed count — the
  distinction a plain line-coverage rollup necessarily discards. A line rollup
  is still trivial to derive client-side (group `statements` by `line`, sum
  `hits`); the reverse is not.
- `line`/`column`/`endLine`/`endColumn` are 1-based, decoded from BC's own
  `[SourceSpans]` attribute (`AlSourceSpanCodec`) — the same source
  `--coverage`'s Cobertura output and `--capture-values`' `statementId` both
  already read.
- `hits` is the number of times that exact statement executed in **this**
  request — not accumulated across the server process's lifetime. A statement
  hit 0 times (an untaken branch) is still listed, not omitted — "did not
  execute" is a real, distinct answer from "not part of this scope".
- Supersedes protocol-v2.schema.json's older, never-implemented
  `FileCoverage{file, lines[], totalStatements, hitStatements}` shape — that
  shape was schema-only (this repo never shipped a working `coverage` producer
  for it), so there is no compatibility break. Per-statement detail with
  positions strictly subsumes a line-hit rollup.
- `coverage` is omitted entirely (not an empty array) when the request didn't
  set `coverage: true`; a non-null but empty `coverage: []` means "asked,
  nothing was instrumented" (e.g. a compile failure before any AL code ran) —
  the same "requested vs found nothing" distinction `capturedValues` already
  makes.

#### Coverage path shape

**`coverage[].file` is an absolute, forward-slashed path, whatever the request's
`sourcePaths` looked like.** The same holds for every other path this source map
feeds: `loops[].file` (`iterationTracking`) and DAP `source.path` on stack frames.
Forward slashes on every platform, so a consumer need not normalise separators.

A consumer may therefore key on the string, and two requests naming one tree the
same way get one spelling for it. That was not true before #4344: the map rendered
each file from the spelling of the root it was reached through, and the roots come
from two places that disagree —

| root | spelling | why |
|---|---|---|
| the request's own `sourcePaths` | **as the caller wrote it** | `RootsWithParsedSourceDependencies` canonicalises only its dedup key |
| a sibling source dependency, from the registry the compile populated | **absolute** | `Program.cs` registers folders computed from a `GetFullPath`'d bundle root |

So a request passing a *relative* `sourcePaths` entry got a document mixing the
two — the caller's relative spelling for its own bundle, an absolute path for the
dependency — while an absolute `sourcePaths` got a uniform one. Nothing was
ambiguous (both resolve against the server's working directory), but a tool keying
on the filename string saw two coordinate systems in one document, decided by
something that says nothing about the run.

**What changed for a consumer:** a caller who passed a relative `sourcePaths`
entry, and read back a path relative to the server's working directory, now reads
an absolute one. A caller who passed absolute `sourcePaths` sees no change — that
case already produced absolute paths throughout, which is the shape this section
pins.

**The CLI's Cobertura output is a different contract and is unchanged.** `--coverage`
writes `filename` attributes *relative to the working directory*, because the
document's own `<source>.</source>` element is the base they are read against
(`AlCoverageReport.WriteCobertura`, #3120). Only the server and DAP paths, which
have no such base element, are absolute.

**Changing this shape means auditing what JOINS on the path, not only what prints
it.** The map's path is also a lookup key inside the runner, and a rendering change
is invisible to a consumer that merely displays it while breaking one that matches
on it — the second returns null, and "no loops for that scope" reads exactly like
"that scope has no loops". Two internal consumers key on it today, and both
canonicalise so that either spelling resolves:

| consumer | keyed through |
|---|---|
| `AlMemberSyntaxIndex` — `iterationTracking` loops, `captureValues` write sets | `NormalizePath` (absolute + forward-slashed), applied on the insert side *and* the lookup side |
| `DapBreakpointResolver` — breakpoint → statement | `Path.GetFullPath` on both the map's paths and the client's request |

`Program.cs`'s `execute` handler is where this bites: it feeds **one** `sourcePaths`
into `AlCoverageSourceMap.Build` and `AlMemberSyntaxIndex.Build` and joins their
results, so the two must agree on a coordinate system or every lookup misses.

### Loop iterations (`iterationTracking`)

`iterationTracking: true` (#2056, `execute` only) opts into per-iteration
segmentation of every loop the run entered. It is what a consumer needs to
answer "which iteration produced this value" and to step through iterations
(SShadowS/ALchemist#1). Each test result gains a `loops` array, one entry per
**loop instance** (a `for` inside a procedure called three times appears three
times, each with its own id and parent). The values and messages a pass
produced are NOT copied into it: they stay in the flat `capturedValues` and
top-level `messages`, each record tagged with the `loop` id and `iteration`
index it belongs to. A loop's `iterations[]` carry only what the flat series
cannot, the statements and lines that ran. A consumer builds an iteration's
view by filtering the flat series on `loop` and `iteration`.

```json
{"command":"execute","captureValues":true,"iterationTracking":true,
 "code":"codeunit 50102 X3 { trigger OnRun() var i: Integer; total: Integer; begin total := 0; for i := 1 to 3 do begin total := total + i; Message(Format(total)); end; end; }"}
```

```json
{"exitCode":0,"tests":[{"name":"Codeunit50102.OnRun","status":"pass","durationMs":24,
 "capturedValues":[
   {"scopeName":"OnRun","variableName":"total","value":0,"statementId":0},
   {"scopeName":"OnRun","variableName":"i","value":1,"statementId":1,"loop":0,"iteration":1},
   {"scopeName":"OnRun","variableName":"total","value":1,"statementId":2,"loop":0,"iteration":1},
   {"scopeName":"OnRun","variableName":"i","value":2,"statementId":3,"loop":0,"iteration":2},
   {"scopeName":"OnRun","variableName":"total","value":3,"statementId":2,"loop":0,"iteration":2},
   ... i=3, total=6 tagged loop 0 iteration 3 ...],
 "loops":[{
   "id":0,"scope":"OnRun","file":"/tmp/.../Scratch.al",
   "line":1,"column":87,"endLine":1,"endColumn":160,
   "iterationCount":3,"closedBy":"exit",
   "iterations":[
     {"index":1,"statements":[2,3],"lines":[1]},
     {"index":2,"statements":[2,3],"lines":[1]},
     {"index":3,"statements":[2,3],"lines":[1]}]}]}],
 "messages":[
   {"text":"1","scopeName":"OnRun","statementId":3,"loop":0,"iteration":1},
   {"text":"3","scopeName":"OnRun","statementId":3,"loop":0,"iteration":2},
   {"text":"6","scopeName":"OnRun","statementId":3,"loop":0,"iteration":3}]}
```

- **Tags, not copies.** A `capturedValues` or `messages` record carries `loop`
  (a loop id) and `iteration` (a 1-based index) when it was produced inside a
  loop, both omitted when it was not (`total := 0` above, and a `Message()`
  after the loop). Filtering the flat series by a loop id and iteration index
  reconstructs exactly that iteration's values and messages, in order. The full-
  fidelity rule still holds: a local a pass assigned is tagged to it with its
  value, changed or not; one it neither assigned nor changed is not (see "One
  record per execution" above).
- **Where a value lands.** `--capture-values` observes a statement's effect at
  the NEXT statement's hit, so the last statement of one pass is observed at the
  first hit of the next. Values are tagged with the pass that produced them; a
  `for`/`foreach` loop variable's new value (the one thing that runs between two
  passes with no hit of its own) with the pass it opens. Whatever a
  `while`/`until` condition produces (`Rec.Next()`, a `Message()`, a loop in a
  procedure it calls) is tagged with the pass that condition opens, or with the
  last pass when it ends the loop. A `repeat` has no hit before its first pass,
  so pass 1 opens with the state the loop entered with: after
  `if Rec.FindSet() then repeat`, `Rec` on its first row is tagged to pass 1.
- **Ids are integers, unique per response.** `id`, `parentLoop` and a record's
  `loop` are integers, distinct across the whole response so a top-level message
  tag is unambiguous even across multiple bundles.
- **`iterations[].statements`** are the AL statement ids that ran in that pass,
  the id-space `coverage[].statements[].id` uses for the loop's `scope`;
  **`iterations[].lines`** their 1-based lines. Both are the loop's own scope
  only, nested loops' lines included (a called procedure's statements belong to
  that procedure's own loop instances). A `while` condition is counted in the
  pass it ended; the final, false evaluation in the last pass.
- **Nesting is dynamic, not lexical.** `parentLoop`/`parentIteration` name the
  loop instance and iteration that were *active when this instance was
  entered*, across procedure calls: a loop inside a procedure called from
  iteration 2 of an outer loop reports `parentLoop` = that outer instance's id
  and `parentIteration: 2`. A loop entered while the parent's condition was
  being evaluated belongs to the pass that evaluation opens, or to the last pass
  when it ends the parent. `parentLoop` is omitted for a loop with no enclosing
  active loop; `parentIteration` is omitted when the parent had no pass yet.
  Instances are listed in entry order. A loop in a table field trigger is listed
  under BC's scope name for it, `Number - OnValidate`, and its `capturedValues`
  are the trigger's own locals.
- **`line`/`column`/`endLine`/`endColumn`** are the loop statement's 1-based
  source range; `file` is the same path identity `coverage[].file` uses.
  `iterationCount` counts passes whose body began, so a `for i := 1 to 0`
  reports `iterationCount: 0` with no iterations, and a `break`/`exit` mid-pass
  still counts that pass (its values up to the exit are tagged to it).
  **`closedBy`** says how the instance ended: `exit` (the first statement after
  the loop ran), `scopeExit` (the procedure returned or `exit`ed from inside
  it), or `unfinished` (an error unwound through it, or the run ended): a last
  pass under `unfinished` is partial.
- **Unsegmentable loops are said so, never guessed.** Some shapes give the hit
  stream nothing to count passes on; such a loop is still listed, with
  `iterationCount` omitted and `unsegmentable` set to a stable code:
  `emptyBody` (`for i := 1 to 3 do ;`); `soleNestedRepeat`, when the whole body
  is a nested `repeat` (its re-entry and its next pass both follow its
  until-condition); `soleNestedWhileWithBreak`, when the whole body is a nested
  `while` containing `break` (its re-entry can follow one of its own body
  statements, like a re-evaluation); `soleNestedUnsegmentable`, when the whole
  body is a nested loop that is itself unsegmentable. Add a statement to the
  outer body and all three segment normally.
- **`unresolvedScopes`** (on the test result, omitted when empty) lists bundle
  scopes as `scope@file` whose member could not be matched in the parsed source;
  loops in them are not tracked. Scopes outside the bundle (dependency apps) are
  never tracked and never listed.
- Independent of `captureValues` (without it there are no `capturedValues` to
  tag, but the loops, their `statements`/`lines`, and message tags are all
  still present). `loops` is omitted entirely when the request did not set
  `iterationTracking: true`; a present but empty `loops: []` means "asked,
  nothing looped".

- **How it works.** Loop *structure* comes from BC's own syntax tree (the same
  `ParseObjectText` the compiler runs, once per request, with the bundle's own
  preprocessor symbols): which statements are a loop's header, which are its
  body, and which one opens an iteration. Segmentation then rides the existing
  `StmtHit` hook `coverage`/`capturedValues` already use: BC hits a `for` once
  at entry and its body once per pass, a `while`/`until` condition once per
  evaluation. No new Cecil rewrite. See `AlRunner/Infrastructure/AlLoopModel.cs`
  for the measured instrumentation shapes and `AlIterationSegmenter.cs` for the
  state machine.
- **`capturedValues` changed alongside this.** Full-fidelity records, per-scope
  diff state and the leading-`for` baseline rule all landed with #2056; they are
  described under `execute`'s `capturedValues` above, because they apply to the
  flat series whether or not `iterationTracking` is on.

`affectedOnly: true` (runTests) narrows execution to tests affected by AL object
changes since the previous successful run of the same bundle, in this server
process or, through the persisted baseline, in an earlier one (see "affectedOnly across
server processes"). The runner:

1. keeps the previous run's per-test coverage grouped by test
   (`"{Codeunit}.{Method}"`);
2. computes changed AL objects from the incremental compiler change model
   (Added/Modified/Removed/rename-attributed object identities);
3. runs tests whose previous coverage intersects those changed objects, or that
   raised an event whose subscribers changed (see "affectedOnly and event
   subscribers"). A test's coverage is the statements it executed plus the objects it
   built and the procedures and triggers it entered, so an empty procedure or a page
   without triggers is in it too (see "affectedOnly and entered scopes");
4. always includes unknown tests (new/renamed tests, tests with no recorded
   coverage, tests that timed out or were skipped in the recording run);
5. selects a test that failed in the recording run by its recorded coverage, like
   any other test, unless the request sets `includeFailing: true` — see below;
6. widens the selection to the tests that share state with a selected one: under the
   default `TestIsolation = Codeunit` every test of a selected test's codeunit, under
   `disabled` every test of the bundle (see "affectedOnly and test isolation");
7. widens it again by session state (WorkDate, number sequences, SingleInstance
   codeunits), which no isolation resets (see "affectedOnly and session state").

When the runner cannot prove a safe object delta, it **forces a full run**
(`selection.forcedFull:true`) and sets `selection.reason`. Forced-full causes
include:

- no previous per-test coverage baseline for that bundle, in this process or on disk;
- a persisted baseline that cannot be used: unreadable, truncated, written by another
  schema version, or unable to vouch for the current source (see "affectedOnly across
  server processes");
- incremental change model fallback (for example `app.json` changes, dependency
  set changes, removed/unclassifiable files, duplicate declaration ambiguity);
- coverage recorded under a different environment (BC version/artifact,
  package-cache closure, the content of a non-Microsoft dependency package —
  see "affectedOnly and packaged dependencies" — or the test isolation, see
  "affectedOnly and test isolation");
- the change model's baseline for a module in the request is not the code the
  coverage was recorded on (see below);
- compile/dependency failures before test execution;
- a changed subscriber whose event's raises the recording run could not see (see
  "affectedOnly and event subscribers");
- a whole-object change to an object with an instance no one test built, or a changed
  `PageExtension` (see "affectedOnly and entered scopes").

#### affectedOnly and test isolation

A test's recording holds what that test ran. Under `TestIsolation = Codeunit` (the
default) the tests of one codeunit share one codeunit instance and one database state,
so a test can depend on setup an earlier test ran: the `isInitialized` pattern, where
the first test to call `Initialize()` creates the data and the others only read it. The
setup is in the first test's recording only, so a change to it selected that test and
skipped the others, which failed just the same (#5035; measured on Tests-SMB in the
#3415 spike).

So selection widens by what the isolation shares:

| isolation | a selected test also selects |
|---|---|
| `codeunit` (default) | every test of its codeunit |
| `disabled` | every test of the bundle, since nothing is reset between codeunits |
| `test` (`method`) | nothing by isolation: each test gets a fresh codeunit instance and a fresh database. Session state still carries over; see "affectedOnly and session state" |

Why the whole codeunit and not only the tests after the first selected one: the test
order is fixed (source declaration order, not the run seed), so the tests before it ran no
changed code and keep their outcome. But the tests after it read the state those earlier
tests left, and running them without that state can make them pass where a full run fails.
The earlier tests have to run too, which is the whole codeunit. That also removes an older
difference: a single selected test used to run without the tests before it.

The isolation is part of the environment key, so coverage recorded under one isolation
is never used to select under another; switching it forces one full run. What no isolation
resets (WorkDate, number sequences, SingleInstance codeunits) is covered in "affectedOnly and
session state"; a whole-object change to a SingleInstance codeunit also forces a full run (see
"affectedOnly and entered scopes").

The cost, on the al-language corpus: see the pull request that introduced this (#5035).

#### affectedOnly and session state

Some state lives in the session, not the database, so no test isolation rolls it back: a
test can read what an earlier test left in it, in the same codeunit or another one (#5050).
Measured under `test` isolation (A sets `WorkDate(20200101D)` and draws one number from a
sequence; B and C read them) and across codeunits under the default isolation (a
SingleInstance store written by one test codeunit and read by the next).

Recorded per test, as keys in the test's events entry:

| state | written by | read by |
|---|---|---|
| `WorkDate` | `WorkDate(<date>)` (Ncl `ALSystemDate.ALWorkDate(NavSession, NavDate)`) | `WorkDate()` (`ALSystemDate.ALWorkDate(NavSession)`) |
| a number sequence, by name and company scope | `Insert`, `Next`, `Range`, `Restart`, `Delete` | `Exists`, `Current`, `Next`, `Range`, `Restart`, `Insert` (whether it exists decides the error) |
| a SingleInstance codeunit, by id | any use of it: resolving a variable to it, or entering one of its procedures or triggers | the same: its globals cannot be told apart by read or write |

A read counts only when the test had not written that state itself first, so a test that
sets WorkDate and then reads it is not a reader. That is a property of the test's own code,
so a record taken in a narrowed run means the same as one taken in a full run.

When the request changed code or subscriber bindings in the bundle (or in a bundle that ran
before it) and anything is selected, the selection gains:

- every test **after the first selected one** that read session state an earlier test
  left, and
- every test **before the last selected one** that wrote session state.

When nothing changed and tests are selected anyway (unknown tests, `includeFailing`), every
test does what its record says, so a selected test brings only the earlier tests that wrote
what it read, and their writers in turn.

Why every reader and every writer, not only those of the state a selected test recorded:
the recording is of the old code. A changed test can start writing WorkDate, or start
reading a sequence it never read, and nothing recorded says so. A later reader can then
fail where it passed, and a changed test can fail for want of state another test used to
give it (a reader run alone finds no sequence). A test with no record counts as both.

Across bundles of one request: WorkDate and number sequences carry from one bundle to the
next (SingleInstance codeunits are reset per bundle), so a change in an earlier bundle (or a
full run of it) selects every reader of a later one, and a bundle followed by another runs its WorkDate and
sequence writers whatever changed.

Not recorded, so not linked (#5057): static .NET state reached through DotNet interop,
`Randomize` seeds, and the last error text. `GlobalLanguage` is not session state here: the
runner answers 1033 whatever a test sets. The persisted baseline is schema 4 from this
change, so a baseline without these keys is not used.

The cost, on the al-language corpus: see the pull request that introduced this (#5050).

#### affectedOnly and previously failing tests

A failing test's per-test coverage is the part of the test that ran before it
failed, and that part decides its outcome: a change that can make it pass must
touch code it already executed, because code after the failure point runs only
once something before it changes. So a test that failed in the recording run
keeps that coverage and is selected by it (#4978). It shares the gaps passing
tests have. A failing test's result depends on code it never ran more
often than a passing test's does: an
object added and reached by id (`Codeunit.Run(<id>)`, `RecordRef.Open`),
and install/setup code that runs outside the test. (State left by earlier tests in the
same codeunit is covered by the widening in "affectedOnly and test isolation".) Neither
is in its coverage, so a change there skips it;
`skippedFailing` says so, and `includeFailing: true` runs it.

It stays **unknown**, and always runs, when the record is not complete:

- nothing was recorded for it at all (for example it failed in setup before
  any AL statement), or a recorded statement could not be attributed to an object
  of this request;
- it timed out: the watchdog stopped it, not the code, and its body may still be
  running;
- it was skipped, or has no result (the run was cancelled before it).

`includeFailing: true` restores the earlier behavior: every test whose last
recorded result was not a pass runs again, whatever changed. Either way a failed
test that selection skips keeps its failing status until it runs again, and the
summary counts it in `selection.skippedFailing`.

#### affectedOnly and event subscribers

A subscriber is reached through the event it binds to, not through a call, so
statement coverage cannot say which tests a subscriber change affects (#4988). The
recording run therefore also records, per test, the events the test raised:

- `ev|<Kind>|<id>|<Event>` for an event a publisher declares
  (`[IntegrationEvent]`/`[BusinessEvent]` on a codeunit, table, page, report, query or
  xmlport), recorded by the event dispatcher before it looks for subscribers. A
  publisher skips its event entirely while nothing subscribes, so a recording run
  seeds every event scope of the request's modules (bundle and dependency modules the
  runner compiled or loaded, not Microsoft's Base or System Application) to make each
  raise reach the dispatcher. With no subscriber the dispatcher returns at once,
  which is what an unseeded publisher does. An event that a tableextension,
  pageextension or reportextension declares is published under the object it extends
  (#5004), so it is recorded under the base object's key, the same key the
  subscriber's `ObjectType::Table` / `Page` / `Report` attribute names. If an
  extension declares an event and the source parse cannot name exactly one base object
  for it, the raise cannot be keyed, and that module's publishers do not count as
  fully seeded.
- `trig|Table|<id>` when the test inserted, modified, deleted, renamed or validated a
  record of that table, in any app: BC asks the table's metadata (or the field's, for
  validate) whether a trigger event is subscribed on each of those operations,
  subscribed or not, and the runner observes that question. It is recorded per table,
  not per trigger event.

It also stores the `[EventSubscriber]` bindings of the request's modules as the tests
ran with them. The next request compares them with the current ones. A binding that
was added or removed, a binding whose attribute or codeunit binding mode changed (its
old and its new event both count), and every binding of a subscriber whose code
changed (the whole object, or the procedure the change was narrowed to) adds its
event to the changed keys. A test is selected when its recorded events meet them. An
empty subscriber body records no statement, so a body filled in later is found this
way and not through coverage.

A full run is forced, with a `reason` naming the subscriber, when a changed binding's
event could have been raised without being recorded:

- the publisher is outside the request's modules and none of that event's raises were
  seen (for example a new subscriber on a Base Application codeunit event nothing
  subscribed to before);
- the publisher is in the request's modules but not all of its event scopes could be
  seeded;
- it is a page trigger event (`OnOpenPageEvent` and the others), or an object type the
  dispatcher does not handle;
- the bindings could not be read, or the baseline holds none.

A changed binding to an event that did not exist in the recording run, on a publisher
that did (or on a publisher that is itself new), adds nothing: raising that event needs
code that changed, and coverage selects the tests reaching it.

Recording cost, measured for #4988 on the al-language corpus at a9b4430e (one server process):
a steady-state `perTestCoverage` run took 38.0 s with the event recording and 38.3 s
with it stubbed out, against 37.5 s for a run recording nothing; the difference is
inside run-to-run noise.

#### affectedOnly and changed tables

A table with no triggers executes no statement, so it is in no test's coverage, yet
adding an `OnInsert`, a field `OnValidate`, an `InitValue`, a key or a tableextension
changes what every test holding a record of it observes (#5008). The recording run
therefore also records, per test, `tbl|Table|<id>` for every table the test built a
record of, read-only records included (the runner observes BC's `NavRecord`
constructor, which every record variable, `RecordRef`, page source record and
table-relation lookup goes through), and `tbl|TableExtension|<id>` for each
tableextension extending such a table at the time.

A changed `Table` (added, edited, removed) adds `tbl|Table|<id>`, `trig|Table|<id>`
and `trig|?` to the changed keys. A changed `TableExtension` adds its own
`tbl|TableExtension|<id>` (the tests that held its base table when it existed) and
the keys of the table it extends now, from the runner's tableextension registry; a new
extension is found through the second, a removed one through the first. A test is
selected when its recorded keys meet them, so an edit to one table selects the tests
that used that table and no others.

A full run is forced, with a `reason`, when the tests reading a changed table cannot be
told apart:

- a record of the table was held outside any one test: built while no test was
  running, or a global of a test codeunit or of a SingleInstance codeunit, which a
  later test reads without building one (a test codeunit's instance outlives its
  tests). The runner walks the record's owner chain: a procedure scope first is a
  local; a test codeunit or a SingleInstance codeunit first is not;
- a tableextension's base table is in neither the current registry nor the recording;
- the baseline holds no table record (one recorded before #5008; the persisted store's
  schema changed with it, so a store written earlier is no baseline).

Not covered here: a test that reads a table only through a query builds no record of
it.

#### affectedOnly and entered scopes

Statement coverage records nothing for code that has no statement: an empty procedure
or trigger, or a page with no triggers at all. A change adding the first statement
there used to select no test, while a full run failed the tests that used it (#5011).
The recording run therefore also records, per test:

- every procedure and trigger the test entered, empty or not, as the same
  `<object>::proc:<name>` key a statement in it records. The runner observes BC's
  `ALMethodScope.ALStart`, which every AL method runs first;
- every codeunit, page, report, xmlport and query instance the test built, as the
  object's key. The runner observes BC's `NavApplicationObjectBase` constructor.
  Records and tableextensions are left to the table keys above, and a test codeunit's
  own instance is not recorded.

So an empty procedure that gains a body selects the tests that called it, an empty
`OnRun` reached by `Codeunit.Run(<id>)` selects the tests that ran it, and a page
without triggers that gains an `OnOpenPage` (a whole-object change) selects the tests
that opened it. A changed `Page` no longer forces a full run.

An object's key comes from its class, so an object declared in a file with several
objects keys too, instead of making the test unknown; a change to such a file still
forces a full run, as before (#5003).

A full run is forced, with a `reason`, when the tests using a changed object cannot be
told apart:

- an instance of it was built outside any one test: while no test was running, or as a
  global of a test codeunit or of a SingleInstance codeunit (the same owner walk as for
  records). A later test can use that instance without building it or entering any of
  its code, for example `Run()` on a codeunit with no `OnRun`. Only a whole-object
  change forces this; a change narrowed to one procedure selects the tests that
  entered it, whichever instance they ran on;
- a `PageExtension` changed: which page it extends is not recorded, so which tests
  open that page is not known.

The persisted store's schema changed with this (schema 3), so a store written earlier
is no baseline. A request that records nothing does not record these either.

Recording cost, measured for #5011 on the al-language corpus at 31b033d6 (one server
process per build, same cache, steady state after the first request): a
`perTestCoverage` run took 39.5 s on average with this recording and 40.0 s with it
stubbed out (four runs each); a plain run took 38.6 s and 40.0 s (six runs each). Both
differences are inside run-to-run noise.

#### affectedOnly and packaged dependencies

A common layout is `App/` (source), `App.Test/` (source) and
`App.Test/.alpackages/App.app`, with a request naming only `App.Test`. The app then
runs from the package, and the statements it executes are attributed to the files
of its source folder `App/`, which the runner registers as a sibling source. No
module of the request tracks those files, so they used to make every test that
calls into the app unknown, and it reran on every request (#4973).

The environment key (see the forced-full causes above) now carries a SHA-256 of
each resolved dependency package that is not published by Microsoft, was not
synthesized by the runner from a sibling source (`workspace-deps`), and **is the
module that actually runs** for its AppId. Replacing such a package, even with a
rebuild of the same version, changes the key and forces a full run. That is what
makes it safe for selection to ignore a statement attributed to the source folder
of a package the key covers: the code that statement came from can only change by
changing the key. Statements under any other untracked file still make the test
unknown.

The "actually runs" condition matters: once a request has compiled `App/` as its
own bundle, a later request that resolves `App.app` reuses that source-compiled
module for the AppId (#1892) rather than loading the package. The package's bytes
then say nothing about what runs, so it is left out of the key and the statements
stay unknown: an edit to `App/` followed by a request naming `App/` is picked up.
While the package is the module that runs, an edit to `App/` that is not rebuilt
into the package changes nothing the tests execute and selects nothing.

The request-bundle exclusion compares symlink-resolved paths, so a request folder
reached through a link is never ignored. Package hashes go through the process's
shared content-hash memo, so an unchanged package is not re-read. Microsoft
packages are left to the BC version and package-directory parts of the key.

#### affectedOnly and the AL-output cache

`changedObjects` is a diff against the change model's per-module baseline (the
incremental compiler's record of the last source it compiled). It is only a
correct selection input when that baseline is the code the stored per-test
coverage was measured on. Two things can break that, and both are handled:

- **A cache HIT loads code without compiling it**, so it cannot move the
  baseline (#4971: edit, revert, re-apply). A request that records coverage
  (`affectedOnly` or `perTestCoverage`) therefore takes a HIT only when the
  module's current baseline was itself compiled from the source under that cache
  key; otherwise it compiles, which moves the baseline to the loaded source. A
  server started on a warm cache has no baseline, so its first request compiles;
  without a persisted selection baseline it also reports `forcedFull` with the
  change model's "no incremental baseline" reason. From the second request on,
  unchanged bundles are HITs again and selection narrows (#4972). A request that
  records nothing keeps taking HITs freely.
- **Another request can move the baseline without recording coverage** — a
  `runTests` without `affectedOnly`, an `execute`, or a module reused from an
  earlier load. Each baseline carries a generation number; coverage remembers the
  generation of every request module it was recorded against, and the next
  `affectedOnly` request compares those with the generations it found at its
  start. Any difference forces a full run with a reason naming the module.

The generation check applies to coverage this process recorded. Coverage loaded from
disk is checked by file content instead, as the next section describes.

#### affectedOnly across server processes

Every request that records per-test coverage (`affectedOnly` or `perTestCoverage`)
also writes it to disk, so the next server started on the same cache root does not
begin with a full run (#4979). An `affectedOnly` request for a bundle this process
holds no coverage for loads it.

- **Where**: `<cache root>/affected-baseline/<hash>.json`, one file per request bundle
  set (the `sourcePaths`, resolved to full paths; order and duplicates do not matter).
  The cache root is the `--cache` directory, the default cache root without one, and a
  throwaway directory under `--no-cache`, so `--no-cache` never persists anything.
  Each write goes to a temporary file that is then renamed, so a server reading the
  file sees the previous version or the new one. Two servers on one cache root
  replace each other's file (last writer wins); each version is a complete baseline.
- **What**: per bundle, what the process keeps in memory (per-test covered object and
  procedure keys, the unknown and failing tests, per-test raised events and table keys
  (plus a `<bundle>` entry for records held outside any one test), the
  subscriber bindings and event observability, the environment key), and per request
  module, the change model's baseline at the moment the coverage was recorded: the
  SHA-256 of every `.al` file, the one object each file declares, and the fingerprints
  of `app.json`/preprocessor symbols and of the resolved dependency set. Statement
  tables are not stored. Object and scope keys are stored once and referenced by
  index. A `Schema` field is compared with the runner's; any other value is no
  baseline.
- **How it selects**: the environment key must be equal, as for coverage recorded in
  the process. Then the stored module snapshots are compared with the change model's
  baselines for the request, which the request's own compile has just recorded (a
  request that selects always compiles when the change model has no baseline; see the
  previous section). A file whose hash differs, that was added, or that was removed
  changes the object it declared and the one it declares now. That is an object-level
  key, so every test that touched the object is selected, not just the tests of the
  changed procedure. Subscriber bindings are compared with the stored ones, the same
  as in the process.
- **When it runs everything instead**: the file cannot be read or parsed, carries
  another schema version, or has no entry for a module of the request; a module has no
  change-model baseline in this process (for example a module reused from another
  directory); `app.json`, the preprocessor symbols or the dependency set changed; a
  changed file does not declare exactly one object the change model tracks. Each of
  these is a `forcedFull` with a `reason`.
- **After the first request**: the request records coverage for the code it ran and
  the process continues with the generation check above. A baseline that could not be
  written (a module without a change-model baseline) leaves the previous file in
  place, which still describes the source its own hashes name.

Measured on the al-language corpus: see the pull request that introduced this (#5007)
for the file size and load time.

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

## `sourceScanFailures` — the coverage table is short (#3847/#3884)

Present on `summary` and on an `execute` response only when a source-map scan ran **and** could
not read something. Never an empty array, the same convention `coverage` uses.

**Absent does not mean "every source was read."** It means no scan failure is being reported,
and an ordinary request that builds no source map at all — no `coverage`, no `perTestCoverage`,
no `captureValues`, no `iterationTracking` — reports nothing here either. If you need to tell
*not scanned* from *scanned and clean*, the request flags you sent are what distinguishes them;
this field does not.

```json
"sourceScanFailures":[{"path":"/src/app/gone","reason":"the source root does not exist","kind":"Root"}]
```

`kind` is `Root`, `Directory` or `File`. The first two cover every source **beneath** them; a
`File` entry covers exactly that one path, so do not treat it as a prefix.

**What it means for the tables in the same response.** `coverage` and `perTestCoverage` are
built from that map, so they are missing whatever these paths declare. A statement absent from
them is **unmeasured, not uncovered** — the distinction the field exists to make, because a
coverage percentage computed over an unknown subset is not a coverage percentage.

`exitCode` is `2` when this field is present and a coverage table was produced, unless the run
earned something more specific; a run that was already failing keeps its own code. The CLI
`--coverage` path does the same thing and names the paths on stderr.

## `companyInitFailures` — the company the tests ran against (#3561)

Present on a `runTests` summary and on an `execute` response exactly when a company
initialization codeunit did not run to completion during **that request**; absent otherwise,
never an empty array, and there is no request field that opts into it. Each entry is
`{codeunitId, codeunit, exceptionType, message, count, accepted|omitted}`:

```json
{"type":"summary","exitCode":2,"passed":1,"failed":0,"errors":0,"total":1,
 "companyInitFailures":[{"codeunitId":2,"codeunit":"Company-Initialize",
   "exceptionType":"InvalidOperationException","message":"…","count":1}],
 "protocolVersion":2}
```

Real BC cannot present a half-initialized company, so this says the DATABASE the AL ran against
was not the one asked for — not that anything about the AL failed. `count` is how many app
groups reported the same abort (identical records are collapsed into one entry). `accepted`
carries the expectations-manifest reason when the project declares the condition accepted, and
then the escalation below does not happen; see
[partial-company-initialization.md](partial-company-initialization.md).

Two consequences of the manifest being resolved at **server startup**, not per request: the
acceptance only applies when the server was started with `--expectations <dir>` (or from a
working directory carrying `tests/expectations`), and the stale-entry drift check is CLI-only —
"this codeunit completed" is a per-process fact, and a long-lived server has no end of run to
judge it at.

The accumulator is drained once per request, so a response reports its own request's aborts and
never a previous one's.

## Exit codes

Same ladder as normal mode: `0` all pass · `1` test failures · `2` execution
error · `3` compilation error. In server mode the code rides on each `runTests`
response's `exitCode`; the process itself exits `0` on `shutdown`/EOF. A request whose company
initialization did not complete reports `2` on that response — the same escalation the CLI
makes, and for the same reason: a client reading only `exitCode` must not read the run as clean.
