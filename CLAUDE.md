# BusinessCentral.AL.Runner — Agent Working Document

## Vision

Run Business Central AL unit tests in milliseconds, with no BC service tier, no
Docker, no SQL Server, and no license. The goal is broad AL language compatibility —
targeting the full functional AL surface so that any codeunit that can run without
the BC service tier can be tested here.

A small number of hard architectural limits exist (parallel session contracts,
transaction isolation, service-tier rendering, HTTP). Everything else is a gap to
close. See Known Limitations for details.

---

## Pipeline: How It Works

```
AL Source directories / .app files
    ↓  BC Compilation.Emit()           [Microsoft.Dynamics.Nav.CodeAnalysis]
Generated C# (per-object files, BC runtime types)
    ↓  RoslynRewriter                  [CSharpSyntaxRewriter — AST-level rewrites]
Rewritten C# (BC types → mock types)
    ↓  RoslynCompiler                  [Roslyn in-memory compilation]
.NET Assembly (in-memory)
    ↓  Executor                        [test discovery + invocation]
Test Results: N/M PASS
```

### Stage 1 — AlTranspiler (`Program.cs`, class `AlTranspiler`)

Uses `Microsoft.Dynamics.Nav.CodeAnalysis.Compilation.Emit()` — the BC compiler's
public API — to transpile AL source to C#. Each AL object (table, codeunit, etc.)
becomes a separate C# class. The transpiler supports:
- Single .al files
- Directories of .al files
- .app packages (ZIP archives containing AL source)
- Symbol references via `--packages <dir>` for cross-app dependencies

### Stage 2 — RoslynRewriter (`RoslynRewriter.cs`)

A `CSharpSyntaxRewriter` that transforms BC-generated C# into standalone code.
Key transformations:
- Remove BC-specific attributes (`[NavCodeunitOptions]`, etc.)
- Replace BC runtime type references with mock types (see mock surface below)
- Strip ITreeObject arguments from method calls
- Remove `.Target` property chains on record handles
- Strip event subscription calls (`RunEvent`, `ALBindSubscription`, etc.)
- Inject `using AlRunner.Runtime;` into each generated namespace

### Stage 3 — RoslynCompiler (`Program.cs`, class `RoslynCompiler`)

Compiles the rewritten C# in-memory using Roslyn, referencing:
- BC Service Tier DLLs (`Microsoft.Dynamics.Nav.Ncl`, `.Nav.Types`, `.Nav.Common`,
  `.Nav.Language`, `.Nav.Core`, `.Nav.Types.Report.*`)
- `AlRunner.Runtime` (the mock implementations in `Runtime/`)

No files are written to disk. The resulting `Assembly` is held in memory.

### Stage 4 — Executor (`Program.cs`, class `Executor`)

- **Test mode**: Discovers codeunits with `Subtype = Test`, resets all in-memory
  tables between tests, calls each `[NavTest]`-attributed method, reports pass/fail.
- **Run mode**: Finds and calls `OnRun()` on the first codeunit in the assembly.

---

## Mock Surface (Runtime/)

These are the BC runtime types replaced in standalone mode:

| Mock class | Replaces | What it does |
|---|---|---|
| `AlScope` | `NavMethodScope<T>` | Base class for all codeunit scopes. Provides `AssertError()`. |
| `MockRecordHandle` | `NavRecordHandle` | In-memory table store with SETRANGE/SETFILTER filtering. |
| `MockCodeunitHandle` | `NavCodeunitHandle` | Cross-codeunit dispatch via reflection on generated classes. |
| `MockVariant` | `NavVariant` | Boxed variant value (any type). |
| `MockArray<T>` | `NavArray<T>` | Generic array without ITreeObject requirement. |
| `MockRecordArray` | `NavArray<NavRecordHandle>` | Array of MockRecordHandle (Record[] in AL). |
| `MockInterfaceHandle` | `NavInterfaceHandle` | AL interface dispatch stub. |
| `MockAssert` | Codeunit 130 "Library Assert" | Assert.AreEqual, AreNotEqual, IsTrue, IsFalse, ExpectedError, ExpectedErrorCode, ExpectedTestFieldError. |
| `MockIsolatedStorage` | `ALIsolatedStorage` | In-memory key-value store (Set, Get, Delete, Contains). Handles NavSecretText via reflection. |
| `MockTextBuilder` | `NavTextBuilder` | In-memory StringBuilder (Append, AppendLine, ToText). |
| `AlScope.AssertError()` | `asserterror` keyword | Catches expected errors, stores message. |
| `AlDialog` | `NavDialog` static methods | Message() prints to console; Error() throws Exception. |
| `MockDialog` | `NavDialog` instance | No-op progress dialog (ALOpen/ALUpdate/ALClose). |
| `AlCompat` | `ALCompiler`, `NavFormatEvaluateHelper` | Type conversion helpers; ALRandomize/ALRandom. |
| `MockRecordRef` | `NavRecordRef` | RecordRef backed by MockRecordHandle. Open/Close, Field, Insert/Modify/Delete, FindSet/Next, GetTable/SetTable. |
| `MockFieldRef` | `NavFieldRef` | FieldRef with ALValue get/set, ALNumber, ALSetRange, ALSetFilter, ALValidate. |
| `MockTestPageHandle` | `NavTestPageHandle` | TestPage variable mock. OpenEdit/OpenView/OpenNew/Close/Trap/New/ClearReference lifecycle; GetField for field access; GetBuiltInAction for OK/Cancel. GoToRecord(), Next(), GetPart() navigation. Tracks ModalResult for RunModal interception. Caption, First(), GoToKey(), Filter.SetFilter()/GetFilter() stubs. |
| `MockTestPageField` | TestPage field | ALSetValue/ALValue (assignable) for field get/set on TestPage fields. ALCaption, ALAsDecimal(), ALEnabled() stubs. |
| `MockTestPageAction` | TestPage action | ALInvoke for OK/Cancel/Close built-in actions. Sets parent handle's ModalResult (OK→LookupOK, Cancel→LookupCancel). |
| `MockTestPageFilter` | TestPage filter | ALSetFilter(fieldNo, filterExpression) and ALGetFilter(fieldNo) for TestPage filter tracking. |
| `MockReportHandle` | `NavReportHandle` | Report variable mock. SetTableView(), Run() (no-op), RunRequestPage() (dispatches to RequestPageHandler). Helper-procedure dispatch via Invoke(). |
| `MockFile` | `NavFile` | Standalone file-dialog replacement. ALUploadIntoStream returns false (no client surface). |
| `MockFormHandle` | `NavFormHandle` | Page variable mock. RunModal() dispatches to ModalPageHandler via HandlerRegistry, returns FormResult. |
| `MockVariableStorage` | Codeunit 131004 "Library - Variable Storage" | In-memory FIFO queue: Enqueue, DequeueText/Integer/Decimal/Boolean/Date/Variant, AssertEmpty, Clear, IsEmpty. |
| `MockBlob` | `NavBLOB` | In-memory BLOB field. CreateInStream/CreateOutStream, HasValue, Clear. |
| `MockInStream` | `NavInStream` | In-memory InStream for reading BLOB data as text/bytes. Also used by `AlCompat.HttpContentLoadFrom` to read stream content into `NavHttpContent`. |
| `MockOutStream` | `NavOutStream` | In-memory OutStream for writing text/bytes to BLOB. |
| `MockStream` | `ALStream` | Static stream helper. ALReadText/ALWriteText routing to MockInStream/MockOutStream. |
| `AlCompat.HttpContentLoadFrom` | `NavHttpContent.ALLoadFrom(NavInStream)` | Accepts `MockInStream`; reads all text and forwards to `ALLoadFrom(NavText)`. The 1-arg `ALLoadFrom` redirect handles both NavText and MockInStream. |
| `AlCompat.HttpContentReadAs` | `NavHttpContent.ALReadAs(ITreeObject, DataError, ByRef<NavInStream>)` | No-op; sets target InStream to empty MockInStream (HTTP receive not available without service tier). |
| `HandlerRegistry` | BC test framework | Dispatches ConfirmHandler/MessageHandler/ModalPageHandler/RequestPageHandler from [NavTest].Handlers to registered handler methods. |
| `MockJsonHelper` | `NavJsonToken.ALWriteTo/ALReadFrom/ALSelectToken/ALSelectTokens` | Bypasses TrappableOperationExecutor for JSON serialization/deserialization. Real BC types used for all other JSON operations. |
| `MockSession` | `ALSession.ALStartSession/ALStopSession/ALIsSessionActive`, `NavSession.Sleep` | StartSession dispatches codeunit synchronously via MockCodeunitHandle, returns true. StopSession/Sleep are no-ops. IsSessionActive returns false. |
| `MockXmlPortHandle` | `NavXmlPortHandle` | XmlPort variable stub. Exposes Source/Destination properties and Import/Export instance methods (throw NotSupportedException). Static StaticImport/StaticExport for `XmlPort.Import/Export(portId, stream)` calls. Invoke() returns null. |
| `MockQueryHandle` | `NavQueryHandle` / `NavQuery` | Query variable and base class stub. Close/SetFilter/SetRange/TopNumberOfRows are no-ops. Open/Read/SaveAsCsv/SaveAsXml/SaveAsJson/SaveAsExcel throw NotSupportedException. GetColumnValueSafe returns type defaults. Invoke() returns null. |
| `EventSubscriberRegistry` | BC event infrastructure | Static registry mapping `(ObjectType, ObjectId, EventName)` to subscriber methods. Auto-discovers `[NavEventSubscriber]` attributes via reflection. Supports manual subscriber binding (Bind/Unbind) for `[ManualEventSubscriber]`-annotated codeunits. |
| `ManualEventSubscriberAttribute` | `NavCodeunitOptions.EventManualBinding` | Marker attribute emitted by the rewriter on codeunit classes with `EventSubscriberInstance = Manual`. Detected by `EventSubscriberRegistry.Build()` to gate dispatch on `Bind()`. |

### MockRecordHandle capabilities

- Init, Insert, Modify, ModifyAll, Get, GetBySystemId, Delete, DeleteAll
- FindFirst, FindLast, FindSet, Next iteration
- Composite primary keys (RegisterPrimaryKey)
- SetCurrentKey / SetAscending sort ordering
- SetRange, SetFilter with simple comparisons (=, <>, <, <=, >, >=)
- Filter expressions with wildcards (\*) and OR separators (|)
- OnValidate triggers on field assignment
- Count, IsEmpty
- Field read/write by field ID (GetFieldValueSafe / SetFieldValueSafe)
- ClearFieldValue (reset single field to default)
- ALGetView / ALSetView (store/retrieve view text)
- GetGlobalArrayVariable (typed MockArray for Code, Text, Integer, Decimal, Boolean)
- ALFieldNo(fieldName) lookups (RegisterFieldName)
- Cross-record table reset via `ResetAll()` between tests
- Implicit DB trigger events: OnBefore/AfterInsert, OnBefore/AfterModify, OnBefore/AfterDelete, OnBefore/AfterValidate
- xRec snapshot creation via `SnapshotForXRec()` for modify/delete/validate events

### Removed out-of-scope files

The following have been removed (they were stubs for unsupported features):

- `RegexRewriter` class — dead code superseded by `RoslynRewriter`

Note: `MockFormHandle.cs` provides Page variable stubs (lifecycle + procedure
dispatch + RunModal interception via HandlerRegistry). `MockTestPageHandle.cs`
provides TestPage support with field get/set, built-in action result tracking,
and handler dispatch. Both are active code.

---

## Agent Guide (`--guide`)

The CLI has a `--guide` flag that prints a comprehensive test-writing reference
for AI coding agents. The guide is emitted by `PrintGuide()` in `Program.cs`.

**When modifying the runner's capabilities (new mock methods, new supported AL
features, new CLI flags), update the guide text in `PrintGuide()` to match.**
The guide is the primary way external agents discover what the runner supports.

The `--help` output references the guide at the bottom so agents discover it
automatically when they run `al-runner -h`.

---

## Testing Requirements

**Every feature, fix, or mock addition MUST have a corresponding test case in
`tests/`. No exceptions.** The test suite is the proof that the runner works.
Without it, nobody can trust the tool.

### Test structure

Tests live in numbered buckets inside `tests/`:

```
tests/
  bucket-1/     ← test suites 01–32, 71, 77, 79-gui-fieldclass
  bucket-2/     ← test suites 33–95 (remainder)
  stubs/        ← 39-stubs (needs --stubs flag, separate invocation)
  excluded/     ← 06-intentional-failure, 46-missing-dep-hint (fixtures, not in main loop)
```

Each test suite inside a bucket:
```
tests/bucket-N/NN-descriptive-name/
  src/   — AL source codeunit(s) exercising the feature
  test/  — AL test codeunit (Subtype = Test) using Assert
```

**When adding a new test suite:** put it in the bucket with fewer suites and verify
its AL object IDs don't clash with other suites already in that bucket. Object IDs
must be unique within a bucket (suites compile together). IDs may repeat across
buckets (separate invocations). Add `bucket-3`, `bucket-4`, etc. when a bucket
exceeds ~50 suites.

### Positive AND negative tests
Every test case must prove BOTH that correct input succeeds AND that incorrect
input fails as expected:

- **Positive**: Call the logic with valid input, `Assert.AreEqual` on the result
- **Negative**: Call with invalid input, use `asserterror` + `Assert.ExpectedError`
  to verify the runner catches errors correctly

If a test only proves the happy path, it doesn't prove the runner works — a mock
that always returns a default value would also pass. Negative tests prove that
the runner actually executes the logic and catches failures.

### When to add tests
- **New mock method** (e.g., `MockRecordHandle.ALRecordId`) → test that calls it
- **New rewriter rule** (e.g., `ALIsolatedStorage → MockIsolatedStorage`) → test
  with AL code that uses the feature
- **Bug fix** → RED test first (fails without fix), then GREEN (passes with fix)
- **New CLI flag** → test that exercises it

### Running tests
```bash
# Run all buckets (each bucket is one al-runner invocation)
for bucket in tests/bucket-*/; do
  args=""
  for suite in "$bucket"*/; do
    [ -d "${suite}src"  ] && args="$args ${suite}src"
    [ -d "${suite}test" ] && args="$args ${suite}test"
  done
  dotnet run --project AlRunner -- $args
done

# Stubs test (separate — requires --stubs flag)
dotnet run --project AlRunner -- --stubs tests/stubs/39-stubs/stubs tests/stubs/39-stubs/src tests/stubs/39-stubs/test
```

### CI
Tests run against a matrix of BC versions (26.0–27.5) on every push. The publish
workflow gates NuGet release on all tests passing across all versions.

---

## Agent Working Rules

These rules apply to every task performed by an AI agent in this repo:

1. **Strict red/green TDD** — Write the failing test first (RED), verify it fails,
   then implement the fix (GREEN). Never write implementation code without a
   failing test. This applies to every feature, fix, and mock addition.

2. **Documentation must always be current** — Every change that affects behaviour,
   CLI flags, mock capabilities, or known limitations must be reflected in:
   - `README.md`
   - `--help` / `-h` output (`Program.cs`)
   - `--guide` output (`PrintGuide()` in `Program.cs`)
   - `CHANGELOG.md`
   - The Known Limitations / Implemented Features sections in this file

3. **Best solution, not easiest** — Always choose the highest-quality solution
   regardless of refactoring scope. Avoid shortcuts that create technical debt.
   Do not settle for "good enough" when a clearly better design is available.

4. **SOLID & DRY** — Apply SOLID principles to a reasonable degree. Avoid
   duplicating logic; extract shared behaviour into well-named, single-purpose
   helpers. Prefer composition over inheritance. Keep classes focused.

---

## Developer Contract

**What you can unit-test with al-runner:**
Pure-logic codeunits that operate on records (tables) and call other codeunits.
The codeunit must not depend on Page, Report, XMLPort, HTTP, events, or external
services to execute its core logic.

**What you cannot unit-test with al-runner (by design):**
Anything that requires the BC service tier. If you can't inject the dependency
via an AL interface, the function is not unit-testable in standalone mode. This
is a deliberate design boundary, not a bug to fix.

**The injection pattern:**
Define an AL interface for the dependency and inject it into the codeunit via
a procedure parameter. The test provides a mock implementation; the production
code uses the real one. Example:
```al
interface IMailSender
procedure Send(Subject: Text; Body: Text): Boolean;
end

codeunit 50200 MyProcessor
procedure Process(Sender: Interface IMailSender)
// ... logic that calls Sender.Send(...)
end
```

---

## Architecture: What's Real vs What's Mocked

The runner uses the **real BC type system** — `NavText`, `Decimal18`, `NavOption`,
`NavBoolean`, `NavCode`, etc. all come from the actual Microsoft DLLs
(`Microsoft.Dynamics.Nav.Types.dll`, `Nav.Ncl.dll`, `Nav.Runtime.dll`). AL
arithmetic, string operations, type conversions, and comparisons run as real
Microsoft code, not reimplementations.

What we mock is **only the I/O boundary** — the thin layer where BC types reach
into infrastructure (database, session, UI):

| Mocked | Real BC call | Why |
|---|---|---|
| `MockRecordHandle` | `NavRecordHandle.ALInsert()` → SQL | No database |
| `MockIsolatedStorage` | `ALIsolatedStorage.ALSet()` → DB | No key-value DB |
| `MockCodeunitHandle` | `NavCodeunit.RunCodeunit()` → runtime | No dispatcher |
| `AlDialog.Error/Message` | `NavDialog.ALError()` → UI | No service tier |
| `MockTextBuilder` | `NavTextBuilder` → `NavEnvironment` | Avoids session crash |

Everything else — `NavText + NavText`, `Decimal18 * Decimal18`,
`NavOption.Create()`, `Format()`, `Evaluate()` — runs as the real Microsoft
code from the Service Tier DLLs.

If you find yourself reimplementing business logic in a mock, that's a sign the
test belongs in the full BC pipeline, not in the runner.

---

## Known Limitations (by design)

- **Implicit event publishers on DB operations** — OnAfterModify, OnAfterInsert,
  OnAfterDelete, etc. are NOT fired. The DB trigger pipeline is not implemented.
- **Page, Report, XmlPort** — Page variables (`Page "X"`) are stubs via `MockFormHandle`.
  `RunModal()` dispatches to `[ModalPageHandler]` when registered; otherwise throws.
  TestPage variables (`TestPage "X"`) support field get/set, built-in actions,
  navigation (GoToRecord, Next, New, GetPart), and filter tracking (SetFilter/GetFilter)
  via `MockTestPageHandle`. ConfirmHandler, MessageHandler, ModalPageHandler, and
  RequestPageHandler dispatch is supported. Report variables (`Report "X"`) compile via
  `MockReportHandle` with `SetTableView()`, `Run()` (no-op), and `RunRequestPage()`
  (dispatches to `[RequestPageHandler]`). Report rendering/layout is NOT available.
  `rendering { ... }` blocks are stripped from report AL source before transpilation.
  XmlPort variables (`XmlPort "X"`) compile via `MockXmlPortHandle` but
  `Import()`/`Export()` throw `NotSupportedException` at runtime — XmlPort I/O requires
  the BC service tier. Developer must inject via AL interfaces for complex XmlPort I/O.
- **Query data access** — Query variables (`Query "X"`) compile via `MockQueryHandle`.
  `Close()`, `SetFilter()`, `SetRange()`, `TopNumberOfRows()` are no-ops.
  `Open()`, `Read()`, `SaveAsCsv/Xml/Json/Excel()` throw `NotSupportedException` —
  query data access requires the BC service tier (SQL views). Developer must inject
  query dependencies via AL interfaces for testable code.
- **HTTP** — Sending/receiving HTTP is NOT supported (no service tier). Codeunits that
  declare `HttpClient`, `HttpRequestMessage`, `HttpResponseMessage`, or `HttpContent`
  variables now compile. `HttpContent.WriteFrom(InStream)` and `ReadAs(var InStream)`
  are handled by `AlCompat` helpers so they no longer produce CS1503. Pure-logic methods
  in HTTP codeunits (those that don't actually call `HttpClient.Send()`) are fully
  testable. Developer must inject HTTP send via AL interface for unit-testable code.
- **Events/subscribers** — Custom `[IntegrationEvent]`/`[BusinessEvent]` dispatch
  works via `AlCompat.FireEvent()` + `EventSubscriberRegistry`. Subscriber
  parameters are forwarded. Implicit DB trigger events (OnBefore/AfterInsert,
  OnBefore/AfterModify, OnBefore/AfterDelete, OnBefore/AfterValidate) fire from
  `MockRecordHandle`. `BindSubscription`/`UnbindSubscription` work for manual
  subscriber codeunits. Remaining gaps: `OnBefore/AfterRenameEvent` not yet
  fired; `ModifyAll`/`DeleteAll` do not fire per-row events; implicit event
  publishers on table extension triggers not wired.
- **.app file loading** — NOT supported for test input. Always runs from AL source
  directories. Dependencies can be loaded from .app for symbol references only.
- **Filter groups** (FilterGroup) — not tracked.
- **ALGetFilter** — returns empty string even when filters are active.
- **InitValue** — field InitValue properties from AL source are not applied by
  `MockRecordHandle.ALInit()`. Fields always initialize to type defaults (0, "", false).
- **ALFieldCaption** — returns "FieldNN" instead of the actual caption. The runner
  lacks field metadata infrastructure.
- **Codeunit OnRun with record parameter** — `RunCodeunit` only finds parameterless
  `OnRun()` methods. Codeunits whose OnRun trigger takes a record parameter (e.g.,
  `trigger OnRun(var Rec: Record "Job Queue Entry")`) will silently do nothing.

---

## Implemented Features (previously listed as missing)

These have been implemented and are tested by the test suite:

1. **Assert codeunit mock** (`Runtime/MockAssert.cs`) — `Assert.AreEqual`,
   `Assert.AreNotEqual`, `Assert.IsTrue`, `Assert.IsFalse`, `Assert.ExpectedError`,
   `Assert.ExpectedErrorCode` (1-arg and 2-arg), `Assert.ExpectedTestFieldError`,
   `Assert.ExpectedMessage`. Wired into `MockCodeunitHandle` so calls to
   codeunit 130 (Library Assert) route to `MockAssert`. An AL stub
   (`stubs/LibraryAssert.al`) is auto-loaded so test code compiles without
   requiring the real Assert .app.

2. **Composite primary key support** — `ALGet`, `ALModify`, `ALDelete`, `ALRename`
   use all PK fields registered via `MockRecordHandle.RegisterPrimaryKey()`.
   Falls back to field 1 when no PK is registered.

3. **Sort ordering** — `ALSetCurrentKey` / `ALSetAscending` are applied in
   `GetFilteredRecords()`. `ALFind("+")` returns the record with the largest
   key value.

4. **ALFieldNo(fieldName)** — Field-by-name lookups work when registered via
   `MockRecordHandle.RegisterFieldName()`.

5. **Table extension field support** — Extension-scoped `SetFieldValueSafe`,
   `GetFieldValueSafe`, `GetFieldRefSafe`, and `Invoke` overloads accept the
   `(extensionId, fieldId, ...)` pattern emitted by the BC compiler for table
   extension fields. The extensionId is ignored; fields are stored flat.

6. **ALRecordId** — `MockRecordHandle.ALRecordId` returns `NavRecordId.Default`.

7. **ALModifyAllSafe** — `MockRecordHandle.ALModifyAllSafe` updates a field on
   all records matching current filters.

8. **Interface return from functions** — `MockInterfaceHandle` supports 1-arg
   constructor (parent scope), nested interface delegation, and `NavScope`
   parameter conversion in cross-codeunit dispatch.

9. **StrSubstNo with integer (and other NavValue) arguments** — `ALSystemString.ALStrSubstNo`
   is intercepted by `RoslynRewriter` and redirected to `AlCompat.StrSubstNo`, which
   formats each `%1`/`%2`/… placeholder using `AlCompat.Format()`. This avoids the
   `NullReferenceException` in `NavIntegerFormatter.FormatWithFormatNumber` that occurs
   when `NavSession` is null in the runner context. Tested by `tests/67-strsubstno-integer/`.

14. **Picture-string tokens in `Format(value, length, formatString)`** (`Runtime/AlScope.cs`) —
    `AlCompat.Format()` now handles AL picture strings for decimal and time values:
    - `<Precision,min:max>` — rounds a `Decimal` to at most `max` decimal places, shows at
      least `min` (e.g. `Format(1.567, 0, '<Precision,1:2>')` → `'1.57'`).
    - `<Standard Format,N>` — N=0 uses default AL decimal formatting (dot separator, no
      trailing zeros); N=1 rounds to integer.
    - Time picture strings like `<Hours24,2>:<Minutes,2>` applied to `Time` variables
      (e.g. `Format(093000T, 0, '<Hours24,2>:<Minutes,2>')` → `'09:30'`).
    Also fixes a bug where `AlCompat.Format()` used the OS locale's decimal separator instead
    of always using `.` (invariant). Tested by `tests/85-picture-format/` (9 test cases).

10. **RecordRef / FieldRef runtime support** (`Runtime/MockRecordRef.cs`,
    `Runtime/MockFieldRef.cs`) — `MockRecordRef` delegates all data operations to
    `MockRecordHandle`, sharing the same in-memory table store as typed Record
    variables. `MockFieldRef` wraps field slots with value get/set. The rewriter
    maps `NavFieldRef` -> `MockFieldRef`. Supported operations:
    - `RecRef.Open(tableId)` / `Close()` / `Number()` / `Clear()`
    - `RecRef.Field(n)` returning `MockFieldRef` with `ALValue` get/set
    - `RecRef.FindSet()` / `FindFirst()` / `FindLast()` / `Next()` iteration
    - `RecRef.Insert()` / `Modify()` / `Delete()` / `DeleteAll()`
    - `RecRef.GetTable(Rec)` / `SetTable(Rec)` for bidirectional data copy
    - `FieldRef.SetRange()` / `FieldRef.SetFilter()` for filtering
    - `RecRef.Count()` / `IsEmpty()` / `Reset()` respecting active filters
    - `FieldRef.Number()` / `FieldRef.Validate()`
    Tested by `tests/69-recref-fieldref/` (23 test cases).

11. **TestPage support** (`Runtime/MockTestPageHandle.cs`, `Runtime/HandlerRegistry.cs`)
    — `NavTestPageHandle` is rewritten to `MockTestPageHandle`. Supports:
    - `OpenEdit()` / `OpenView()` / `OpenNew()` / `Close()` / `Trap()` / `New()` /
      `ClearReference()` lifecycle
    - `GetField(fieldHash)` returning `MockTestPageField` with `ALSetValue` / `ALValue`
      (assignable), `ALAsDecimal()`, `ALEnabled()`
    - `GetBuiltInAction(FormResult)` returning `MockTestPageAction` with `ALInvoke()`
    - `GoToRecord(rec)` navigation (stub returns `true`)
    - `Next()` navigation (stub returns `false`)
    - `GetPart(partHash)` returning a nested `MockTestPageHandle`
    - **ConfirmHandler** dispatch: `[ConfirmHandler]` procedures intercept `Confirm()` calls
    - **MessageHandler** dispatch: `[MessageHandler]` procedures intercept `Message()` calls
    - **ModalPageHandler** dispatch: `[ModalPageHandler]` procedures intercept `Page.RunModal()`
      calls. `MockFormHandle.RunModal()` creates a `MockTestPageHandle`, invokes the handler,
      and returns the `FormResult` set by OK/Cancel action invocation. Missing handler throws
      a descriptive error.
    - **RequestPageHandler** dispatch: `[RequestPageHandler]` procedures intercept
      `Report.RunRequestPage()` calls. Falls back to `[ModalPageHandler]` if no dedicated
      request-page handler is registered.
    - Handler registration via `[NavTest].Handlers` attribute on test methods
    - `Caption` property (stub returns `"TestPage"`)
    - `First()` navigation (stub returns `true`)
    - `GoToKey(keyValues...)` navigation (stub returns `true`)
    - `Filter.SetFilter(fieldNo, filterExpression)` and `Filter.GetFilter(fieldNo)` via
      `MockTestPageFilter`
    Tested by `tests/71-testpage/` (13 test cases), `tests/73-modal-handler/` (3 test cases),
    `tests/74-testpage-navigation/` (6 test cases), and `tests/90-testpage-extended/` (10 test cases).

12. **Library - Variable Storage mock** (`Runtime/MockVariableStorage.cs`) —
    Built-in stub for codeunit 131004 "Library - Variable Storage". Provides an
    in-memory FIFO queue for passing values between test setup and handler
    functions. Supports `Enqueue`, `DequeueText`, `DequeueInteger`,
    `DequeueDecimal`, `DequeueBoolean`, `DequeueDate`, `DequeueVariant`,
    `AssertEmpty`, `Clear`, and `IsEmpty`. AL stub auto-loaded alongside Assert.
    Tested by `tests/75-library-variable-storage/` (9 test cases).

13. **BLOB / InStream / OutStream support** (`Runtime/MockBlob.cs`,
    `Runtime/MockInStream.cs`, `Runtime/MockOutStream.cs`, `Runtime/MockStream.cs`)
    — `NavBLOB` is rewritten to `MockBlob`, an in-memory `NavValue` subclass
    storing raw `byte[]`. `NavInStream`/`NavOutStream` are rewritten to
    `MockInStream`/`MockOutStream` (standalone, no ITreeObject). `ALStream` is
    rewritten to `MockStream` which routes `ALReadText`/`ALWriteText` to the
    mock streams. BLOB fields on records auto-persist `MockBlob` instances.
    Supports: `CreateInStream`, `CreateOutStream`, `HasValue`, `WriteText`,
    `ReadText`. Tested by `tests/78-blob-stream/` (6 test cases).

15. **Report handle support** (`Runtime/MockReportHandle.cs`) — `NavReportHandle`
    is rewritten to `MockReportHandle`, a standalone replacement for BC's report
    handle. Supports `SetTableView()`, `Run()` (no-op), `RunRequestPage()` (dispatches
    to `[RequestPageHandler]`), and helper-procedure dispatch via `Invoke()`.
    Report and report-extension generated classes are stubbed so BC-only layout/runtime
    infrastructure does not block compilation. `rendering { ... }` blocks and
    `DefaultRenderingLayout` properties are stripped from report AL source before
    transpilation. Tested by `tests/91-report-handle/` (6 test cases) and
    `tests/95-rendering-strip/` (2 test cases).

16. **`[RequestPageHandler]` dispatch** (`Runtime/HandlerRegistry.cs`) — `HandlerRegistry`
    now registers and invokes `[RequestPageHandler]` procedures. Falls back to
    `[ModalPageHandler]` when no dedicated request-page handler is registered.
    Tested by `tests/92-request-page-handler/` (2 test cases).

17. **`GetBySystemId`** (`Runtime/MockRecordHandle.cs`, `Runtime/MockRecordRef.cs`) —
    `ALGetBySystemId(Guid)` looks up records by system ID field (2000000000). Throws
    when not found with `DataError.ThrowError`, returns false with `DataError.TrapError`.
    Tested by `tests/93-record-getbysystemid/` (2 test cases).

18. **`ClearFieldValue`** (`Runtime/MockRecordHandle.cs`) — Resets a single field to
    its default by removing it from the field dictionary. Supports both direct and
    extension-scoped overloads. The rewriter redirects `ALSystemVariable.Clear(x)` to
    `x.Clear()` for non-NavComplexValue types. Tested by `tests/94-clear-field-value/`
    (6 test cases).

19. **Event subscriber parameter forwarding** (`AlRunner/RoslynRewriter.cs`,
    `AlRunner/Runtime/AlScope.cs`) — Publisher event arguments (`ByRef<T>` and
    value parameters) are forwarded from `βscope.RunEvent()` to subscriber
    methods via positional matching. `AlCompat.FireEvent()` accepts
    `params object?[] eventArgs` and passes them through to subscriber
    invocations. This enables subscriber write-back through shared `ByRef<T>`
    references. Tested by `tests/97-event-params/` (2 test cases) and
    `tests/101-multi-subscribers/` (2 test cases).

20. **Implicit DB trigger events** (`AlRunner/Runtime/MockRecordHandle.cs`) —
    `MockRecordHandle` fires OnBefore/AfterInsertEvent, OnBefore/AfterModifyEvent,
    OnBefore/AfterDeleteEvent from `ALInsert`/`ALModify`/`ALDelete`, and
    OnBefore/AfterValidateEvent from `ALValidateSafe`/`ALValidate`. Events fire
    regardless of `runTrigger` (matching BC behavior). xRec snapshots are created
    via `SnapshotForXRec()` before mutations. Uses `EventSubscriberRegistry` with
    3-tuple key `(ObjectType, ObjectId, EventName)` to prevent table/codeunit ID
    collision. Tested by `tests/98-db-trigger-events/` (5 test cases) and
    `tests/99-validate-events/` (3 test cases).

21. **BindSubscription / UnbindSubscription** (`AlRunner/RoslynRewriter.cs`,
    `AlRunner/Runtime/MockCodeunitHandle.cs`, `AlRunner/Runtime/EventSubscriberRegistry.cs`,
    `AlRunner/Runtime/ManualEventSubscriberAttribute.cs`) — The rewriter detects
    `NavCodeunitOptions.EventManualBinding` in `[NavCodeunitOptions]` and emits a
    `[ManualEventSubscriber]` marker attribute. `ALSession.ALBindSubscription()` /
    `ALUnbindSubscription()` are rewritten to `MockCodeunitHandle.Bind()` /
    `Unbind()`. The `EventSubscriberRegistry` tracks bound instances and only
    dispatches to manual subscribers when bound. Bindings are reset between tests.
    Tested by `tests/100-bind-subscription/` (3 test cases).

These are gaps that remain for full production use:

1. **Wire al-runner.json config into the CLI** — config file exists but is not
   read by the CLI.
2. **Filter groups** (FilterGroup) — not tracked.
3. **ALGetFilter** — returns empty string even when filters are active.
4. **RecordRef/FieldRef metadata** — `FieldRef.Name`, `FieldRef.Caption`,
   `FieldRef.Type`, `FieldRef.Length`, `FieldRef.Class` return stub defaults
   (no field metadata infrastructure). `FieldCount` returns the number of
   fields that have been set on the current record, not the table schema count.
5. **KeyRef** — not implemented.
6. **Codeunit OnRun with record parameter** — `RunCodeunit` only finds
   parameterless `OnRun()` methods. Codeunits whose OnRun trigger takes a
   record parameter will silently do nothing.
7. **Implicit DB trigger events** — OnBefore/AfterInsert/Modify/Delete/Rename
   are NOT fired by MockRecordHandle.
8. **Temporary records** — `IsTemporary` always returns false; no isolated
   partition for temp records.
9. **FlowField formulas beyond Exist** — `Sum`, `Count`, `Lookup`, `Average`,
   `Min`, `Max` are not computed by CalcFields.
10. **ErrorInfo & collectible errors** — `Error(ErrorInfo)` works (throws with
    message) but ErrorInfo property access and the collectible errors framework
    are not implemented.
11. **RecordId fidelity** — `ALRecordId` returns `NavRecordId.Default` instead
    of encoding the actual table ID + primary key values.

---

## CLI Usage

### Global tool (al-runner)
```bash
al-runner ./src ./test                        # run tests (auto-detected)
al-runner --coverage ./src ./test             # run with coverage report
al-runner --packages ./packages ./src ./test  # with dependency symbols
al-runner --stubs ./stubs ./src ./test        # with stub AL files
al-runner -v ./src ./test                     # verbose output
al-runner --dump-csharp ./src                 # dump generated C# (before rewriting)
al-runner --dump-rewritten ./src              # dump rewritten C# (after rewriting)
al-runner --output-json ./src ./test          # machine-readable JSON output
al-runner --run TestMyThing ./src ./test      # run a single test procedure by name
al-runner --capture-values ./src ./test       # capture variable values after each test
al-runner --server                            # long-running JSON-RPC daemon (stdin/stdout)
al-runner -e 'codeunit 99 X { trigger OnRun() begin Message('"'"'hi'"'"'); end; }'
al-runner --generate-stubs .alpackages ./stubs             # scaffold stubs (all codeunits)
al-runner --generate-stubs .alpackages ./stubs ./src ./test # stubs filtered to referenced objects
al-runner --guide                             # print test-writing guide for AI agents
al-runner -h                                  # help
```

### Development (dotnet run)
```bash
dotnet run --project AlRunner -- ./src ./test
dotnet run --project AlRunner -- --coverage ./src ./test
```

### Config file (al-runner.json)
Place in the project root. Not yet wired into the CLI (future work):
```json
{
  "sourcePath": "./src",
  "testPath": "./test",
  "testCodeunits": [50200, 50201]
}
```

---

## Build & Bootstrap

**Prerequisites:**
- .NET 8 SDK

Both the AL compiler (~57 MB from NuGet) and BC Service Tier DLLs (~11 MB via
HTTP range requests) are downloaded automatically on first build. No manual setup.
Works on Windows, Linux, and macOS.

**Build:**
```bash
dotnet build AlRunner/
```

**Run tests:**
```bash
dotnet run --project AlRunner -- ./src ./test
```

**Install as global tool:**
```bash
dotnet tool install --global MSDyn365BC.AL.Runner
al-runner ./src ./test
```

---

## BC-Linux Integration and Pipeline Outcomes

al-runner runs as a fast pre-check step before the full BC service tier pipeline
(MsDyn365Bc.On.Linux). The recommended workflow:

1. **al-runner** — run in CI in seconds for AL unit tests (fast feedback)
2. **Full pipeline** — run the BC service tier (Docker + SQL) for integration/UI tests

The full pipeline lives at:
- https://github.com/StefanMaron/MsDyn365Bc.On.Linux

The alDirectCompile repo (`bc-linux` / `alDirectCompile`) contains the service tier
startup hook patches and the CI pipeline that orchestrates both approaches.

### Exit codes

| Code | Meaning |
|------|---------|
| 0 | All tests passed |
| 1 | Test assertion failures (real bugs in code) or usage/argument error |
| 2 | Runner limitations only (no assertion failures; all blocked tests are due to Roslyn compilation gaps or missing mock support) |
| 3 | AL compilation error (the AL source itself does not compile) |

Use exit codes in CI to distinguish runner gaps from real failures:

```bash
al-runner --packages .alpackages ./src ./test
rc=$?
if [ $rc -eq 2 ]; then
  echo "Runner limitations only — not a build failure"
  exit 0
elif [ $rc -ne 0 ]; then
  exit $rc
fi
```

### Three possible outcomes when al-runner executes a test codeunit

**Outcome 1 — FAIL: test failure caught**
An assertion failed or an unhandled exception was thrown in test logic. This is a
real failure. Pipeline stops immediately. The runner correctly identified a bug.

**Outcome 2 — ERROR: runner cannot execute the codeunit**
The codeunit depends on an unsupported feature (Page, HTTP, event subscriber,
unmocked codeunit) and crashes during execution with a `NotSupportedException`
or similar. This is a configuration error, not a test failure. Pipeline stops.
The user must either:
- Remove that codeunit from the runner config (run it only in the full pipeline), or
- Inject the missing dependency via an AL interface so the runner can execute it.

**Outcome 3 — PASS (potential silent false positive)**
The runner reports PASS. However, if the test logic implicitly depends on an event
subscriber (e.g., OnAfterModify, OnAfterInsert) that modifies state and the test
asserts on that modified state — the runner will PASS because it does not fire
implicit events. The full BC service tier pipeline will catch this failure in the
subsequent step.

**The guarantee the runner provides:**
> If al-runner says FAIL, it is a real failure.
> If al-runner says PASS, the codeunit's direct logic is correct. Silent passes
> due to missing event subscribers are an accepted known limitation — the full
> BC pipeline always runs after the runner and catches them.

---

## Naming Convention

Follows the `BusinessCentral.AL.*` pattern:
- BusinessCentral.AL.Mutations — mutation testing
- BusinessCentral.AL.Runner — this repo (unit test runner)

---

## Key File Index

| File | Role |
|---|---|
| `AlRunner/Program.cs` | Main CLI + AlTranspiler + RoslynCompiler + Executor + AppPackageReader + Kernel32Shim + PrintGuide |
| `AlRunner/DiagnosticClassifier.cs` | AL diagnostic message parser; `IsSelfDuplicateAmbiguity` detects AL0275 self-duplicate packages |
| `AlRunner/PackageScanner.cs` | Scans package dirs for `.app` files; two-pass deduplication (by GUID then by publisher+name+version) |
| `AlRunner/RoslynRewriter.cs` | BC→mock type transformations (AST-level) |
| `AlRunner/Runtime/AlScope.cs` | Base scope, AlDialog, AlCompat, MockDialog |
| `AlRunner/Runtime/MockRecordHandle.cs` | In-memory record store with filtering, composite PKs, sort ordering |
| `AlRunner/Runtime/MockCodeunitHandle.cs` | Cross-codeunit dispatch via reflection |
| `AlRunner/Runtime/MockVariant.cs` | AL Variant type replacement |
| `AlRunner/Runtime/MockArray.cs` | AL Array type replacement |
| `AlRunner/Runtime/MockRecordArray.cs` | AL Record array replacement |
| `AlRunner/Runtime/MockInterfaceHandle.cs` | AL Interface dispatch stub |
| `AlRunner/Runtime/MockAssert.cs` | Assert codeunit mock (AreEqual, ExpectedError, etc.) |
| `AlRunner/Runtime/MockIsolatedStorage.cs` | In-memory IsolatedStorage mock |
| `AlRunner/Runtime/MockTextBuilder.cs` | In-memory TextBuilder mock |
| `AlRunner/Runtime/MockJsonHelper.cs` | JSON WriteTo/ReadFrom/SelectToken bypass for TrappableOperationExecutor |
| `AlRunner/Runtime/MockRecordRef.cs` | RecordRef backed by MockRecordHandle (Open, Field, Insert, FindSet, GetTable/SetTable) |
| `AlRunner/Runtime/MockFieldRef.cs` | FieldRef with ALValue get/set, ALNumber, ALSetRange, ALSetFilter |
| `AlRunner/Runtime/MockTestPageHandle.cs` | TestPage mock: lifecycle, field access, navigation (GoToRecord/Next/New/GetPart), filter tracking, built-in actions |
| `AlRunner/Runtime/MockReportHandle.cs` | Report variable mock: SetTableView, Run, RunRequestPage, helper dispatch via Invoke |
| `AlRunner/Runtime/MockFile.cs` | File-dialog replacement: ALUploadIntoStream (returns false, no client surface) |
| `AlRunner/Runtime/HandlerRegistry.cs` | ConfirmHandler/MessageHandler/ModalPageHandler/RequestPageHandler dispatch for test codeunits |
| `AlRunner/StubGenerator.cs` | `--generate-stubs` command: scaffold AL stubs from .app symbol packages |
| `AlRunner/Runtime/MockVariableStorage.cs` | In-memory FIFO queue mock for Library - Variable Storage (codeunit 131004) |
| `AlRunner/Runtime/MockBlob.cs` | In-memory BLOB field replacement for NavBLOB |
| `AlRunner/Runtime/MockInStream.cs` | In-memory InStream replacement for NavInStream |
| `AlRunner/Runtime/MockOutStream.cs` | In-memory OutStream replacement for NavOutStream |
| `AlRunner/Runtime/MockStream.cs` | Static ALStream replacement routing to MockInStream/MockOutStream |
| `AlRunner/Runtime/MockSession.cs` | Session API stubs: StartSession (synchronous dispatch), StopSession, IsSessionActive, Sleep |
| `AlRunner/Runtime/MockXmlPortHandle.cs` | XmlPort variable stub: Source/Destination properties, Import/Export (throw NotSupportedException), Invoke (returns null), StaticImport/StaticExport for static XmlPort.Import/Export calls |
| `AlRunner/Runtime/MockQueryHandle.cs` | Query variable and base class stub: Close/SetFilter/SetRange/TopNumberOfRows no-ops, Open/Read/SaveAs throw NotSupportedException |
| `AlRunner/Runtime/EventSubscriberRegistry.cs` | Event subscriber discovery + dispatch: 3-tuple key (ObjectType, ObjectId, EventName), manual binding support, auto/manual subscriber classification |
| `AlRunner/Runtime/ManualEventSubscriberAttribute.cs` | Marker attribute for manual event subscriber codeunits (emitted by rewriter from NavCodeunitOptions.EventManualBinding) |
| `AlRunner/stubs/LibraryAssert.al` | AL stub for codeunit 130 (auto-loaded for compilation) |
| `AlRunner/stubs/LibraryVariableStorage.al` | AL stub for codeunit 131004 (auto-loaded for compilation) |
| `tests/bucket-1/`, `tests/bucket-2/` | Test suites in buckets (each bucket = one al-runner invocation). `src/*.al` + `test/*.al` per suite. |
| `tests/stubs/39-stubs/` | Stubs test — run with `--stubs` flag. |
| `tests/excluded/` | Fixtures not in the main loop: `06-intentional-failure` (asserts exit 1), `46-missing-dep-hint`. |
| `.github/workflows/test-matrix.yml` | CI: runs all tests across BC version matrix |
| `.github/workflows/publish.yml` | CI: publish to NuGet on tag |

---

## Continuing Development

When resuming work on this repo as an agent, you do NOT need to re-read the
alDirectCompile repo. Everything needed is in this CLAUDE.md and the source files.

Priority order for next work:
1. Wire al-runner.json config into the CLI
2. Implement ALGetFilter to return actual filter expressions
3. Implement filter groups (FilterGroup)
4. Add more Assert methods (AreNearlyEqual, Fail, etc.)
5. Implement RecordRef / FieldRef runtime support
