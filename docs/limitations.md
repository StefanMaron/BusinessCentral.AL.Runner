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
  the use case.
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
- `IncludeSender = true` — the sender codeunit instance is prepended as the first argument.
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
- `Page.Run()` is a no-op. `Page.RunModal()` dispatches to `[ModalPageHandler]` if
  registered, otherwise throws — both the page-variable form
  (`P.SetRecord(Rec); P.RunModal();`) and the static-by-id forms
  (`Page.RunModal(id, Record)`, `Page.RunModal(Page::"X", Record)`, and Base App
  `Codeunit 700 "Page Management"` code that routes through them). The static
  `Page.RunModal(0, Record)` form, which real BC resolves via the record table's
  `LookupPageId`, is not yet implemented and throws
  [#1918](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/1918); pass an
  explicit page id in the meantime.
- Request pages can be handled via `[RequestPageHandler]`, but this is handler dispatch
  only, not real request-page rendering.
- Report variables support `Run()`, `RunRequestPage()`, `SetTableView()`, and
  helper procedures. Report triggers execute: `OnPreReport`, `OnPreDataItem`,
  `OnAfterGetRecord` (once per row in the in-memory table), `OnPostDataItem`, and
  `OnPostReport`. `Run()` drives BC's own data-item loop, so `SetTableView(Rec)`
  constrains the matching data item to the applied view, and `DataItemTableView`,
  `DataItemLink`, nested data items and `CurrReport.Skip`/`Break` behave as the
  runtime engine defines them. Report layout/rendering is still not available.
- The static `Report.Run(id[, requestWindow[, systemPrinter[, record]]])` /
  `Report.RunModal(id, ...)` forms (called on the `Report` codeunit-like object, without
  first declaring a report variable) execute the report the same way the report-variable
  form does — construct the report from its id, then run the same trigger lifecycle.
  `requestWindow` / `systemPrinter` are accepted but not acted on: no dialog is ever raised
  from `Run`/`RunModal` (request pages are handler dispatch only, see above); a report that
  needs its request page's `[RequestPageHandler]` to fire should call the static/instance
  `RunRequestPage()` explicitly. The `Report.Run(ReportRunOptions)` overload is not
  implemented and throws `out-of-scope: static NavReport.Run`.

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

### Query — joins and dataset export work; aggregation does not

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

## Known gaps — in scope but not yet implemented

These are not architectural limits. They can be fixed; report them at
https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues.

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
  silently emptied:
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
