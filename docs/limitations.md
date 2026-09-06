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
- **Company context** — no active BC company. `CompanyName()` and `UserId()` are
  seeded with fixed defaults (empty string / `"TESTUSER"`) at runtime startup —
  not currently configurable via a CLI flag or an AL-callable API. Code that
  only branches on whether the name is empty still takes the "empty" branch by
  default. If your workflow needs a different value, open an issue describing
  the use case. Both identities are also written to the table that holds them,
  so AL's own referential checks resolve them: one row in Company (2000000006)
  for `CompanyName()` and one in User (2000000120) for `UserId()` /
  `UserSecurityId()`, with the User Property (2000000121) companion row BC
  creates alongside every user. One consequence worth knowing: Microsoft AL that
  skips a check while the User table is entirely empty — `User Selection
  .ValidateUserName` is the common one — now runs that check, so a made-up user
  name is refused the way real BC refuses it. The Session virtual table
  (2000000009) is still empty; that gap is tracked separately.
- **Base app data** — no standard BC tables are populated. Code that reads
  `G/L Account`, `Customer`, `Vendor`, or any other base app table finds them empty
  unless your test inserts data.
- **Setup tables** — `General Ledger Setup`, `Sales Setup`, etc. are empty.
  Code that reads setup fields gets type defaults.

### Transaction semantics — commit-point rollback and `Codeunit.Run`'s write-transaction scoping are modeled

There is one flat, in-memory record store shared across the entire test run — no
SQL transaction log, no isolation levels, no `READCOMMITTED`/`REPEATABLEREAD`
distinctions. On top of that store, though, the runner tracks a rolling **commit
point**: established at the start of every test method, by an explicit AL
`Commit()`, and by BC's own APIs that complete a real nested transaction
internally (both calling shapes of `XmlPort.Import`, tracked in #1946). When an AL
error is caught — by `asserterror`, or anywhere else BC's own
`NavMethodScope.AssertError` catches it — every write made since the last commit
point is rolled back, mirroring BC's own `session.Rollback()`.

This is not a guess: it is pinned by the upstream corpus, which a real BC service
tier validates. `error-handling/TestAssertErrorRollback.al` checks whether an
uncommitted `Insert`/`Modify` survives an unrelated later error (no), whether an
explicit `Commit()` moves the surviving boundary forward (yes), and whether
temporary-table writes participate in rollback at all (no — they have no database
backing to roll back). `record/TestTriggerRollback.al` checks the same thing for a
write that fails inside its own `OnInsert`/`OnModify`/`OnDelete` trigger.

What this means in practice:

- `Commit()` is not a no-op — it moves the rollback boundary forward. A write made
  before `Commit()` survives a later unrelated error; a write made after the last
  `Commit()` does not.
- Rollback undoes partial writes correctly along the main call path, including
  writes made inside a nested codeunit call, and including writes a nested BC API
  already committed inside its own transaction (`XmlPort.Import` et al.).
- A test that relies on "the previous test method's `Commit()`ed row is still
  there, but this method's own uncommitted writes get rolled back by my
  `asserterror`" works the same way it does on real BC.

- `Codeunit.Run` is refused while a write is still uncommitted, but only in the
  **guarded** form — the one whose `Boolean` result is consumed, e.g.
  `Ok := Codeunit.Run(Codeunit::X);`. That form needs its own isolated transaction, so
  BC will not open one on top of a pending write and raises "An error occurred and the
  transaction is stopped." The **statement** form, `Codeunit.Run(Codeunit::X);` with the
  result discarded, just joins the caller's transaction and is allowed. `Commit()` before
  the call clears the refusal.

  Confirmed against a real BC service tier, not inferred: all three assertions —
  guarded form refused, statement form allowed, guarded form allowed after `Commit()` —
  pass on **BC 27.5 and 28.3** in `codeunit/TestCodeunitRunWriteTransaction.al`, merged
  upstream as
  [`30d46f95`](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/commit/30d46f95665aeed87bff3e14234a521d3232a68d)
  (corpus PR #75). Runner side landed in #2133.

  This rule does **not** generalise to every method BC's own error text names.
  `Result := Page.RunModal(...)` and `Ok := XmlPort.Export(...)` after an uncommitted
  `Insert()` are both green on a real service tier
  (`handlers/TestPageModalHandlerStatic_Tests.al`,
  `xmlport/TestXmlPortObject.al`), so the runner does not refuse those. What is still
  unmeasured — `Ok := XmlPort.Import(...)` and `Ok := Report.RunModal(...)` with a
  pending write — is tracked in issue 2184.

Separately, and unrelated to rollback: the isolation between a "worker session"
and its caller does not exist. `StartSession` runs synchronously, inline, sharing
the same record store as the caller — see "No parallel session execution" below.

### Test isolation modes — mapping to AL's `TestIsolation` values

The `--isolation` (alias `--test-isolation`) flag picks one of three granularities.
They are AL's own `TestIsolation` values: reading the strings out of
`Microsoft.Dynamics.Nav.CodeAnalysis.dll` shows the property accepts `Disabled`,
`Codeunit` and `Function`.

| `--isolation` value | AL `TestIsolation` | BC test runner codeunit | Database (record store) | AL global variables |
|---|---|---|---|---|
| `codeunit` (default) | `Codeunit` | 130450 "Test Runner - Isol. Codeunit" | Rolls back after each test **codeunit**. A row one `[Test]` writes without committing is still visible to the next `[Test]` in the same codeunit. | Shared across every `[Test]` in the same codeunit — one codeunit instance runs them all |
| `test` (alias `method`) | `Function` | none — no shipped BC runner declares `Function` | Rolls back before every `[Test]` procedure | **Not** shared — every `[Test]` runs on a brand-new codeunit instance |
| `disabled` | `Disabled` | 130451 "Test Runner - Isol. Disabled" | Never rolls back — suite-long sharing | Shared for the whole suite |

Both of the last two columns are measured against a real service tier, not inferred.
In the [al-language corpus](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests),
`TestIsolationRollbackScope` (60897) writes an uncommitted row in one `[Test]` and reads
it back in the next `[Test]` of the same codeunit, and `Test Isolation Global Var`
(60898) does the same with a global Integer and a global Text. Both are green on BC 27.5
and 28.3: the row survives and so do the globals, which is what "rolls back after each
test codeunit" and "one codeunit instance runs them all" mean in practice.

#### A correction worth recording (#2160)

Between #2144 and #2160 this table said something different and wrong: that `codeunit`
rolls the database back before *every test*, that `test` matches a BC codeunit 130452
"Test Runner - Isol. Test", and that `disabled` matches 130453. Extracting the shipped
`Microsoft_Test Runner.app` shows 130452 is "Test Runner - Get Methods" and 130453 is
"ALTestRunner Reset Environment". Neither is an isolation runner, and no
"Test Runner - Isol. Test" codeunit exists in BC at all.

The database claim came from a differential measurement against a BC container, taken
through a harness that invokes tests one at a time. Such a harness cannot distinguish
"the platform rolled back" from "the harness opened a new transaction", and it reported
the first when the truth was the second. The corpus test above is what settled it,
because a real service tier ran the two tests inside one codeunit the way BC runs them.

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
- `IncludeSender = true` — the sender codeunit instance is bound to the subscriber's sender parameter regardless of its position in the declared parameter list (matching real BC — #2348).
- Database event subscribers (`OnAfterModify`, `OnBeforeInsert`, etc.) receive `Rec` and can read or modify fields; the mutations are visible to the caller after the trigger returns.

### No UI rendering

Pages are not rendered. There is no layout engine, no field visibility evaluation, and
no report dataset. `TestPage` provides expanded field access, navigation, and handler
dispatch, and report/request-page variables support a limited standalone surface, but:

- Field `Visible`, `Enabled`, and `Editable` ARE evaluated against real page metadata,
  live, including a control's `Visible` combined with every enclosing `group`'s `Visible`
  up to the content area — but nothing renders, so this only affects what `TestPage`
  reports back, not any actual layout.
- `TestPage` methods like `GoToRecord`, `Next`, `New`, `GetPart`, and filter reads are
  mock-backed rather than UI-backed.
- `TestPage` action `Invoke()` saves the row the page is on and then dispatches the
  compiled `OnAction` trigger, the same order a real client uses — so `OnAction` reads a
  `Rec` that is already in the table, with the page's `AutoSplitKey` field assigned
  (BC's own `NavForm.SplitKey`, in 10000 increments). A plain `SetValue` still does not
  save: the row is written when something leaves it (a cursor move, an action, or close).
  The `AutoSplitKey` *values* are not yet BC's: the runner has no client cursor to take an
  insertion point from, so an empty grid starts at 10000 where BC starts at 20000, and a
  line appended to a grid numbered from something other than 10000 does not continue from
  the last row. Tracked in
  [#1755](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/1755).
- `Page.Run()` (non-modal) dispatches the page the way a client would: to the test's
  `TestPage.Trap()` if one is outstanding, otherwise to the registered `[PageHandler]`,
  otherwise it raises BC's own `Unhandled UI` error. The page a trap receives stays open
  for the test to drive; the one a `[PageHandler]` receives is closed when the handler
  returns. No window is rendered either way. `Page.RunModal()` dispatches to `[ModalPageHandler]` if
  registered, otherwise throws — both the page-variable form
  (`P.SetRecord(Rec); P.RunModal();`) and the static-by-id forms
  (`Page.RunModal(id, Record)`, `Page.RunModal(Page::"X", Record)`, and Base App
  `Codeunit 700 "Page Management"` code that routes through them). The static
  `Page.RunModal(0, Record)` form, which real BC resolves via the record table's
  `LookupPageId`, is not yet implemented and throws
  [#1918](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/1918); pass an
  explicit page id in the meantime.
- Request pages can be handled via `[RequestPageHandler]`, but this is handler dispatch
  only, not real request-page rendering. `Report.Run()` / `RunModal()` open the request page
  and route it to the declared handler, exactly as a real service tier does under test:
  a handler that cancels leaves the report body unexecuted, and one that calls
  `TestRequestPage.SaveAsXml(parametersFile, dataSetFile)` gets the report's dataset written
  to that file (so `Codeunit "Library - Report Dataset"` can load it) instead of a layout.
  A handler asking for a RENDERED artifact instead — `SaveAsExcel`, `SaveAsPdf`, `SaveAsWord`,
  print, preview — is refused loudly on the rendering path, like every other rendering request
  here; it is not answered with a dataset written into the file it named
  ([#2887](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2887)).
  A handler can also read and write the request page's **controls**
  (`RequestPage.ShowAmountsInLCY.SetValue(true)`): a request-page control is bound to one of
  the report's own globals, and it resolves through BC's own `NavForm.SourceExpressions`
  binding table, so a write lands on that global and the report body reads it back.
  One difference from real BC remains:
    - When no declared handler matches the request page, the runner continues WITHOUT
      opening one rather than raising BC's `Unhandled UI` error. It cannot yet tell "the
      test declared no handler" apart from "handler lookup did not reach us", and refusing
      on the second would break reports that run fine today.
- Report variables support `Run()`, `RunRequestPage()`, `SetTableView()`, and
  helper procedures. Report triggers execute: `OnPreReport`, `OnPreDataItem`,
  `OnAfterGetRecord` (once per row in the in-memory table), `OnPostDataItem`, and
  `OnPostReport` — for reports the runner compiles and for precompiled dependency
  reports (Base Application and friends), in whichever flavour BC's compiler emitted the
  trigger (`OnPostReport` or `OnPostReportAsync` behind `__IsAsync`; dispatched through
  BC's own `On{Pre,Post}ReportInternalAsync`). `Run()` drives BC's own data-item loop, so `SetTableView(Rec)`
  constrains the matching data item to the applied view, and `DataItemTableView`,
  `DataItemLink`, nested data items and `CurrReport.Skip`/`Break` behave as the
  runtime engine defines them. Report layout/rendering is still not available.
- The static `Report.Run(id[, requestWindow[, systemPrinter[, record]]])` /
  `Report.RunModal(id, ...)` forms (called on the `Report` codeunit-like object, without
  first declaring a report variable) execute the report the same way the report-variable
  form does — construct the report from its id, then run the same trigger lifecycle, and
  open the request page when `requestWindow` says to. `systemPrinter` is accepted but not
  acted on: nothing prints. The `Report.Run(ReportRunOptions)` overload is not implemented
  and throws `out-of-scope: static NavReport.Run`.

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

### Task scheduler — no scheduler, and no inline substitute

**Tasks are never executed.** `TaskScheduler.CreateTask()` does not run the target codeunit —
not in the background, and not inline either. [`docs/scope.md` §3.6](scope.md#jobs) is the
authoritative description of this surface; the summary below only restates what was measured
against it, so if the two ever disagree again, §3.6 and the Cecil layer win.

The runner Cecil-rewrites `ALTaskScheduler.CanCreateTask` / `ALCanCreateTask` to return
`false` and deliberately leaves `ALCreateTaskAsync` **unmodified**, so BC's own body raises
BC's own exception. Measured on BC 28.1:

| AL call | What actually happens |
|---|---|
| `CanCreateTask()` | `false` |
| `CreateTask()` | throws `You do not have permission to create or run scheduled tasks.` The target codeunit's `OnRun` does **not** run. |
| `TaskExists()` | refuses by name: `out-of-scope: TaskScheduler.TaskExists — task-scheduler — … — see docs/scope.md#jobs`. There is no scheduled-task store to query, and BC's real body has no answer it reaches without one. (#2866; before that it threw a `NullReferenceException` out of `NavSqlConnectionScope`.) |
| `CancelTask()` | refuses by name the same way — **except** for an empty task id, where BC's own body answers `false` before it touches the scheduler, so the runner answers `false` too. (#2866; before that it threw a `NullReferenceException` out of `ALCancelTaskAsync`.) |
| `SetTaskReady()` | throws the same `You do not have permission to create or run scheduled tasks.` as `CreateTask()` — its real body runs the same `CanCreateTask` guard. An empty task id answers `false` before that guard, as on BC. |

So: guarded AL (`if TaskScheduler.CanCreateTask() then …`) skips task creation cleanly and is
the pattern that works here. Unguarded AL that calls `CreateTask()` directly gets BC's loud
refusal, which is deliberate — an earlier version of the runner rewrote `ALCreateTaskAsync` to
return `Guid.Empty`, and that was reverted (#1733, #1739) as a silent fake suppressing BC's
own guard.

AL that tests the *scheduling contract* — a task still pending, a `NotBefore` delay,
cancellation before execution — cannot work here, because nothing is scheduled and nothing
runs. AL that needs the target codeunit's logic to actually execute should call it directly
(`Codeunit.Run`) rather than through `CreateTask`.

> This section previously described `CreateTask()` as dispatching the codeunit
> "synchronously, inline". That described a design that was reverted, and it was wrong in both
> directions: no codeunit runs, and `TaskExists()` does not return `false`. See #2565. It then
> described `CancelTask()` and `SetTaskReady()` as completing quietly, which measurement (#2866)
> showed neither did. The table above is now pinned by `tests/runner-extras/task-scheduler-oos`,
> so it fails a CI leg rather than drifting again.

### No DotNet interop

`.NET interop` requires the BC runtime, which handles `.NET` variable binding, `assembly` declarations, `dotnet` type wrappers, and the `DotNet` AL type:

- `System.CanLoadType(DotNet)` — requires a `.NET` type reference at runtime.
- `System.GetDotNetType(Joker)` — resolves the `.NET` type for an arbitrary AL value; no `.NET` type resolution without BC service tier.
- `assembly_declaration`, `dotnet_declaration`, `type_declaration` — object types that wrap .NET assemblies; not compiled in standalone mode.

### `System.Drawing` — Windows-only in .NET 8, so it never runs on a Linux or macOS host

<a id="system-drawing"></a>

Precompiled Microsoft code does reach .NET interop, and some of what it reaches is
Windows-only. Base Application table 2121 `"O365 Brand Color"`.`MakePicture` builds a
`System.Drawing.Bitmap` to draw a colour swatch, which is why eleven `O365 Brand Color Tests`
fail on a Linux host.

The runner names the surface instead of letting BC's wrapper bury it. Before
[#3212](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/3212):

```
NavNCLDotNetInvokeException: A call to System.Drawing.Bitmap failed with this message:
The type initializer for 'Gdip' threw an exception.
```

After:

```
out-of-scope: NavDotNet.CreateDotNet(System.Drawing.Bitmap) — dotnet-platform-unsupported —
System.Drawing.Common refuses every entry point on this operating system (Linux …). …
.NET reported: System.Drawing.Common is not supported on non-Windows platforms. …
```

**There is no workaround on the host side.** `System.Drawing.Common` 8.0 — the copy BC itself
ships — throws `PlatformNotSupportedException` from its `Gdip` class initializer whenever
`OperatingSystem.IsWindows()` is false, before any native library is consulted; it carries no
`libgdiplus` reference at all, and the .NET 6 `System.Drawing.EnableUnixSupport` switch was
removed in .NET 7. Installing a system package changes nothing.

**What to do with AL that needs it.** Either run that test against a real BC service tier, or
inject the image work behind an AL interface and pass a test double. The runner will not
substitute a different imaging library: the pixels would not be GDI+'s, so the test would be
asserting against the runner rather than against BC — see "Why no real SA implementations"
below and `docs/scope.md#dotnet-platform`.

### Query — joins and dataset export work; aggregation does not

<a id="query-shape-gaps"></a>

Query objects work in-memory: `Open` reads from the mock table store, `Read`
iterates rows, `Close` releases the result set. `SetFilter`, `SetRange`, and
`TopNumberOfRows` filter and limit the results, including runtime `SetRange`/
`SetFilter` applied to either side of a join. Column values are returned from the
current row via `GetColumnValueSafe`.

**Multi-dataitem queries (JOINs) are supported.** Inner and left-outer joins
across two dataitems run a real in-memory join, including unmatched-parent
handling for `LeftOuterJoin`. Pinned by the upstream corpus
(`query/TestQueryJoin.al`, migrated from this repo's own `query-join` suite).

**`SaveAsJson`, `SaveAsXml`, and `SaveAsCsv` run BC's own implementation** against
the query's real metadata and produce a genuine dataset — they are not stubbed out.

There is no `Query.SaveAsExcel` method in the AL language; this doc previously
listed one that doesn't exist.

**Sub-shapes of a working join the executor refuses rather than guessing.** Nine
guards in `AlRunner/Patches/RecordPatches.QueryProjection.cs` and
`RecordPatches.QueryJoin.cs` raise `RunnerOutOfScopeException` when the query the
executor is handed is a shape it cannot take: a synthesized sub-dataitem that is
not the FlowField-calculation shape, a runtime `SetRange`/`SetFilter` or a static
`ColumnFilter` keyed by a column outside the projected row, or a BC helper
(`NavValue.GetDefaultNavValue`, `FlowFieldsHelper.NegateValue`) that is not on this
build. They carry the reason anchor `not-yet-implemented`, so an AL `[TryFunction]`
cannot trap one into `false`
([#2966](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2966)).
These are gaps, not scope boundaries: real BC answers every one of them, and
`docs/scope.md` no longer claims otherwise.

**Not supported: aggregation.** A column with `Method = Sum` (or `Count`,
`Average`, `Min`, `Max`) does not aggregate or group — the runner returns each
row's own value unaggregated instead of collapsing rows per BC's SQL projection.
This is a known gap, not the documented `NotSupportedException` this doc used to
claim — the runner returns a wrong value silently rather than throwing. Tracked in
[#2137](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2137).

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

### Document-service providers (`DOCUMENTSERVICEMOCK`)

Base Application codeunit 9510 `"Document Service Management"` resolves a provider through
`Microsoft.Dynamics.Nav.DocumentService.DocumentServiceFactory.CreateService`. That factory
composes a MEF `DirectoryCatalog` over the directory holding
`Microsoft.Dynamics.Nav.DocumentService.dll`, using the file pattern
`*.nav.*DocumentService*.dll`, and picks the export whose `IDocumentServiceMetadata.ServiceType`
matches the requested type, compared case-insensitively.

The only provider Microsoft ships in the public platform artifacts is
`Microsoft.Dynamics.Nav.SharePointOnlineDocumentService.dll`. The two types Microsoft's own test
codeunit 139101 `"Document Service Mgmt Test"` asks for — `DOCUMENTSERVICEMOCK` and
`EMPTYDOCUMENTSERVICEMOCK` — live in internal test binaries. Measured across 25 cached artifacts
from BC 26.0 through 28.4, the string `DOCUMENTSERVICEMOCK` appears in no shipped DLL.

A test that requests one of those service types therefore fails, with BC's own message:

```
NavNCLDotNetInvokeException: A call to ...DocumentServiceFactory.CreateService failed with this
message: <install-dir> The following document service provider could not be found: 'DOCUMENTSERVICEMOCK'.
```

That is the correct result, and the runner keeps it. It names the API, the missing provider and
the directory that was searched. `tests/runner-extras/document-service-session-seed` checks it
from AL, and `AlRunner.Tests/DocumentServiceProviderScopeGuardTests.cs` checks that the runner
ships no provider of its own.

**The runner will not supply a `DOCUMENTSERVICEMOCK` implementation.** Ten of the eleven failing
tests in codeunit 139101 need one, and they divide into two halves: five need the handler to
return a result the test then asserts on, and five assert an exact error string that Microsoft's
AL marks `Comment = 'Text is copied from Mock assembly.'`. A runner-written handler would have to
reproduce those strings out of the test codeunit that checks them, so those five would pass
because the runner matched its own copy, not because it behaved the way Microsoft's mock behaves.
That is the same problem that caused MockImage to be reverted in #1502.

**Bring your own provider.** The extension point is public, so this needs no runner change:

1. Build a .NET assembly whose file name matches `*.nav.*DocumentService*.dll`.
2. Export a type implementing `IDocumentServiceHandler`, decorated
   `[DocumentServiceMetadata("YOURTYPE")]`.
3. Put it in the artifact directory alongside `Microsoft.Dynamics.Nav.DocumentService.dll`.
4. Call `SetServiceType('YOURTYPE')` from your AL.

The factory rescans that directory on every `CreateService` call, so the assembly is picked up
without any further setup.

---

## Behavioural differences — same API, different semantics

These don't crash, but they behave differently from real BC. Tests that assert on
the exact value will see different results.

| AL call | Real BC | al-runner |
|---|---|---|
| `CompanyName()` | Active company name | `""` (fixed default, not currently configurable) |
| `UserId()` | Authenticated user | `"TESTUSER"` (fixed default, not currently configurable) |
| `IsSessionActive(id)` | True while session runs | Always `false` |
| `GuiAllowed()` | False in background sessions | `false` |
| `GetFilter(field)` | Serialised filter expression | Returns serialised filter expression (functional) |
| Field `InitValue` | Applied on `Init()` | Applied — parsed from AL source at pipeline start via `TableInitValueRegistry` |
| `FieldRef.Caption` / `.Name` | Field metadata from schema | Real values for all AL-compiled tables including tableextension fields; `"FieldNN"` stub only for base-app tables not compiled in the current run |
| `Commit()` | Commits current transaction | Establishes a rollback commit-point — see "Transaction semantics" above; not a no-op |
| `FilterGroup(n)` | Scoped filter groups | Not tracked — `FilterGroup()` is a no-op; all filters apply to group 0 |

### Permission-set assignment — answered from `Access Control`, and the session user is SUPER without a row

<a id="permission-set-assignment"></a>

Real BC answers "is this permission set assigned to this user" out of the session's permission
cache (`NavSession.Permissions`), which is built from the tenant's permission tables in SQL. The
runner has no SQL-backed tenant database — `NavUserPermissions.HasRole` and `GetRoles` both need
`NavTenant.Database`, and populating a skeleton `NavTenant` is out of scope (it was measured
breaking ~466 tests) — so `NavSession.Permissions` stays null.

Rather than let every AL path through `NavUserAccountHelper.IsPermissionSetAssigned` NRE
(AlRunner#3039 — it made a valid `User.Modify` fail from inside codeunit 9002's subscriber), the
runner answers the question itself:

| question | Real BC | al-runner |
|---|---|---|
| Is permission set X assigned to user U? | From the permission cache built out of `Access Control` and entitlements | From the `Access Control` (2000000053) rows the run holds — matching User Security ID, Role ID, App ID and Scope, with a blank Company Name meaning every company |
| Is the session's own user SUPER? | Yes on a test tier, because provisioning wrote it a row | Yes — stated directly, consistent with `NavSession.HasExecutePermission*` → `true` and `MaximizePermissions` → no-op |
| Does `Access Control` contain a row for the session user? | Yes | **No** |

The last row is the divergence worth knowing about: the session user is SUPER, but nothing in
`Access Control` says so, because the fact is stated in the runtime rather than seeded as a row.
Any other user is answered purely from rows, in both directions — grant SUPER and `IsSuper`
reads back true, grant something else and it reads back false. Whether the row should be seeded
instead is AlRunner#3176.

Entitlements are not modeled at all, so a permission set that a real tier would report as
assigned *via an entitlement* rather than via `Access Control` reads as not assigned here.
`NavUserAccountHelper.IsUserSuperInAllCompanies` still raises, because its body has no Ncl hop
the runner can rewrite — AlRunner#3174.

### `Record "Time Zone"` — ids follow the HOST, so they are IANA ids on Linux

<a id="time-zone-virtual-table"></a>

The `Time Zone` system virtual table (2000000164) is computed on demand, and BC's own
`TimeZoneDataProvider` computes it by enumerating the host's
`TimeZoneInfo.GetSystemTimeZones()` and numbering the results 1..N. That is the whole of
its implementation — the row set is a property of the machine, not of Business Central.

The runner enumerates the same call. BC in the cloud runs on Windows, so a SaaS tier
reports Windows ids (`W. Europe Standard Time`); the runner runs on Linux, where the same
call reports IANA ids (`Europe/Berlin`). `"No."` is a sequence number over that list, so
the two hosts disagree about the ids **and** about the numbering.

| | Real BC (Windows-hosted) | al-runner (Linux) |
|---|---|---|
| `TimeZone.ID` | `W. Europe Standard Time` | `Europe/Berlin` |
| `TimeZone."No."` | position in the Windows list | position in the IANA list |
| `TimeZone."Display Name"` | Windows display name | the host's display name |

**This is deliberate and permanent.** The alternative — shipping a hardcoded Windows id
list so the answers match a Windows tier — was considered and rejected: fabricating
Windows time zone ids on a Linux host is a silent fake (see
`.claude/rules/loud-failures.md`), it is wrong in a way no test running on this host could
catch, and the list goes stale every time Microsoft revises it. When BC's own answer is a
property of the machine, being faithful to BC's *code* is the honest option.

What this means for a test: assert the **shape** — that `FindSet()` succeeds, that `"No."`
starts at 1 and increments with no gaps, that every row has a non-blank `ID`, and that
`Get(1)` agrees with the first row of `FindSet()`. All of that holds on any host. A test
that asserts a specific zone id is asserting a property of the machine it happens to run
on, and will not hold across hosts in either direction.

Nothing in the corpus or in `tests/runner-extras/` reads this table today, which is why
there is no `expect-divergence` entry for it in `tests/expectations/`: that mode declares
a corpus test that **fails** on the runner, and no such test exists yet. This section is
the record until one does.

---

### `Record Date` — an open-ended `Period Start` filter is answered from a materialised window

<a id="date-virtual-table"></a>

The `Date` system virtual table (2000000007) is computed per request on the service
tier and covers years 1 through 9999 — about 3.6 million `Date`-type rows on its own,
plus the Week, Month, Quarter and Year periods. The runner serves every table from an
in-memory store, so it has to materialise rows, and it cannot materialise all of them.

What it does instead:

- **Nothing is materialised until a read asks for it.** Declaring a `Record Date`
  variable costs no rows at all.
- A read whose `"Period Start"` filter is **closed at both ends** materialises exactly
  the periods inside those bounds, and nothing else. A filter naming one week gets
  about 25 rows, whether that week is in 1850 or 2300. This is safe rather than a
  shortcut: BC's own filter engine excludes every row outside the filter anyway, so a
  narrower store cannot change an answer. A keyed `Get` likewise materialises only the
  day its key names.
- A read that does **not** close both ends — no `"Period Start"` filter at all, an open
  bound, or a filter shape the runner cannot read — is answered from a window of whole
  years, **1900-01-01 to 2099-12-31** by default (86,885 rows across all five period
  types), widened by whichever bound the filter did close. A FlowField whose
  `CalcFormula` source is `Date`, and a `TableRelation` to `Date`, also get the whole
  window: they reach the store without a filter the runner can see.
- The narrowing happens on all **four** request paths a `Record Date` read can take, so
  a filter naming 1850 or 2300 gets real rows whichever one AL uses:

  | AL | `DataAccess` method | request type |
  |---|---|---|
  | `Find` / `FindSet` / `FindFirst` / `FindLast` | `InnerFindAsync` | `FindCacheRequest` |
  | `Count` | `CountAsync` | `CountCacheRequest` |
  | `IsEmpty` | `ExistsAsync` | `ExistsCacheRequest` |
  | `Get(Period Type, Period Start)` | `InternalTryGetByPrimaryKeyAsync` | `PrimaryKeyCacheRequest` |

  This list said "both request paths — find, and `Count` / `IsEmpty`" until #3006.
  `IsEmpty()` has never taken the count path: `RecordImplementation.IsEmptyAsync` calls
  its own `ExistsAsync`, which builds an `ExistsCacheRequest`. Until that fourth guard
  existed, `IsEmpty()` answered `true` for a 1850 range that `Count()` answered `7` for
  on the very next line. The FlowField and `TableRelation` net described above does not
  cover this case, because it materialises the default window and 1850 is outside it.
- Materialising past **500,000 rows** raises `RunnerOutOfScopeException`, naming the
  requested bounds, what is materialised and the cap. It never answers a wider request
  with fewer rows.

The one case the window does not cover is an **open** bound. `SetFilter("Period Start",
'%1..', D)` asks real BC for every period from `D` to 9999-12-31; the runner answers it
from the window — which is why an open bound is one of the shapes that materialises the
whole window rather than something narrower. `FindFirst` on such a filter is unaffected, because its answer sits at
the closed end — and that is the shape production AL uses. Iterating an open-ended range
to the end stops at the window edge instead of year 9999.

Three environment variables move all three numbers for a one-off run:
`AL_RUNNER_DATE_WINDOW_MIN_YEAR`, `AL_RUNNER_DATE_WINDOW_MAX_YEAR`,
`AL_RUNNER_DATE_WINDOW_MAX_ROWS`.

Everything the rows themselves say — the weekday number and name of a `Date` period,
the Monday start and ISO week number of a `Week` period, a computed month end, and
`"Period End"` being a closing date — comes from BC's own arithmetic
(`DateTimeHelper` and `DateDataProvider` in `Microsoft.Dynamics.Nav.Ncl`, called by
reflection), so the runner cannot disagree with the service tier about any of it.

---

### `Record "Windows Language"` — the license and installed-resource columns are chosen values

<a id="windows-language-virtual-table"></a>

The `Windows Language` system virtual table (2000000045) answers sixteen columns. Six come
from the license and four from installed translation resources, and the runner has neither.

| Column | Real BC | al-runner |
|---|---|---|
| `Enabled`, `Globally Enabled`, `Form Enabled`, `Report Enabled`, `Dataport Enabled`, `XMLport Enabled` | `License.HasLanguagePermission(...)` per language | Always **permitted** |
| `STX File Exist`, `ETX File Exist`, `Help File Exist`, `Localization Exist` | Whether translation resources are installed | Always **none** |
| `Language ID`, `Primary Language ID`, `Name`, `Abbreviated Name`, `Primary CodePage`, `Language Tag` | From the platform's culture list | The same — read through BC's own `WindowsLanguageHelper` |

**These are declared divergences, not faithful substitutions**, and the distinction is worth
stating because the runner elsewhere does the opposite. `ALDatabase.ALSid(string)` returns the
empty string because the host has no Windows identity store and BC's *own* not-mapped result
is the empty string — there is a defined BC answer to copy. Here there is none: with no
license BC does not answer `false`, `get_License()` throws. Nothing is being reproduced; a
value is being chosen.

The license columns answer **permitted** because the runner exists so that AL tests run
without a license at all. Answering "not permitted" would gate the very business logic those
tests are there to exercise, turning a missing license into failures that say nothing about
the AL under test.

The installed-resource columns answer **none** for a different reason: the runner installs no
BC translation resources, so that is a true statement about this process. It still differs
from a service tier with localizations installed, so it is recorded here too.

Both are **provisional**. A mockable license is a planned capability; when it arrives, the
license columns become answerable from it. Each sits behind one named seam —
`StubbedLicensePermission` and `StubbedLocalizationResources` in
`AlRunner/Patches/RecordPatches.WindowsLanguageVirtualTable.cs` — so that change is one place,
and `tests/runner-extras/windows-language-license-stub` asserts the current answers so they
cannot move quietly.

No `expect-divergence` entry accompanies this: that mode declares a corpus test that fails on
the runner, and the corpus test for this table deliberately asserts only the six columns that
do have a source. This section is the record.

---

### `Record "Object Metadata"` — the rows are synthesised and the payload columns read blank

<a id="object-metadata-system-table"></a>

`Object Metadata` (2000000071) is not a virtual table. It is one of the 43 ids in BC's own
`SystemTables.ApplicationDatabaseTables`, a real SQL table in the *application* database that
publishing writes into and Ncl's `ObjectMetadataStorage` reads back with plain SQL. Its content
is the compiled metadata of the application-database system tables — not an object inventory;
the table's own AL summary in `System.app` says the inventory role "is now taken by
[Application Object Metadata]".

The runner has no application database and publishes nothing into one, so:

| Column | Real BC | al-runner |
|---|---|---|
| `Object Type`, `Object ID` | One row per application-database system table | Synthesised from BC's own `SystemTables.ApplicationDatabaseTables` |
| `Emit Version` | The tier's compiler emit version | BC's own `NavEnvironment.Instance.EmitVersion` |
| `Metadata`, `User Code`, `User AL Code`, `Symbol Reference` (BLOB) | The published metadata payload | Always **empty** |
| `Metadata Version`, `Hash`, `Object Subtype`, `Has Subscribers`, `Schema Hash` | Derived from that payload | Always **`0` / empty / `false`** |

**The row set is an upper bound derived from Microsoft's code, not a service-tier-confirmed
equality.** The selecting predicate is the insert in
`InPlacePublisher.UpsertIntoMetadataStorageImpl`: the System app's own table objects,
intersected with `ApplicationDatabaseTables`, minus ids with static metadata XML (which is only
2000001071, so a no-op here). Enumerating the `.al` sources inside `System.app`, all 43 ids have
a table object on both BC 27.0 and 28.1. What is *not* established is whether the 11 ids
declared `ObsoleteState = Removed` get rows on a real tier; if one ever reports fewer than 43,
that is where the difference will be.

An earlier version of this section said the runner and a real tier "cannot disagree about which
ids belong". That was wrong, and worth recording as a mistake to avoid repeating: it rested on
Microsoft's `CleanupObjectMetadataFromNonApplicationDatabaseTables` migration, whose
`DELETE ... WHERE [Object Type] <> 1 OR [Object ID] NOT IN (...)` bounds the retained set from
*above* and does not create a row for each id. A `DELETE` proves ⊆, never =.

**No service tier has adjudicated any of this**, and not for want of trying. The BC-behaviour
half belongs in the al-language corpus and cannot be expressed there: the corpus app targets
Cloud, the table is `Scope = OnPrem`, so `Record "Object Metadata"` fails `AL0296` at compile,
and the `RecordRef` route is refused at *runtime* by `NavRecordRef.CheckIsOpenAllowed` —
`"You cannot open record 2000000071 from a RecordRef data type when you are using target Cloud."`
2000000071 is in `SystemTables.InternalTables`, and the escape hatch
`SystemTables.OnPremSystemTableRecordRefAllowed` is only `{ 2000000187, 2000000188 }`. Corpus PR
[#153](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/153) is closed with
that evidence, from
[run 33968379281](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/actions/runs/33968379281),
where all 8 BC legs failed on that message before reaching a single assertion. Settling the
remainder needs an OnPrem-target app in the corpus, or Microsoft's `Tests-SINGLESERVER` bucket,
which is OnPrem-target and reads this table directly.

The payload columns are a **declared divergence** — there is nothing to reproduce, because
nothing was ever published. Per `.claude/rules/loud-failures.md` those nine columns should refuse
by name rather than read blank; that needs a per-(table, field) blob-read seam on the shared
`TempTableDataProvider` path which does not exist yet, and **issue #2771** tracks it. Throwing at
row-build time instead is not an option — it would take out `FindSet` / `FindLast` / `Count` as
well, which is the bug (#2519) this table's support closed.

`tests/runner-extras/object-metadata-system-table` asserts the runner-side behaviour so it cannot
move quietly. It deliberately uses only ids that are live table objects, so it does not encode
the open `ObsoleteState = Removed` question as settled in either direction.

**When the runner cannot answer at all, it refuses and says so as a gap, not as a scope
boundary.** Twelve preconditions guard this table — BC's `SystemTables` type and its
`ApplicationDatabaseTables` list, `NavEnvironment.Instance.EmitVersion`, the `"Object Type"`
option string, the in-memory data provider, and `TempTableDataProvider.primaryTree`. If one of
them is not the shape the runner expects, it raises `RunnerOutOfScopeException` with reason
anchor `not-yet-implemented` and a link back to this section. None of them means the surface is
out of scope: this table is implemented, and a refusal here is a bug report about the runner
(#2894). The anchor matters at runtime as well as on the page — an AL `[TryFunction]` traps a
*permanent* refusal into `false` and lets a `not-yet-implemented` one tear through, so a shape
gap can never read as a clean `if not TryX()`. `AlRunner.Tests/ObjectMetadataSystemTableRefusalTests.cs`
pins the contract; `AlRunner.Tests/ObjectMetadataProviderRowProbeTests.cs` drives the two
`primaryTree` cases end to end.

**Synthesis never overwrites restored rows, and never guesses whether there are any.** Because
2000000071 is a real SQL table, a `--test-data` backup can genuinely carry rows for it, so the
on-demand loader runs first and the populator does nothing when the store already holds a row.
Deciding that means reading BC's private `TempTableDataProvider.primaryTree` by reflection. A
*null* tree is BC's own "no row was ever inserted" and synthesis proceeds; the field being
**absent**, or holding something that cannot be enumerated, means BC's private layout moved and
the runner cannot tell the two apart — so it refuses with an `out-of-scope:` failure naming the
member rather than synthesising over rows it cannot see (#2786). That refusal is unreachable on
every BC version the runner supports; it exists so a future one cannot silently disable the
precedence rule.

**Synthesis is also held off while a load is still owed.** Hydrating some other table runs BC's
own metadata and NavValue construction, which can reach a `Record` of 2000000071 and materialise
its storage from *inside* that hydration — where a load of its own would recurse and so is
refused. The populator used to run against the empty store it had just been handed, claim the
once-per-provider flag and synthesise; the backup's real rows then had nowhere to go. It now
leaves such a store alone until the next touch outside a hydration loads it, so the precedence
rule holds on that path too (#2877). The nested caller sees an **empty** Object Metadata store
for that moment — deliberately, because the alternative is synthesising rows that would have to
be withdrawn, and rows a caller has already read cannot be withdrawn. Nothing is lost when the
backup does not offer the table: the load comes back with no rows, and the populate that follows
synthesises exactly as it always did.

---

### `Record "Object"` — the rows are the runner's own object inventory, and most columns read blank

<a id="object-system-table"></a>

`Object` (2000000001) is the other half of the table relation `Object Metadata`."Object ID"
declares (`TableRelation = Object.ID WHERE(Type = FIELD("Object Type"))`). It is not a virtual
table either: it is the legacy object registry, one of the same 43 ids in
`SystemTables.ApplicationDatabaseTables`, and `System.app`'s own declaration calls it the
"legacy object metadata storage system superseded by Application Object Metadata table"
(`Scope = OnPrem`, `ObsoleteState = Pending`, keyed on `Type` + `"Company Name"` + `ID`).

Its rows are an object *inventory* rather than a fixed id list, so the runner projects the one
inventory it already has — `EnumerateKnownAlObjects`, the source `AllObj` (2000000038) and
`AllObjWithCaption` (2000000058) are answered from — into this table's column shape. That is
deliberate: two registries could disagree about which objects exist, one cannot.

| Column | Real BC | al-runner |
|---|---|---|
| `Type`, `ID`, `Name` | One row per application object | Projected from the runner's own object inventory |
| `"Company Name"` | The per-company object registry's company | Always **blank** — every object the runner knows is company-independent |
| `Modified`, `Compiled`, `"BLOB Reference"`, `"BLOB Size"`, `"DBM Table No."`, `Date`, `Time`, `"Version List"`, `Caption`, `Locked`, `"Locked By"` | What the classic registry stored | Always **`false` / `0` / empty** |

An object kind this table's own `Type` option string cannot name — `Enum`, `Interface`,
`PermissionSet`, every `*extension` kind — gets **no row** rather than an invented ordinal. The
option is `TableData,Table,,Report,,Codeunit,XMLport,MenuSuite,Page,Query,System,FieldNumber`,
a strict subset of `AllObj`'s, so `Object` legitimately lists fewer kinds than `AllObj` does.

**Four of its columns are `OemText`, not the `Text[n]` they are declared as, and that is BC's
decision rather than the runner's.**
`Microsoft.Dynamics.Nav.CodeAnalysis.Emit.CodeGenerator.IsOemTextFieldOnObjectTable` is a
table-id check against 2000000001 and a switch over field numbers 2, 4, 12 and 50; `GetFieldType`
calls it and substitutes `NavTypeKind.OemText`, so the emitted IL calls
`ValidateExpectedType(fieldNo, NavType.OemText)` while `SymbolReference.json` and the shipped
`.al` both say `Text[n]`. Read off the shipped compiler on BC 27.5.46862.48827 and BC
28.1.49838.53910 — identical bodies. `RecordPatches.NclMetaTableBuilder.MapNavType` mirrors the
carve-out; without it every AL read of `"Company Name"` / `Name` / `"Version List"` /
`"Locked By"` throws `NavObjectDefinitionChangedException` ("old type: OemText, new type: Text")
instead of returning anything.

**No service tier has adjudicated the row set yet — but one now can, and is being asked.** Both
routes a *Cloud-target* app has are closed: `Record "Object"` fails `AL0296` because the table is
`Scope = OnPrem`, and the `RecordRef` route is refused at runtime by
`NavRecordRef.CheckIsOpenAllowed`, because 2000000001 is in `SystemTables.InternalTables` (100
ids on BC 28.1) and the escape hatch `SystemTables.OnPremSystemTableRecordRefAllowed` is only
`{ 2000000187, 2000000188 }`.

Both closures are decided by the **calling app's compilation target and nothing else** —
`NavRecordRef.IsOpenAllowed` returns `true` outright for an OnPrem target without consulting
`InternalTables` at all — so an OnPrem-target app is refused neither, and needs no `RecordRef`
in the first place. The corpus gained exactly such an app in corpus PR
[#179](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/179)
(`tests/al-language-onprem`), and corpus PRs #179 and #187 have since adjudicated two other
`Scope = OnPrem` system tables on all eight **OnPrem** legs. Corpus PR
[#197](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/197) asks the same
question for this table; **issue #3071** tracks reconciling the runner with whatever comes back.

Two measurements to keep apart, because this repository conflated them for a while. The
*refusal* of a Cloud-target `RecordRef.Open` was originally measured on the **sibling** id —
corpus PR [#153](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/153) was
withdrawn after all 8 BC legs of
[run 33968379281](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/actions/runs/33968379281)
refused 2000000071, and 2000000001 followed by *membership in the same set*, not by its own
measurement. `AlRunner.Tests/RecordRefCompilationTargetScopeTests` now exercises BC's own
unreplaced `CheckIsOpenAllowed` body against 2000000001 directly, in both target directions, so
that inference is retired. The *row set* is the separate question, and only a tier answers it.

Until it does, what the runner-extras suite asserts is the *projection* — a claim about the
runner — not what a tier's `Object` table holds. **issue #2834** tracks the missing upstream
coverage for this whole area.

The blank columns are a **declared divergence** for the same reason Object Metadata's payload
columns are: there is no registry behind them to reproduce. Per
`.claude/rules/loud-failures.md` they should refuse by name rather than read blank, which needs
the per-(table, field) read seam **issue #2771** tracks — this table is its second consumer.
`Caption` is in the blank list even though the shared inventory does carry a caption, because
whether this legacy table's field 20 holds the object's AL caption is a BC claim no tier here
can adjudicate; **issue #2839** tracks it rather than guessing.

A `--test-data` backup's real rows take precedence over the projection, and that precedence is
decided from what the backup **actually loaded** rather than from what the in-memory store
happens to hold (#2875). Reading the store was the earlier design and it could not work: an
install-baseline restore replays rows the projection itself wrote into a brand-new provider, so
the projection read its own stale output as somebody else's and stopped topping the table up.
The on-demand load now records the tables it put rows into, and the projection stands down for
this table only when that record names it. Its companion is in the capture: while the projection
owns the rows, table 2000000001 is left out of the install baseline entirely — the same treatment
the self-populating virtual tables get (#2272), and for the same reason, since the dispatch
branch re-derives the projection on every access. With nothing captured there is nothing to
replay, so the ambiguity is gone rather than narrowed.

One case in the same family is **not** closed here and is reported rather than silent: a store
published by a *nested* materialisation owes a `--test-data` load that could not run there, and
the projection fills it before that debt is settled, so the settle writes the load off. Object
Metadata's dispatch branch holds its populate off until the debt is settled; `Object`'s does not.
**issue #2877** covers the deferred-load mechanism.

`tests/runner-extras/object-system-table` asserts the runner-side behaviour so it cannot move
quietly.

---

## Per-BC-minor engine variants: granularity is per MINOR, not per exact build

Every released `al-runner` binary used to be compiled against exactly one BC minor's
reference assemblies (`Microsoft.Dynamics.Nav.CodeAnalysis` etc.), regardless of which
`--bc-version` a user actually ran it against — running a mismatched minor could NRE
deep inside BC's own code (#2020). The package now ships one thin engine variant per
[`.github/bc-versions.txt`](../.github/bc-versions.txt) entry and swaps to the matching
one automatically at startup (#2024 item 3, #2027) — a large improvement, but **not
"any BC version works."**

**`Microsoft.Dynamics.Nav.CodeAnalysis` is strong-named per BUILD, not per minor.**
Two separate builds of the same BC minor ship different `CodeAnalysis` assembly
versions (e.g. 28.1.49838.50794 → 17.0.36.40629 vs. 28.1.49838.53220 → 17.0.39.53543),
and the runner's variant was compiled against whichever build was newest at PACK TIME.
The strong-named reference does not tolerate that skew — a mismatched build fails loud
at startup with `FileLoadException` before any test runs, not silently.

So concretely: if the shipped `28.3` variant was built against build `28.3.52162.53954`
and you have a *different* `28.3.x` build cached locally (a real scenario — Microsoft
regularly ships more than one build per minor, and can withdraw one after the fact —
see #2012), the runner prints a loud, explicit warning naming both versions and still
attempts the run; it may or may not actually load, depending on how far that specific
skew reaches. This is the one case per-minor variants don't close — only shipping a
variant per exact 4-part build would, and that's a materially larger package for a
combination that's uncommon in practice.

Eight correctly-matched minors instead of one is the real, measured improvement here.
Treat "shipped variant" and "exact user build" as related but distinct guarantees.

---

## BC 26

Not supported. The runner is tested against **BC 27.0 and up** — see
`.github/bc-versions.txt` for the exact matrix.

This is not a statement about the runner's capability. The canonical test corpus
(`tests/al-language`) declares in its own `app.json`:

```
platform:     27.0.0.0
dependencies: System Application 27.5.0.0
              Base Application   27.5.0.0
```

Those are AL *minimum* versions, so a BC 26 provisioning — platform 26.0, System
and Base Application 26.x — is rejected by the compiler before a single test
runs. The corpus is a read-only upstream submodule pinned to 27.5-era System
Application surface, so lowering that floor is neither this repo's call nor free:
it would mean deleting the coverage that depends on it.

"The runner supports BC 26" and "the corpus runs on BC 26" are therefore separate
claims, and only the second one is blocked by the above. Demonstrating the first
would need a small suite with its own BC 26-compatible `app.json`, not the corpus.

Three interface shapes cannot be bridged by reflection, because the runner
implements or constructs them and the C# compiler must agree with the reference
assembly before any code runs:

| Shape | BC 26 | BC 27+ |
|---|---|---|
| `ITestPage` part accessor | `ITestPage GetPage(int)` | `ITestPart GetPart(int)` (`ITestPart` does not exist on 26) |
| `INCLObjectXmlMetadataLoader.GetExtensionDeltasForAppObject` | returns `NavAppObjectMetadataTimestampRecord<T>` | returns bare `T` |
| `NCLObjectXmlMetadata` ctor | extra leading `long timestamp` | no timestamp |

Commit `0983df71` handled all three with version-derived compile constants and is
the reference if a future BC version needs the same treatment. The constants were
removed again when BC 26 was dropped, because nothing in CI could exercise them
and an unexercised `#if` branch rots silently.

One further known difference, reached but never resolved: `NavTenant
.GetObjectAccessIntent` takes `(session, objectType, objectId)` on BC 27+ but
`(objectType, objectId)` on 26, and the Cecil pass looks it up by arity. That is
the *first* failure past compilation, not necessarily the last — the pass aborts
there, so everything behind it is unmeasured.

---

<a id="bc-shape-gaps"></a>

## BC shape gaps — the runner could not read BC's internals

A **shape gap** is not a limitation of the runner's scope, and it is not a feature nobody has
built. It is a bug report: the runner reflects on a private field, a static type or an internal
property inside BC's own assemblies, and on the BC build in front of it that member is not
there, or holds something the runner cannot interpret. The surface is in scope, the code that
serves it is written, and the read failed.

The runner raises `AlRunner.Infrastructure.BcShapeGapException` for exactly that, and the
message names the surface, the member and why the read mattered:

```
bc-shape-gap: Object Metadata (system table 2000000071) — TempTableDataProvider.primaryTree:
field not found — the runner cannot tell a store BC never inserted into from one --test-data
already filled, and synthesising rows would silently shadow the restored ones —
see docs/limitations.md#bc-shape-gaps
```

**If you see one, the first question is which BC version produced it.** A shape gap is a
property of the build on disk rather than of the runner, so it can fire on one BC minor and not
another in the same matrix run. Report it with the BC version, the member named in the message
and the AL that reached it.

### Three refusals, three different meanings

| the runner says | means | AL `[TryFunction]` | AL `asserterror` | can an `expect-oos` entry declare it expected? |
|---|---|---|---|---|
| `out-of-scope:` + a `docs/scope.md` anchor | permanently out of scope — SMTP, HTTP egress, printing | traps it into `false` | catches it | yes |
| `out-of-scope:` + `not-yet-implemented` | in scope, not built yet | tears through | catches it | yes |
| `bc-shape-gap:` | the runner could not read BC's internals | tears through | **tears through** | **no** |

The first row traps because it is faithful: real BC, in an environment that also lacks the
surface, raises a trappable error there, so `false` is BC's own answer. Neither of the other two
has a BC outcome to be faithful to — real BC answers, and the runner does not — so trapping
either would turn a gap into a green test that lies.

A shape gap goes one step further than `not-yet-implemented` and escapes `asserterror` as well,
because catching it does not merely hide the gap, it **inverts a result**: on real BC the
statement inside the `asserterror` succeeds, so the `asserterror` fails; a runner that swallows
the gap makes it pass.

And it can never be absorbed by an `expect-oos` entry. That mode declares a permanent scope
boundary, and a BC-layout regression is not one — see [docs/expectations.md](expectations.md).
`expect-fail-known-gap` still applies, with an open issue, once someone has written the gap
down.

The convention was settled in
[#2946](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2946), which was filed
because four readers of one private BC structure raised three different exception types between
them. `AlRunner/Infrastructure/BcShapeGapException.cs` carries the full derivation, including
why it is a separate type rather than a third reason anchor.

Two shape gaps live on the TestPage surface
([#2999](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2999)), and both are
reached only when BC's own metadata is not the shape the runner reads:

- a **SubPageLink whose `FilterType`** is outside `FIELD`/`CONST`/`FILTER`. Measured on BC
  28.1's `Microsoft.Dynamics.Nav.Types.dll`, that enum declares exactly those three, and the
  runner's own dependency-metadata emitter writes only those three spellings — so a fourth
  value can only have come from BC's compiled page metadata.
- **whether a source-table field declares an `OnLookup`**, when reflection cannot resolve
  `NCLMetaField`'s private `EventTriggerDataValue` / `EventTriggerData.LookupHandler` backing
  fields. A read that *succeeds* and says the field declares no trigger is a different outcome
  and stays a permanent refusal, because that lookup would come from a `TableRelation`.

The **write** side of those same two backing fields refuses too
([#3026](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/3026)). Installing a
table's AL `OnValidate` / `OnLookup` field trigger pokes
`NCLMetaField.EventTriggerData.ValidateHandler` / `.LookupHandler`; if either cannot be
resolved, the runner now names it and stops, instead of skipping the install and reporting the
table as wired. The skip was the worse half of the pair: nothing was printed, the trigger never
fired, and AL that depended on it *passed*. The same applies to the tableextension
`OnBeforeValidate` / `OnAfterValidate` handler lists and to the types
`BuildFieldTriggerHandler` wraps a trigger method in.

Two further skips on those same rewritten lines were **not** shape gaps and are covered above
under [runtime shape gaps](#runtime-shape-gaps): an unresolvable field number and an
unsupported trigger return type
([#3048](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/3048)). They refuse
with `out-of-scope:` + `not-yet-implemented` rather than `bc-shape-gap:`, because neither is a
property of the BC build on disk.

The refusal is proportional — it fires only for a table and field the runner has a handler in
hand for. A table that declares no field trigger still wires and reports success on such a
build, because nothing was skipped for it. The two exceptions are
`FieldTriggerHandlerAttribute` and `FieldTriggerType`, which are what the scan itself reads: on
a build missing either, the runner cannot tell whether *any* table declares a trigger, so it
refuses for every table rather than let the whole bundle's field triggers go quiet.

**Where a field-trigger refusal surfaces depends on which member moved**
([#3047](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/3047)). A proportional
guard fires only for the table and field it could not install, so it fails the test that touched
that table and names both. The two scan-type guards refuse for *every* table, and
`RecordPatches.WireFieldTriggerHandlersAll` runs once at bundle load
(`BcRuntime.SetTestAssembly`, and `Program.cs` for a server-mode reload) — so on a build missing
`FieldTriggerHandlerAttribute` or `FieldTriggerType` the refusal is a **run-level abort at bundle
load**, not an attributable single-test failure. Nothing runs, and the message names the member.
That is the intended trade: the alternative is every field trigger in the bundle going quiet
while the suite still reports green.

## Known gaps — in scope but not yet implemented

These are not architectural limits. They can be fixed; report them at
https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues.

<a id="runtime-shape-gaps"></a>

- **Runtime shape gaps outside the virtual tables — the runner refuses rather than answering
  a shape it cannot produce.** 12 further guards raise `RunnerOutOfScopeException` with the
  reason anchor `not-yet-implemented`, so an AL `[TryFunction]` cannot absorb one into `false`
  ([#2966](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2966)). The number
  counts refusal **call sites**, which is the rule the original nine were counted under; it is
  now asserted against the code by
  `AlRunner.Tests/FieldTriggerShapeGapCallSiteTests.cs`, because it read "Nine" for as long as
  it took [#3048](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/3048) to add
  three more and nothing checked it
  ([#3047](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/3047)):
  - the **User Property** row BC writes alongside every `User` insert, when the record under
    insert carries no session, when table 2000000121 has no metadata in this run, or when the
    metatable states no field of the name the writer needs
    (`AlRunner/Patches/UserTableTriggerPatches.cs`);
  - the **per-codeunit install baseline**, when a table is backed by something other than
    `TempTableDataProvider` and so cannot be snapshotted or restored across a codeunit
    boundary (`RecordPatches.InstallBaseline.cs`);
  - a **[ModalPageHandler]** asked for a form handle the runner's own form registry does not
    hold (`RunnerTestClientSession.cs`), and modal/page dispatch handed a null test-execution
    context or request (`RunnerModalDispatch.cs`);
  - **report construction**, when the runner cannot build the report object at all to run it
    or its request page (`NavReportSync.cs`);
  - **AL field-trigger installation**, in two shapes across three call sites that used to be
    skipped in silence
    ([#3048](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/3048)) — a field
    the runner's own metatable does not carry although the runner's own emitted AL declares a
    trigger for it (refused at both ways `NCLMetaTable.GetFieldByNo` can decline it: it throws,
    or it returns null, which BC's own body cannot do on any supported build), and a trigger
    method whose return type is neither `void` nor `ValueTask`, which BC's
    `FieldTriggerHandler<T>` has no constructor for
    (`AlRunner/Patches/FieldTriggerInstallGaps.cs`). Neither is a BC shape gap: both sides of
    each disagreement are the runner's own, so "which BC version produced this?" has no answer.
    Both used to leave `WireFieldTriggerHandlers` reporting the table as wired, so the trigger
    never fired and AL depending on it passed anyway.

  Real BC does all of these, so each is the runner failing to keep up rather than a surface BC
  also lacks — which is the test for whether a refusal may cite `docs/scope.md` at all.

<a id="testpage-shape-gaps"></a>

- **TestPage shape gaps — the runner refuses rather than driving a page it could not build.**
  Fourteen guards in `AlRunner/Patches/MockTestPage.cs` and
  `AlRunner/Patches/RunnerPageInstance.cs` raise `RunnerOutOfScopeException` with the reason
  anchor `not-yet-implemented`, so neither an AL `[TryFunction]` nor a silent default can
  absorb one
  ([#2999](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2999)):
  - a **subpage part** the runner could not resolve to a part definition, could not own
    (the hosting page was built without an `ITreeObject` owner), or could not drive live;
  - a **`SubPageLink`** whose part field or whose `field(...)` parent field the runner's own
    dependency-metadata reconstruction could not resolve to a field number;
  - a **control** bound neither to a source-table field nor to a page variable the runner could
    resolve, and an **Option-bound control** carrying no option metadata;
  - **`OnLookup` / `OnDrillDown`**, when no AL page object was built for the page at all;
  - a **`Visible`/`Editable`/`Enabled` expression** that evaluated to a non-Boolean or that the
    runner's expression evaluator could not evaluate
    ([#2596](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2596));
  - two emitted **trigger methods** on one object hashing to the same member id, so the runner
    cannot tell which one the test asked for.

  Real BC drives every one of these, so each is the runner failing to keep up rather than a
  surface BC also lacks — which is the test for whether a refusal may cite `docs/scope.md` at
  all. Eight further refusals in those same two files genuinely are permanent and keep their
  `docs/scope.md` citation: a page with no `SourceTable`, an `OnQueryClosePage` veto on the
  explicit `TestPage.Close()` path, a lookup that could only come from a `TableRelation`, and
  the AL-authoring errors real BC also raises. The veto refusal is scoped to that path: a page
  the PLATFORM closes for a `[ModalPageHandler]` / `[PageHandler]` reproduces what a real
  service tier does with a veto instead — no error reaches the test, `OnClosePage` still fires,
  and `RunModal()` reports `Action::None`
  ([#3050](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/3050)).

<a id="virtual-table-shape-gaps"></a>

- **System virtual tables — the runner refuses rather than answering a shape it cannot read.**
  AllObj, AllObjWithCaption, All Profile, Integer, Field, Table/Page/CodeUnit/Report Metadata,
  Report Data Items, Report Layout List, Page Control Field, Metadata and Aggregate Permission
  Set, Feature Key, Time Zone and Windows Language are all populated in-memory by
  `AlRunner/Patches/RecordPatches.*VirtualTable.cs`. Each populator reads something it does not
  own — the runner's in-memory store, the artifact's own metatable and option strings, or
  Microsoft's own data provider — and when what it finds is not the shape it drives, it raises
  `RunnerOutOfScopeException` naming the member that moved instead of answering with rows it
  guessed at. An option ordinal is a stored column value, so a guess there mis-keys every row
  it writes and no test can see it.

  These refusals are gaps, not scope boundaries: every one of these tables answers on a real
  service tier. They carry the reason anchor `not-yet-implemented`, which is what stops an AL
  `[TryFunction]` from trapping one into `false`
  ([#2945](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2945)). Time Zone
  and Windows Language have documented *divergences* as well — see their own sections above —
  and those are answers the runner gives on purpose, not refusals.
- **FilterGroup** — `Rec.FilterGroup(n)` has no effect; filters always apply to group 0.
- **Query aggregation** — a query column with `Method = Sum`/`Count`/`Average`/`Min`/`Max`
  does not aggregate or group rows; it silently returns each row's own unaggregated value.
  Tracked in [#2137](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2137).
- **`--test-data` hydration coverage** — `--test-data` loads the in-memory database from the
  BC backup shipped in the artifact cache (issue
  [#2258](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2258)). A table is
  read the **first time the run touches it**, not up front
  ([#2262](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2262)), because the
  install baseline is re-inserted at every test boundary and so its size is a cost paid per
  boundary rather than per run. Coverage does not extend to everything, and what it does not
  cover it **reports** — a skipped or refused table is named on stderr with the reason, never
  silently emptied.

  Independently of the flag, a failure on a table that holds **no rows** gets a one-line
  `[test-data]` explanation printed under it, naming the table
  ([#2240](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2240)). Two known
  limits on when it appears, both deliberate — a wrong explanation is worse than none:
  - It needs the failure to name a table. `Record.Get` raising, and `TestField` failing, both
    do; an ordinary `Error(...)` (which is what a failed `Assert` compiles to) does not, and
    is left alone.
  - The table has to be genuinely empty. A setup singleton whose row exists but was never
    configured — `Purchases & Payables Setup` is the shipped example: the install seed inserts
    one blank row, so `Get()` succeeds and `TestField` then fails — is NOT explained, because
    "the row is there but blank" is a weaker inference than "there are no rows at all".
    Tracked in [#2277](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2277).

  What is and is not hydrated by the flag itself:
  - Table-extension (`$ext`) fields ARE merged into the base record
    ([#2261](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2261)), with one
    declared exception: a companion column owned by an app **outside this run's app closure**
    has no AL field in this run's schema, so it is dropped and counted in the summary.
  - Date, DateTime, Time and DateFormula values are rebuilt
    ([#2259](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2259)), as are
    Blob, Media, MediaSet, RecordId and Duration
    ([#2270](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2270)) and a DB
    NULL in any column type
    ([#2268](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2268)). Each
    mirrors BC's own SQL-cell reader case for case.
  - TableFilter values are still refused: BC's reader has a case for them, but no table in the
    shipped demo data stores one, so the shape the backup reader emits has never been measured
    and the codec will not invent it —
    [#2271](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2271).
  - BC's system columns (`SystemId`, `SystemCreatedAt`, …) are not hydrated —
    [#2260](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2260).
  - A table whose AL name is declared by two installed apps in the same company is refused
    rather than guessed at —
    [#2264](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2264).
  - A table whose storage is first created from **inside another table's hydration** cannot be
    loaded there — a nested load would recurse — so the load is deferred to the next touch
    outside a hydration and runs into that same storage
    ([#2877](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2877)). It used to
    be dropped instead, silently and for the rest of the run, because storage being present is
    what the on-demand policy reads as "already loaded". If something wrote into that storage in
    the meantime, the deferred load is **written off** rather than stacked on top of those rows,
    and the table is named on stderr with the reason; `--test-data`'s per-table outcome
    distinguishes "created during another table's hydration" from "never touched".

  Measured on BC 28.1's W1 CRONUS backup with the Base Application / System Application /
  Business Foundation closure: **39,231 rows across 344 tables** hydrated; 12 refused,
  1 ambiguous by name, 293 companion columns dropped for apps outside the closure. All 12
  remaining refusals are a bare column the backup holds that this build's AL table has no
  field for ([#2273](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2273)) —
  none is a value type any more.

---

## When to use the full BC pipeline instead

al-runner targets broad AL language compatibility. If AL code compiles but
fails to run, that is a gap to report, not a reason to restructure your code.

The hard exceptions — things that require the BC service tier by architecture —
are listed above. For those, test in the full pipeline:

- Real company or setup data being present
- Parallel sessions running concurrently
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
