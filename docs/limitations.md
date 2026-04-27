# AL Runner — Limitations

AL Runner targets broad AL language compatibility. The limits below are
architectural — they require the BC service tier and cannot be emulated in a
single .NET process. Everything else is either already supported or a gap that
can be fixed. If AL code fails to run and the reason is not listed here, report
it as a bug.

---

## Architectural limits — cannot be fixed

### No BC service tier

The runner has no SQL Server, no BC server process, and no license. It runs your AL
as .NET code in a single process. This rules out anything that is inherently tied to
the BC runtime environment:

- **Permissions and entitlements** — there is no permission system. All field/table
  access succeeds unconditionally. `entitlement_declaration`, `permissionset_declaration`,
  and `permissionsetextension_declaration` object types compile but have no effect at runtime.
- **Company context** — no active BC company. `CompanyName()` defaults to empty
  string but is configurable: pass `--company-name <name>` on the CLI, or call
  codeunit 131100 `"AL Runner Config".SetCompanyName(Name)` from AL tests.
  Code that only branches on whether the name is empty still takes the "empty"
  branch by default.
- **Base app data** — no standard BC tables are populated. Code that reads
  `G/L Account`, `Customer`, `Vendor`, or any other base app table finds them empty
  unless your test inserts data.
- **Setup tables** — `General Ledger Setup`, `Sales Setup`, etc. are empty.
  Code that reads setup fields gets type defaults.

### No transaction semantics

There is one flat, in-memory record store shared across the entire test run.
`Commit()` and `Rollback()` are no-ops. As a result:

- Code that detects whether a nested codeunit called `Commit()` will not work.
- Code that relies on rollback to undo partial writes will not work.
- The isolation between a "worker session" and its caller does not exist.

### No parallel session execution

`StartSession` runs the target codeunit **synchronously, inline**, before returning.
The implications:

- `IsSessionActive` always returns `false` — the session is already done.
- Session timeout logic never fires — there is no wall-clock timer or background thread.
- Tests that poll until a session finishes see all results already present from the first call.
- Workers share the same record store as the caller — there is no cross-session isolation.

Libraries built around parallel execution (e.g. parallel-worker-bc) can have their
pure-logic tests pass, but any test that exercises the parallel contract itself — timeout
enforcement, transaction isolation between workers, async completion detection — cannot
pass here.

### Event subscribers — supported

The runner dispatches event subscribers. `RunEvent()` calls are rewritten to
`AlCompat.FireEvent(publisherCodeunitId, eventName, ...)`, which scans the compiled
assembly for `[NavEventSubscriber]` methods at startup and calls matching subscribers.

**What works:**
- Custom `[IntegrationEvent]` / `[BusinessEvent]` publishers with any subscriber signature.
- Subscribers that receive `var` parameters (e.g. `var Rec: Record X`, `var IsHandled: Boolean`) — the rewriter forwards all event parameters, and `var` arguments are wrapped in `ByRef<T>` so mutations propagate back to the publisher.
- `IncludeSender = true` — the sender codeunit instance is prepended as the first argument.
- Database event subscribers (`OnAfterModify`, `OnBeforeInsert`, etc.) receive `Rec` and can read or modify fields; the mutations are visible to the caller after the trigger returns.

### No UI rendering

Pages are not rendered. There is no layout engine, no field visibility evaluation, and
no report dataset. `TestPage` provides expanded field access, navigation, and handler
dispatch, and report/request-page variables support a limited standalone surface, but:

- Field `Visible`, `Enabled`, and `Editable` are not evaluated against real page metadata.
- `TestPage` methods like `GoToRecord`, `Next`, `New`, `GetPart`, and filter reads are
  mock-backed rather than UI-backed.
- `TestPage` action `Invoke()` dispatches the compiled `OnAction` trigger for custom actions; field `Visible`/`Enabled`/`Editable` are not evaluated against real page metadata.
- `Page.Run()` is a no-op. `Page.RunModal()` dispatches to `[ModalPageHandler]` if
  registered, otherwise throws.
- Request pages can be handled via `[RequestPageHandler]`, but this is handler dispatch
  only, not real request-page rendering.
- Report variables support `Run()`, `RunRequestPage()`, `SetTableView()`, and
  helper procedures. Report triggers execute: `OnPreReport`, `OnPreDataItem`,
  `OnAfterGetRecord` (once per row in the in-memory table), `OnPostDataItem`, and
  `OnPostReport`. Report layout/rendering is still not available.

### No debugger infrastructure

The runner executes in a single .NET process with no attached BC debugger. Debugger API calls that require a live BC debug session cannot work:

- `Debugger.Attach()` — attaches to a live session; no session infrastructure exists.
- `Debugger.Break()`, `BreakOnError()`, `BreakOnRecordChanges()` — set breakpoints; no breakpoint mechanism.
- `Debugger.Continue()`, `StepInto()`, `StepOut()`, `StepOver()`, `Stop()` — step/continue through debugger; no debug loop.
- `Debugger.DebuggedSessionID()`, `DebuggingSessionID()` — query debugger session IDs; always meaningless standalone.
- `Debugger.EnableSqlTrace()` — SQL tracing on a specific session; no SQL server exists.
- `Debugger.GetLastErrorText()` — debugger-specific error query; not to be confused with `GetLastErrorText()` (a System function, which is covered).
- `Debugger.IsAttached()` — always false (no attached debugger).
- `Debugger.IsBreakpointHit()` — no breakpoints can be hit.
- `Debugger.SkipSystemTriggers()` — controls trigger dispatch in a debug session; no debug session.

`Debugger.Activate()`, `Debugger.Deactivate()`, and `Debugger.IsActive()` are supported — they are stripped or return `false`.

### Task scheduler — synchronous dispatch

`TaskScheduler.CreateTask()` dispatches the target codeunit **synchronously, inline**,
before returning — the same pattern as `StartSession`. The implications:

- `TaskExists()` always returns `false` — the task already completed before the call returned.
- `CancelTask()` and `SetTaskReady()` are no-ops — the task has already run.
- `CanCreateTask()` returns `false` — there is no background job queue.
- `NotBefore` and `CompanyName` parameters are accepted but ignored — the codeunit runs immediately in the current company context.

AL that tests the *logic* around task creation (what codeunit runs, what state it produces) works here. AL that tests the *scheduling contract* (task still pending, NotBefore delay, cancellation before execution) cannot work here because there is no background scheduler.

### No DotNet interop

`.NET interop` requires the BC runtime, which handles `.NET` variable binding, `assembly` declarations, `dotnet` type wrappers, and the `DotNet` AL type:

- `System.CanLoadType(DotNet)` — requires a `.NET` type reference at runtime.
- `System.GetDotNetType(Joker)` — resolves the `.NET` type for an arbitrary AL value; no `.NET` type resolution without BC service tier.
- `assembly_declaration`, `dotnet_declaration`, `type_declaration` — object types that wrap .NET assemblies; not compiled in standalone mode.

### Query — single-dataitem only

Query objects with a single dataitem work in-memory: `Open` reads from the
mock table store, `Read` iterates rows, `Close` releases the result set.
`SetFilter`, `SetRange`, and `TopNumberOfRows` filter and limit the results.
Column values are returned from the current row via `GetColumnValueSafe`.

**Not supported:** multi-dataitem queries (JOINs), aggregation methods
(Sum, Count, Average, Min, Max), and `SaveAsCsv`/`SaveAsXml`/`SaveAsJson`/
`SaveAsExcel`. These throw `NotSupportedException`.

### UI objects — out of scope

The following AL object types require the BC client or client-side rendering and are deliberately excluded from the runner. AL files that declare them still compile (the runner accepts whatever the BC compiler emits), but the runner takes no action on the object-level metadata:

- `controladdin_declaration` — control add-ins require a JavaScript/browser runtime.
- `profile_declaration`, `profileextension_declaration` — user profiles and page customisations are a BC client feature with no standalone equivalent.
- `usercontrol_section` — user-control page sections require BC client rendering.

These are classified `out-of-scope` because supporting them requires the BC client, which is architecturally outside the runner's scope (run AL unit tests in a single .NET process, no service tier, no browser, no Docker).

### HTTP — partial support

HTTP types (`HttpClient`, `HttpRequestMessage`, `HttpResponseMessage`, `HttpContent`,
`HttpHeaders`) are replaced with in-memory mocks. The following works:

- `HttpContent.WriteFrom(Text)` / `ReadAs(var Text)` — text round-trip
- `HttpContent.WriteFrom(InStream)` / `ReadAs(var InStream)` — stream round-trip
- `HttpResponseMessage.HttpStatusCode()` (default 200), `IsSuccessStatusCode()`
- `HttpHeaders.Add()`, `Contains()`, `Remove()`
- `HttpRequestMessage.Method()`, `SetRequestUri()`, `Content()`

**Not supported:** `HttpClient.Send()`, `Get()`, `Post()`, `Put()`, `Delete()`,
`Patch()` — these throw `NotSupportedException`. Inject HTTP dependencies via an
AL interface if you want to unit test the logic around HTTP calls.

---

## System Application codeunits — scope policy

### What the runner ships

The runner ships hand-written AL stubs and C# mock implementations **only** for objects whose sole purpose is to make test codeunits compile and execute assertions. These contain no BC business-domain logic.

**Always in scope — test-automation infrastructure (approved exceptions):**

| Codeunit ID | Name | File |
|---|---|---|
| 130 | `"Assert"` (Library Assert) | `AlRunner/stubs/LibraryAssert.al` + `AlRunner/Runtime/MockAssert.cs` |
| 131 | `"Library Assert"` (alias) | `AlRunner/stubs/Assert.al` |
| 130000 | Assert from BC test toolkit | routing alias, no extra file |
| 130002 | Real BC "Library Assert" ID | routing alias, no extra file |
| 131004 | `"Library - Variable Storage"` | `AlRunner/stubs/LibraryVariableStorage.al` + `AlRunner/Runtime/MockVariableStorage.cs` |
| 130440 | `"Library - Random"` | `AlRunner/stubs/LibraryRandom.al` (pure AL, BC primitives only) |
| 130500 | `"Any"` | `AlRunner/stubs/LibraryAny.al` (pure AL, BC primitives only) |
| 131003 | `"Library - Utility"` | `AlRunner/stubs/LibraryUtility.al` (pure AL, GUID/random text) |
| 132250 | `"Library - Test Initialize"` | `AlRunner/stubs/LibraryTestInitialize.al` (event publishers only) |
| 131100 | `"AL Runner Config"` | `AlRunner/stubs/AlRunnerConfig.al` (runner-only; not a BC codeunit) |

Adding a new entry here is a high bar: it must be a *test-automation* library (something a test codeunit uses to assert or orchestrate), not a piece of business logic.

**Always out of scope — SA business-logic implementations:**
The runner must not ship a real implementation of any System Application codeunit (Image, FileMgt, Cryptography, Email, DocumentSharing, WebServiceMgt, …). Auto-generated blank shells are fine — C# classes that re-create SA business behaviour are not.

**Always out of scope — domain test libraries:**
Domain test libraries such as `Library - Sales` (130509), `Library - Purchase`, etc. are auto-stubbed from BC packages, not hand-shipped. They must stay auto-stubbed only; no hand-written implementation is permitted.

### What the runner auto-generates

For every codeunit/object pulled in from your dependencies (System Application, Base Application, third-party apps), the runner auto-generates a **blank shell**: every method exists with the right signature, returns the type-default, and does nothing.

That is how AL compiles without those packages being present at runtime. It is not a real implementation — it is scaffolding.

### Why no real SA implementations

The moment the runner ships a re-implementation of an SA codeunit, it inherits the burden of staying faithful to the real System Application across every BC version. Your tests would be asserting against the runner's reimplementation rather than against BC. This has happened once (MockImage was reverted in #1502 for exactly this reason).

### Bring your own stub

If your AL under test depends on real SA behaviour to mean anything, the supported pattern is **provide your own stub** in your test project. Two common shapes:

1. **AL interface + injected implementation.** Define an AL interface, have your production code take it via dependency injection, ship a real implementation that delegates to the SA codeunit, and ship a fake implementation in your test project that does just enough to make the test pass.
2. **Test-only AL codeunit shadowing the SA call.** Add an AL codeunit in your `test/` directory with the same object ID and a hand-rolled implementation that returns the values your test expects. The runner will use your codeunit because it is in the compile unit; in real BC, your production code never sees it.

Concrete example — `Image` codeunit (System Application). A test that asserts on image dimensions cannot rely on the runner's blank-shell `Image.GetWidth()` (which returns `0`). The fix is to write a small stub in your test project that parses a known fixture image, not to ask the runner to ship an `Image` implementation. If the AL pattern under test is widespread enough that everyone needs the same stub, file a runner-gap issue and we can discuss whether a shared stub belongs in `AlRunner/stubs/` (the bar is high — it must be test-automation infrastructure, not business logic).

---

## Behavioural differences — same API, different semantics

These don't crash, but they behave differently from real BC. Tests that assert on
the exact value will see different results.

| AL call | Real BC | al-runner |
|---|---|---|
| `CompanyName()` | Active company name | `""` (or `--company-name <name>` / `"AL Runner Config".SetCompanyName()`) |
| `UserId()` | Authenticated user | `""` (configurable via `--user-id <value>` / `PipelineOptions.UserId`) |
| `IsSessionActive(id)` | True while session runs | Always `false` |
| `GuiAllowed()` | False in background sessions | `false` |
| `GetFilter(field)` | Serialised filter expression | Returns serialised filter expression (functional) |
| Field `InitValue` | Applied on `Init()` | Applied — parsed from AL source at pipeline start via `TableInitValueRegistry` |
| `FieldRef.Caption` / `.Name` | Field metadata from schema | Real values for all AL-compiled tables including tableextension fields; `"FieldNN"` stub only for base-app tables not compiled in the current run |
| `Commit()` | Commits current transaction | No-op |
| `FilterGroup(n)` | Scoped filter groups | Not tracked — `FilterGroup()` is a no-op; all filters apply to group 0 |

---

## Known gaps — in scope but not yet implemented

These are not architectural limits. They can be fixed; report them at
https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues.

- **FilterGroup** — `Rec.FilterGroup(n)` has no effect; filters always apply to group 0.

---

## When to use the full BC pipeline instead

al-runner targets broad AL language compatibility. If AL code compiles but
fails to run, that is a gap to report, not a reason to restructure your code.

The hard exceptions — things that require the BC service tier by architecture —
are listed above. For those, test in the full pipeline:

- Real company or setup data being present
- Parallel sessions running concurrently
- Transaction boundaries (commit / rollback)
- Page or report rendering
- HTTP calls to external services
- Permissions or entitlements

Everything else is in scope for the runner. If you hit a failure that does not
fall into one of the categories above, report it as a gap at
https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues.

```
al-runner  →  AL logic failures in seconds
    ↓ (only if al-runner passes)
Full BC pipeline  →  full fidelity, 45+ minutes
```
