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
  access succeeds unconditionally.
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

### Event subscribers — partial support

The runner does dispatch event subscribers. `RunEvent()` calls are rewritten to
`AlCompat.FireEvent(publisherCodeunitId, eventName)`, which scans the compiled assembly
for `[NavEventSubscriber]` methods at startup and calls matching subscribers.

**What works:** custom `[IntegrationEvent]` / `[BusinessEvent]` publishers with
zero-argument subscribers.

**What does not work:** subscribers that receive event arguments (e.g. `var Rec: Record X`,
`Sender: Codeunit X`). The parameters are passed as `null` because the BC-generated
event method does not expose them to the dispatch call. A subscriber that reads or
modifies its `Rec` parameter will see a null reference and either crash or silently do
nothing.

The practical effect: a test that calls `Rec.Modify()` and then asserts on state that is
normally set by an `[EventSubscriber]` on `OnAfterModify` will see the subscriber called
but unable to modify any record — producing a **silent false positive**.

Always run the full pipeline after al-runner for code whose correctness depends on
event subscriber side-effects.

### No UI rendering

Pages are not rendered. There is no layout engine, no field visibility evaluation, and
no report dataset. `TestPage` provides expanded field access, navigation, and handler
dispatch, and report/request-page variables support a limited standalone surface, but:

- Field `Visible`, `Enabled`, and `Editable` are not evaluated against real page metadata.
- `TestPage` methods like `GoToRecord`, `Next`, `New`, `GetPart`, and filter reads are
  mock-backed rather than UI-backed.
- Actions beyond OK/Cancel/Close are not bound to real page action triggers.
- `Page.Run()` is a no-op. `Page.RunModal()` dispatches to `[ModalPageHandler]` if
  registered, otherwise throws.
- Request pages can be handled via `[RequestPageHandler]`, but this is handler dispatch
  only, not real request-page rendering.
- Report variables support `Run()`, `RunRequestPage()`, `SetTableView()`, and
  helper procedures. Report triggers execute: `OnPreReport`, `OnPreDataItem`,
  `OnAfterGetRecord` (once per row in the in-memory table), `OnPostDataItem`, and
  `OnPostReport`. Report layout/rendering is still not available.

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
| Field `InitValue` | Applied on `Init()` | Not applied — type default only |
| `FieldRef.Caption` / `.Name` | Field metadata from schema | Real values for all AL-compiled tables including tableextension fields; `"FieldNN"` stub only for base-app tables not compiled in the current run |
| `Commit()` | Commits current transaction | No-op |

---

## When to use the full BC pipeline instead

al-runner targets broad AL language compatibility. If AL code compiles but
fails to run, that is a gap to report, not a reason to restructure your code.

The hard exceptions — things that require the BC service tier by architecture —
are listed above. For those, test in the full pipeline:

- Event subscribers that pass data via `var Rec` or `Sender` parameters
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
