# Changelog

All notable changes to this project are documented here. Format based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versioning follows
[SemVer](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **Compact summary line at end of test runs** — After each test run, the output
  now ends with a concise one-liner analogous to pytest/jest:
  - All pass: `42 passed in 1.8s`
  - With failures: `9 passed, 2 failed, 3 blocked (runner limitation) in 1.8s`
  - With setup errors: `9 passed, 1 errors in 0.3s`
  Only non-zero counts are shown. Runner-limitation errors (`IsRunnerBug=true`) are
  labelled `blocked (runner limitation)`; other errors are labelled `errors`.
  Elapsed time is always included when timing is available. Closes #71.
- **`sourceFile` field on iterations and captured values** — The `--output-json`
  iteration and captured-value records now include a `sourceFile` property with the
  path to the AL file that contains the loop or variable. A new `SourceFileMapper`
  class resolves AL object names to source files at input-loading time.
  Tested by `tests/67-iteration-tracking/`.
- **Stream assignment, binary I/O, and `COPYSTREAM`** — Three new stream capabilities:
  - `MockOutStream.ALAssign` — enables `OutStr2 := OutStr1` stream assignment in AL
  - `MockStream.ALWrite`/`ALRead` overloads for `Integer`, `Boolean`, `Decimal18` — binary read/write via `OStr.Write(value)` / `IStr.Read(value)` in AL
  - `MockStream.ALCopyStream` — implements `COPYSTREAM(OutStr, InStr)`; rewriter redirects `ALSystemVariable.ALCopyStream` to `MockStream.ALCopyStream`

  Tested by `tests/79-stream-surface/` (10 cases). Fixes #65.
- **Picture-string tokens in `Format()`** — `Format(value, 0, formatString)` now
  handles AL decimal and time picture strings:
  - `<Precision,min:max>` — rounds a decimal to at most `max` decimal places and
    shows at least `min` (e.g. `Format(1.567, 0, '<Precision,1:2>')` → `'1.57'`).
  - `<Standard Format,N>` — N=0 uses default AL decimal formatting; N=1 rounds to
    the nearest integer (e.g. `Format(1.567, 0, '<Standard Format,1>')` → `'2'`).
  - Time picture strings (`<Hours24,N>:<Minutes,N>`) applied to `Time` variables
    (e.g. `Format(093000T, 0, '<Hours24,2>:<Minutes,2>')` → `'09:30'`).
  Tested by `tests/85-picture-format/` (9 test cases).
- **`--generate-stubs` workflow documented in `--guide`** — The agent guide now
  includes a section explaining when and how to use `--generate-stubs` to scaffold
  AL stubs for missing dependencies, including the filtered form that limits output
  to objects actually referenced by the source under test.
- **Differentiated exit codes for CI integration** — al-runner now returns distinct
  exit codes so CI scripts can distinguish real failures from runner gaps:
  - `0` — all tests passed
  - `1` — test assertion failures (real bugs in code) or usage/argument error
  - `2` — runner limitations only (no assertion failures; all blocked tests are due
    to Roslyn compilation gaps or missing mock support)
  - `3` — AL compilation error (the AL source itself does not compile)

  Previously, all non-success outcomes returned `1`. This change enables incremental
  CI adoption: tolerate exit code `2` while treating `1` and `3` as hard failures.
  Fixes [#46](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/46).

### Changed
- **Invariant-culture decimal formatting** — `AlCompat.Format()` for decimal values
  now always uses `.` as the decimal separator regardless of OS locale, matching
  real BC behavior.
- **Source files with compilation errors are no longer silently excluded** — Previously,
  when Roslyn compilation failed, al-runner would silently retry by dropping the
  offending files and compiling the remaining ones. This could produce a passing run
  that was missing whole codeunits. Now, any compilation error causes an immediate hard
  failure: all errors are printed to stderr and the runner exits. This ensures you always
  compile the full app or get a clear error — no silent partial results.
  Fixes [#66](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/66).
- **Server cache preserves compilation state across requests** — The `--server` JSON-RPC
  daemon now stores compilation errors alongside the compiled assembly in the cache.
  Cache hits return the same error state as the original compilation, preventing stale
  results on repeated identical requests.

### Fixed
- **`Insert()` now enforces primary-key uniqueness for more tables** — Previously,
  PK uniqueness was only checked when the table's key declaration had been parsed from
  AL source by `TableFieldRegistry`. Tables without an explicit `keys {}` block, or
  tables loaded from external `.app` symbol packages, skipped the check entirely,
  allowing silent duplicate inserts that would have errored in real BC. Now `ALInsert`
  falls back to field 1 as the implicit PK when no key is registered, restoring
  duplicate detection for tables without a declared key. Note: for symbol-only tables
  whose actual PK is composite or does not include field 1, behavior may still differ
  from real BC.
  Tested by `tests/86-pk-insert-fallback/`. Fixes [#78](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/78).
- **`Dialog` variable type now compiles and runs** — AL codeunits that declare a
  `Dialog` variable and call `Open`, `Update`, and `Close` on it previously failed
  with `CS1503: cannot convert from 'string' to 'NavText'` when the BC compiler
  emitted string literals for the dialog format string. `MockDialog.ALOpen` and
  `ALUpdate` now accept both `string` and `NavText`/`NavValue` overloads, matching
  all patterns emitted by the BC compiler. Tested by `tests/85-dialog/` (4 test cases).
  Fixes [#63](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/63).
- **XmlPort object classes no longer break compilation** — XmlPort schema classes
  generated by the BC compiler (`XmlPortNNNN : NavXmlPort`) contain complex
  constructor and schema-initialization code that cannot compile in standalone mode.
  The rewriter now replaces these entire class bodies with minimal stubs extending
  `MockXmlPortHandle`, so any test suite that includes an XmlPort definition compiles
  and runs correctly.

## [1.0.11] — 2026-04-13

### Added
- **XmlPort stub (`MockXmlPortHandle`)** — Codeunits that declare `XmlPort "X"` variables
  now compile and run in al-runner. `NavXmlPortHandle` is rewritten to `MockXmlPortHandle`,
  which exposes `Source`/`Destination` stream properties and satisfies `Import`/`Export`
  instance method calls from the BC compiler output. The static `XmlPort.Import(portId, stream)`
  / `XmlPort.Export(portId, stream)` forms (emitted as `NavXmlPort.Import/Export`) are
  redirected to `MockXmlPortHandle.StaticImport/StaticExport`. All import/export methods
  throw `NotSupportedException` at runtime with a clear message directing the developer to
  inject the XmlPort dependency via an AL interface. `Invoke()` returns null.
  Tested by `tests/84-xmlport/` (6 test cases).

## [1.0.10] — 2026-04-13

### Fixed
- **Variant-to-Record cast now works** — AL code that assigns a `Variant` to a
  `Record` variable (`MyRec := MyVariant;`) previously caused a Roslyn compile
  error `CS0030: Cannot convert type 'MockVariant' to 'MockRecordHandle'`. Fixed
  by adding an explicit cast operator `MockVariant → MockRecordHandle` that
  unwraps the inner value, matching BC runtime behavior. Tested by
  `tests/84-variant-to-record/` (5 test cases).
- **`Variant.IsRecord()` and other `Variant.IsXxx()` type-checks now unwrap
  the `MockVariant` wrapper and handle Nav runtime wrapper types** —
  `AlCompat.ALIsRecord()` (and all sibling `ALIs*` helpers) previously received
  the `MockVariant` object directly from the rewriter and checked its type name,
  which always failed for record-typed variants. All `ALIs*` methods now unwrap
  `MockVariant` before type-checking. They also handle Nav runtime wrapper types
  (`NavBoolean`, `NavInteger`, `NavBigInteger`, `NavDecimal`, `NavDate`,
  `NavDateTime`, `NavGuid`) that appear when values come from record fields
  rather than AL literals. Tested by `tests/84-variant-to-record/` (8 test cases).
- **`--output-json` now distinguishes compilation errors from test failures** —
  Tests that could not run due to a runner limitation now receive `"status": "error"`
  instead of `"status": "fail"` in the JSON output. The top-level `"errors"` field
  correctly counts these, while `"failed"` is reserved for genuine assertion failures.
  Resolves [#67](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/67).

## [1.0.9] — 2026-04-13

### Added
- **Opt-in crash telemetry** — When an unexpected .NET exception escapes the runner
  pipeline, al-runner now prompts the user to send an anonymous error report to
  Application Insights (Azure). The prompt only appears in interactive terminal
  sessions (never in CI, server mode, or when output is redirected). A 30-second
  timeout auto-answers "no" so no pipeline can ever hang. Only `AlRunner.*` stack
  frames are included — user AL source, file paths, and codeunit names are never
  transmitted. Use `--no-telemetry` to disable the prompt entirely.

### Fixed
- **Duplicate `.app` packages no longer cause AL0275 "ambiguous reference" errors**
  — When the packages directory contains multiple copies of the same extension
  (same publisher/name/version) with different GUIDs, al-runner now deduplicates
  them proactively at scan time via `PackageScanner`, keeping exactly one entry per
  identity (deterministic: lowest GUID wins). A reactive fallback also handles
  any residual self-duplicate AL0275 errors from explicitly specified dependency
  specs. Genuine cross-extension AL0275 conflicts (different publishers or names)
  are unaffected. Stubs-vs-package conflict resolution is unchanged.
  `DiagnosticClassifier.IsSelfDuplicateAmbiguity` correctly distinguishes the two
  cases by comparing both sides of the AL0275 message. This fixes the error
  pattern `'X' is an ambiguous reference between 'X' defined by the extension
  'App by Publisher (V)' and 'App by Publisher (V)'` when both sides are identical.

### Added
- **`DiagnosticClassifier`** — new public static class that parses AL compiler
  diagnostic messages. `IsSelfDuplicateAmbiguity(message)` returns `true` when
  both extension identity strings in an AL0275 message are identical (the
  self-duplicate case). `ExtractAmbiguityExtensionIds(message)` returns both
  extension identity strings or null if the message doesn't match.
- **`PackageScanner`** — new public static class that scans package directories
  for `.app` files and returns a deduplicated `IReadOnlyList<PackageSpec>`. Two-
  pass deduplication: (1) by GUID keeping highest version, (2) by
  publisher+name+version keeping lowest GUID. Replaces the inline scan loop that
  was previously embedded in `AlTranspiler`.

### Overloaded procedures with `var List of [T]` parameters
  `Invoke` now resolves the correct overload when the BC compiler emits suffixed
  C# method names (e.g., `ProcessJson_2101255952`) for overloaded AL procedures.
  Previously, `MockCodeunitHandle.Invoke` used only the base method name, picking
  the wrong overload and causing `Object of type 'ByRef<NavList<T>>' cannot be
  converted to type 'T'` reflection errors.
  Tested by `tests/83-list-byref/` (9 test cases).

### Added
- **`RecordRef.FieldIndex(n)`** returns a `MockFieldRef` for the nth registered
  field (sorted by field number). Out-of-range index returns a stub with field
  number 0.
- **`RecordRef.Caption`** returns the table caption (delegates to
  `MockRecordHandle.ALTableCaption`).
- **`TestPage field Visible`** — `ALVisible()` method on `MockTestPageField`
  returning `true` (stub).
- **`TestPage field Editable`** — `ALEditable()` method on `MockTestPageField`
  returning `true` (stub).
- **`TestPage field Lookup()`** — `ALLookup()` no-op method on `MockTestPageField`.
- **`TestPage field DrillDown()`** — `ALDrilldown()` no-op method on `MockTestPageField`.
- **`FieldRef.SetRange(MockVariant)`** — explicit overload preventing C# implicit
  conversion to `NavValue?` (which returned null for non-NavValue variant contents).
- **`FieldRef.SetRange(object)`** — overload for `NavComplexValue → object` rewritten
  parameters.
- **`FieldRef.ValidateSafe()`** — no-arg overload (re-validates current value).
- **`FieldRef.CalcField(DataError)`** — overload accepting DataError parameter (no-op).
- **`FieldRef.Clear()`** — resets field value to default.

  Tested by `tests/82-recref-fieldindex/` (10 test cases).

### Fixed
- **`IsInWriteTransaction()` no longer crashes with NullReferenceException.**
  `ALDatabase.ALIsInWriteTransaction()` calls into `NavSession` which is null in
  standalone mode. The rewriter now replaces the call with `false` (no DB
  transactions in the runner).
- **`GuiAllowed` now compiles in standalone mode.** Added `ALGuiAllowed` property
  to `MockSystemOperatingSystem` returning `false` (no UI in standalone mode).
  Previously caused `CS0117` compilation error.
  ([#54](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/54))
- **`FieldRef.Class = FieldClass::Normal` comparison now compiles.** Changed
  `MockFieldRef.ALClass` return type from `int` to `FieldClass` enum, fixing
  `CS0019` operator mismatch error.
  ([#54](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/54))
- **`NavComplexValue` type parameter mismatch resolved.** Added rewriter rule
  replacing `NavComplexValue` with `object` so `MockVariant` and `MockRecordRef`
  can be passed where BC expects `NavComplexValue`.
  ([#54](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/54))

  Tested by `tests/79-gui-fieldclass/` (6 test cases).

### Fixed
- **`exit(this)` in fluent-chaining codeunits now works.** The BC compiler emits
  `__ThisHandle` for codeunit methods that return `Codeunit "Self"` (fluent builder
  pattern). After the rewriter stripped the `NavCodeunit` base class, `__ThisHandle`
  was undefined, causing `CS1061` compilation errors. The rewriter now replaces
  `__ThisHandle` access with `MockCodeunitHandle.FromInstance()`, which wraps the
  live codeunit instance. Tested by `tests/79-exit-this/` (3 test cases).
  ([#45](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/45))

### Added
- **`MockFormHandle` page variable stubs.** `SetTableView(rec)`, `LookupMode`
  (bool property, default false), `Editable` (bool property, default true),
  `PageCaption` (string property, default empty), `Clear()`, and
  `GetRecord(rec)` (1-arg overload) are now available on Page variables.
  Previously caused CS1061 compilation errors when production code used these
  common page-level members. Tested by `tests/79-form-handle-stubs/` (8 test cases).
  ([#51](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/51))
- **`TestPage` custom action invoke (`GetAction`).** `MockTestPageHandle.GetAction(actionHash)`
  returns a no-op `MockTestPageAction` so `TestPage.MyAction.Invoke()` compiles and
  runs without crashing. Previously caused CS1061 because only `GetBuiltInAction`
  (for OK/Cancel) existed. Tested by `tests/79-form-handle-stubs/`.
  ([#52](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/52))
### Added
- **Session API support (`StartSession`, `StopSession`, `IsSessionActive`, `Sleep`).**
  `StartSession` dispatches the target codeunit synchronously via
  `MockCodeunitHandle` (same pattern as `Codeunit.Run`) and returns `true`.
  `StopSession` and `Sleep` are no-ops. `IsSessionActive` returns `false`
  (session already completed synchronously). All four session functions are
  intercepted by the rewriter and redirected to `MockSession`. Previously,
  `StartSession` with a record parameter caused a compilation failure because
  the rewriter stripped `.Target` from the record argument, leaving a
  `MockRecordHandle` where the BC runtime expected `NavRecord`.
  Tested by `tests/79-startsession/` (6 test cases).
  (fixes [#50](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/50))
### Added
- **`RecordRef.Duplicate()`** — `MockRecordRef.ALDuplicate()` returns a copy of
  the RecordRef pointing to the same table with copied field data and filters.
  ([#53](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/53))
- **`RecordRef.ReadIsolation` (no-op)** — `MockRecordRef.ALReadIsolation` setter
  accepts isolation level assignments without crashing. Getter returns default.
  ([#53](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/53))
- **`InStream` assignment (`InStr2 := InStr1`)** — `MockInStream.ALAssign()`
  copies the source stream's buffer and position into the target.
  ([#53](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/53))
- **`Record.ReadIsolation` already supported** — `MockRecordHandle.ALReadIsolation`
  was already implemented; confirmed working with test coverage.
  ([#49](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/49))

  Tested by `tests/80-recref-isolation/` (5 test cases).

## [1.0.8] — 2026-04-12

### Added
- **JSON types (`JsonObject`, `JsonArray`, `JsonToken`, `JsonValue`) now work.**
  The real BC JSON types (`NavJsonObject`, `NavJsonArray`, `NavJsonToken`,
  `NavJsonValue`) from `Microsoft.Dynamics.Nav.Ncl.dll` are used directly for
  most operations (Add, Get, Contains, Remove, Replace, AsValue, AsText,
  AsInteger, AsBoolean, Count, etc.). Only `WriteTo`, `ReadFrom`, `SelectToken`,
  and `SelectTokens` are intercepted by the rewriter and redirected to
  `MockJsonHelper`, which performs the same Newtonsoft.Json operations without
  going through BC's `TrappableOperationExecutor` / `NavEnvironment` (which
  crash in standalone mode). Tested by `tests/77-json-types/` (15 test cases).
  ([#47](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/47))
- **BLOB / InStream / OutStream support.** `MockBlob` replaces `NavBLOB` as an
  in-memory byte buffer. `MockInStream` and `MockOutStream` replace `NavInStream`
  and `NavOutStream` respectively. `MockStream` replaces the static `ALStream`
  helper class. Supports the common test pattern: write text to a BLOB field via
  `CreateOutStream` + `WriteText`, read it back via `CreateInStream` + `ReadText`,
  and check `HasValue`. BLOB fields on records auto-persist `MockBlob` instances
  so writes survive across reads. Tested by `tests/78-blob-stream/` (6 test cases).
  (fixes [#46](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/46))
- **`TestPage.Caption`, `.First()`, `.GoToKey()`, `.Filter.SetFilter()` stubs.**
  `MockTestPageHandle` now supports `ALCaption` (returns `"TestPage"`), `ALFirst()`
  (returns `true`), `ALGoToKey(DataError, params NavValue[])` (returns `true`), and
  `ALFilter` property returning `MockTestPageFilter` with `ALSetFilter(int, string)`
  no-op. These previously caused CS1061 compilation errors when test codeunits used
  TestPage navigation/filter members. Tested by `tests/74-testpage-navigation/`
  (6 test cases). ([#37](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/37))
- **`TestPage` field `Caption` property.** `MockTestPageField.ALCaption` returns
  a stub empty string, matching the BC compiler's `tP.GetField(hash).ALCaption`
  call pattern. Previously caused CS1061 compilation error.
  ([#38](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/38))
- **`RecordRef.SetLoadFields` no-op.** `MockRecordRef.ALSetLoadFields(DataError,
  params int[])` accepts the BC compiler's lowered call and does nothing — all
  fields are always in memory in standalone mode. Previously caused CS1061.
  ([#39](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/39))
- **`RecordRef.Name` stub.** `MockRecordRef.ALName` returns `"TableN"` (where N
  is the table ID) or empty string when no table is open. Previously caused
  CS1061 compilation error.
  ([#40](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/40))

  Tested by `tests/74-mock-stubs/` (8 test cases covering all 3 additions plus
  the existing `Page.Update()` no-op (#41)).
- **Built-in Library - Variable Storage stub** (codeunit 131004). An AL stub
  (`stubs/LibraryVariableStorage.al`) is auto-loaded alongside the Assert stub,
  and `MockVariableStorage` provides an in-memory FIFO queue at runtime.
  Supports `Enqueue`, `DequeueText`, `DequeueInteger`, `DequeueDecimal`,
  `DequeueBoolean`, `DequeueDate`, `DequeueVariant`, `AssertEmpty`, `Clear`,
  and `IsEmpty`. Tested by `tests/75-library-variable-storage/` (9 test cases).
  (fixes #43)

### Fixed
- **NavScope conversion gap in cross-codeunit dispatch.** When an AL method
  returns a Record or Interface, the BC compiler adds a hidden `NavScope`
  parameter for ownership tracking. Same-codeunit calls pass the calling
  scope object, but after rewriting scopes extend `AlScope` (not `NavScope`),
  causing Roslyn CS1503 error. The rewriter now replaces `NavScope` with
  `object` so any scope or null can be passed. Tested by
  `tests/76-navscope-dispatch/` (3 test cases).
  (fixes [#44](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/44))
- **`Codeunit.Run()` bool return value.** `MockCodeunitHandle.RunCodeunit` now
  accepts a `DataError` parameter and returns `bool`. When the BC compiler emits
  `NavCodeunit.RunCodeunit(DataError.TrapError, id, rec)` for
  `if Codeunit.Run(id) then`, the `&` operator no longer fails with CS0019
  (`Operator '&' cannot be applied to operands of type 'bool' and 'void'`).
  The rewriter now passes `DataError` through; `TrapError` catches exceptions
  and returns `false`, `ThrowError` propagates. Also keeps the outer `OnRun`
  wrapper method so `RunCodeunit` can dispatch to it via reflection.
  Tested by `tests/75-codeunit-run-bool/`.
  ([#42](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/42))
- **RecordRef assignment (`:=` operator).** `MockRecordRef` was missing the
  `ALAssign` method that the BC compiler emits for `RecRef2 := RecRef1`. This
  caused a CS1061 compilation error excluding any codeunit that assigns one
  RecordRef to another. Tested by `tests/72-recref-assign/`. (fixes #35, #36)

### Added
- **ModalPageHandler dispatch.** `[ModalPageHandler]` procedures now intercept
  `Page.RunModal()` calls. When production code calls `RunModal()` on a page
  variable, `MockFormHandle.RunModal()` looks up the registered handler via
  `HandlerRegistry`, creates a `MockTestPageHandle`, invokes the handler, and
  returns the `FormResult` set by the handler's OK/Cancel action invocation
  (OK maps to `LookupOK`, Cancel maps to `LookupCancel`). Missing handler
  throws a descriptive error. Tested by `tests/73-modal-handler/` (3 test cases).
- **TestPage support.** `NavTestPageHandle` is rewritten to `MockTestPageHandle`.
  Test codeunits can now use `TestPage "X"` variables with `OpenEdit()`,
  `OpenView()`, `OpenNew()`, `Close()`, and `Trap()` lifecycle methods.
  `GetField(hash)` returns `MockTestPageField` supporting `ALSetValue`/`ALValue`
  for field get/set. `GetBuiltInAction(FormResult)` returns `MockTestPageAction`
  with `ALInvoke()` for OK/Cancel actions. Tested by `tests/71-testpage/`.
- **ConfirmHandler / MessageHandler dispatch.** Test codeunits with
  `[HandlerFunctions('MyHandler')]` now dispatch `Confirm()` and `Message()`
  calls to the registered `[ConfirmHandler]` and `[MessageHandler]` procedures.
  The `HandlerRegistry` reads handler names from `[NavTest].Handlers`, finds
  matching `[NavHandler]` methods on the test codeunit, and wires them to
  `MockDialog.ALConfirm` and `AlDialog.Message`. `ByRef<bool>` parameters for
  confirm reply are initialized via delegate field wiring.

### Fixed
- **`CompanyName` / `UserId` crash** (#35). AL built-in functions `CompanyName`,
  `UserId`, `TenantId`, and `SerialNumber` caused `NullReferenceException` at
  `ALDatabase.get_ALCompanyName()` because the BC session is not initialized in
  standalone mode. The rewriter now replaces these `ALDatabase` property accesses
  with empty-string literals.

### Added
- **`--generate-stubs` source filtering.** When source directories are provided
  (`--generate-stubs <packages-dir> <output-dir> <src-dir> ...`), only codeunits
  actually referenced in the AL source are generated. Procedure-level filtering
  further limits each stub to only the methods called in source. Falls back to
  generating all codeunits when no source dirs are given (backward compatible).
- **`--generate-stubs` CLI command.** Scaffolds empty AL stub files from `.app`
  symbol packages. Reads `SymbolReference.json` from each `.app` file in the
  packages directory and emits one `.al` file per codeunit with correct procedure
  signatures, parameter types (including `var`, `Record "X"`, `Enum "X"`, etc.),
  and return types with default `exit(...)` values. Existing files are never
  overwritten, and natively mocked codeunits (e.g. codeunit 130) are skipped
  automatically. Non-codeunit objects (tables, pages, etc.) are counted and
  reported but not emitted.
- **RecordRef + FieldRef runtime support.** `MockRecordRef` now delegates all data
  operations (Insert, Modify, Delete, DeleteAll, FindSet, FindFirst, FindLast,
  Next, Count, IsEmpty, SetRange, SetFilter, Reset) to `MockRecordHandle`,
  sharing the same in-memory table store as typed Record variables.
  `MockFieldRef` provides `ALValue` get/set, `ALNumber`, `ALSetRange`,
  `ALSetFilter`, and `ALValidate`.
  Key operations:
  - `RecRef.Open(tableId)` / `RecRef.Close()`
  - `RecRef.Field(n)` returning a `MockFieldRef` with value read/write
  - `RecRef.FindSet()` + `RecRef.Next()` iteration
  - `RecRef.Insert()` / `Modify()` / `Delete()` / `DeleteAll()`
  - `RecRef.GetTable(Rec)` / `RecRef.SetTable(Rec)` for data copy
  - `FieldRef.SetRange()` / `FieldRef.SetFilter()` for filtering
  - `RecRef.Count()` / `RecRef.IsEmpty()` respecting active filters
- **Rewriter: `NavFieldRef` -> `MockFieldRef`.** The rewriter now replaces
  `NavFieldRef` with `MockFieldRef` (previously passed through to the real BC
  type with `null!` parent, which crashed on any property access).

### Fixed
- **`StrSubstNo` with `Integer` (and other `NavValue`) arguments no longer crashes.**
  `ALSystemString.ALStrSubstNo` is now intercepted by `RoslynRewriter` and
  redirected to `AlCompat.StrSubstNo`, which formats each `%1`/`%2`/… placeholder
  using the session-free `AlCompat.Format()`. Prevents the
  `NullReferenceException` in `NavIntegerFormatter.FormatWithFormatNumber` that
  occurred when `NavSession` is null in the runner context.
  ([#33](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/33))

### Added
- **Per-iteration tracking for loop debugging (`--iteration-tracking`).**
  A new CLI flag instruments `for`/`while`/`do` loops at the Roslyn AST level
  and captures, per loop and per iteration: variable values, console messages,
  and executed statement IDs mapped back to AL source lines. Output is appended
  to `--output-json` as an `iterations[]` array. Nested loops are tracked
  independently with parent/child relationships preserved.
  ([#34](https://github.com/StefanMaron/BusinessCentral.AL.Runner/pull/34))

### Fixed (coverage)
- Coverage line numbers corrected from 0-based to 1-based (off-by-one in
  `CoverageReport`).
- `OnRun_Scope` trigger names are now matched correctly by the scope regex.
- Coverage scope→file mapping no longer bleeds library scopes into user
  coverage output.

## [1.0.7] — 2026-04-11

### Added
- **`RecordRef` row-presence operations now read the in-memory store.**
  `Rec.Open(TableId[, Temporary[, CompanyName]])` followed by
  `IsEmpty` / `FindSet` / `Find` / `Next` / `Count` / `Close` consults
  the same shared table store typed `Record X` variables write to, so
  seeding a row via a typed variable is visible through a subsequent
  `RecRef.Open` on the same table id. Field-level access
  (`RecRef.Field(n).Value`) remains out of scope.
  ([#30](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/30))
- **Plain helper procedures on pages can now be called from tests.**
  `MockFormHandle` remembers the page id (the rewriter's constructor
  handling now keeps it instead of stripping) and exposes
  `Invoke(memberId, args)` that reflects over the generated `Page<N>`
  class using the same scope-name encoding MockCodeunitHandle uses.
  Page triggers, layout, actions, and factboxes remain skipped.
  ([#31](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/31))
- **`[EventSubscriber]` procedures fire when their
  `[IntegrationEvent]` / `[BusinessEvent]` is raised in the same
  compilation unit.** Rewriter replaces `βscope.RunEvent()` with
  `AlCompat.FireEvent(publisherCuId, eventName)` and strips the
  `if (γeventScope == null && …) return;` guard BC emits at the top
  of event methods. `EventSubscriberRegistry` scans the assembly for
  `NavEventSubscriberAttribute` via `CustomAttributeData` (reading
  `targetObjectNo` from the second int positional arg — the first
  is the `ObjectType` enum). Sender / Rec parameters pass `null`,
  matching BC's best-effort contract for standalone dispatch.
  ([#32](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/32))

## [1.0.6] — 2026-04-11

### Added
- **Table `trigger OnInsert()` bodies now run** on `Rec.Insert(true)`.
  Trigger firing uses reflection to instantiate the generated
  `Record<N>` class via `GetUninitializedObject` and overwrite its
  compiler-generated `<Rec>k__BackingField` (and xRec) so the
  trigger's `Rec.SetFieldValueSafe` calls mutate the caller's
  MockRecordHandle field bag in place. Falls back to a no-op for
  tables without a declared trigger. Only fires when `runTrigger=true`
  so `Insert(false)` still skips as before.
  ([#27](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/27))
- **`NumberSequence`** stub, **`NavApp.GetModuleInfo`** stub, and
  **`Hyperlink()`** stub — all no-op runtime calls that used to
  trap into BC's service-tier-dependent code paths and throw
  `NullReferenceException` or assembly-load errors.
  ([#14](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/14),
   [#22](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/22),
   [#24](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/24))
- **`Enum::X.Names()` via an enum instance** (`E := E::Draft; E.Names();`)
  now returns the declared member names. Tracked alongside the #17
  static `Enum::"X".Ordinals()` fix. NavOption instances are tagged
  at construction with their source enum id via a
  `ConditionalWeakTable`; rewriter rewrites `.ALNames` / `.ALOrdinals`
  getters to `AlCompat.GetNamesForOption` / `GetOrdinalsForOption`
  which look up the tag. Reassignments via
  `NavOption.Create(existing.NavOptionMetadata, V)` inherit the tag
  through `AlCompat.CloneTaggedOption`.
  ([#28](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/28))
- **Primary-key uniqueness on `Rec.Insert`** — duplicate inserts now
  throw a BC-style "already exists" error so `asserterror` catches
  them. Only enforced when the PK is registered, which is now
  automatic: `TableFieldRegistry` parses the first declared
  `key(...)` block and calls `MockRecordHandle.RegisterPrimaryKey`
  at pipeline start, so synthetic test fixtures don't need to wire
  the registration themselves.
  ([#29](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/29))

### Fixed
- **Multi-field FlowField `exist(...)`** — follow-up to #15 that the
  reporter hit on 1.0.3. Parser now paren-walks the `exist(...)` body
  manually and splits top-level commas in the `where(...)` list.
  Field IDs resolve through a new transpile-time
  `TableFieldRegistry` instead of depending on the runtime
  `RegisterFieldName` path.
  ([#15](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/15) follow-up)
- **`Rec.Validate("DateFormula field")` after `Evaluate`** —
  `DefaultForType` wasn't initialising DateFormula fields to
  `NavDateFormula.Default`, so the first read from a ByRef<T>
  inside `ALSystemVariable.ALEvaluate` hit a `NavText → NavDateFormula`
  cast error. Added the missing branch.
  ([#25](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/25))
- **`Rec.Get(textFromGuidField)`** — Guid values stored as
  `Text[100]` round-tripped to different string forms (braces,
  case, hyphens) than the raw Guid stored in the PK, so `Get` missed
  the match. Added `PkValuesEqual` helper in `ALGet` that tries
  Guid/decimal parse fallbacks before giving up.
  ([#26](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/26))

## [1.0.5] — 2026-04-11

### Fixed
- **`SetFilter` with Option member-name literals** (e.g.
  `SetFilter(Kind, '<>Red&<>Blue')`) now resolves the literal to its
  ordinal via `EnumRegistry.FindOrdinalByMemberName` before comparing,
  instead of producing a string-form mismatch against the stored
  NavOption ordinal. Also harvests inline `OptionMembers = A,B,C;`
  declarations from table fields so Option fields without a separate
  `enum` object resolve too.
  ([#19](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/19) follow-up)
- **~200 ms per-test spike on the first `SetFilter` over a NavOption
  field**. `MockRecordHandle.NavValueToString` used to fall back to
  `value.ToString()` for NavOption (and any other subtype without an
  explicit branch), which traps into BC's `NavFormatEvaluateHelper`
  → triggers `Microsoft.CodeAnalysis` reference resolution + Roslyn
  overload resolution on first use. The fallback is gone; NavOption,
  NavDate, NavDateTime now have explicit branches, NavDecimal uses a
  cached `PropertyInfo` instead of reflecting per call, and unknown
  types return empty string rather than reaching the slow path.
  ([#23](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/23))

### Internal
- New `AlRunner.Tests/NavValueToStringPerfTests.cs` holds the line:
  every test in `tests/52-setfilter-and/` must run in under 50 ms, so
  any regression back into BC's `NavValue.ToString()` fallback fails
  loudly.

## [1.0.4] — 2026-04-11

### Added
- `NavApp.GetModuleInfo` / `GetCurrentModuleInfo` / `GetCallerModuleInfo`
  routed through a new `MockNavApp` stub. The real `ALNavApp` loads
  `Microsoft.Dynamics.Nav.CodeAnalysis` (not shipped with al-runner),
  so any code path that reached NavApp metadata crashed with an
  assembly-load failure. The stub returns `false` for every lookup
  and leaves the ByRef `ModuleInfo` untouched, matching BC's
  "not found" contract. ([#22](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/22))

### Fixed
- **Multi-field FlowField `exist(...)`** now works. 1.0.3 covered the
  single-field case but the multi-condition variant
  (`exist(Child where(C1 = field(X), C2 = field(Y)))`) silently
  returned false: the `CalcFormulaRegistry` regex was non-greedy and
  stopped at the first `)` — the one closing `field(X)` — so the
  second clause was lost. Parser now paren-walks the `exist(...)`
  body manually, splits top-level commas in the `where(...)` body,
  and resolves child field IDs through a new transpile-time
  `TableFieldRegistry` (previously relied on runtime
  `RegisterFieldName`, which only fired when generated code referenced
  `ALFieldNo(name)` explicitly).
  ([#15](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/15) follow-up)

## [1.0.3] — 2026-04-11

### Added
- `CHANGELOG.md` shipped inside the NuGet package; `<PackageReleaseNotes>`
  points nuget.org at it.
- Publish workflow now creates a GitHub Release on tag push, seeded with
  the matching `CHANGELOG.md` section and the `.nupkg` attached.
- Missing-dependency diagnostic now enriches with a namespace-mismatch
  hint when a stub with the matching type+name was loaded under a
  different namespace. ([#9](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/9))
- Server mode: multi-slot LRU cache (8 slots) keyed by a per-file
  fingerprint, and the `runTests` response now includes a `changedFiles`
  array on cache miss so IDE integrations can show change-aware
  feedback. Bouncing between projects in one session no longer
  invalidates the previous entry.
  ([#10](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/10) — MVP; full dep-graph partial recompile still open)
- **Per-statement value capture**: `--capture-values` now emits a
  Quokka-style timeline of intermediate values, not just a
  final-state snapshot. A new `ValueCaptureInjector` pass injects
  `ValueCapture.Capture(...)` after each scope-field assignment,
  keyed by the neighboring `StmtHit(N)` so captures map back to AL
  source lines. Post-test reflection-based capture is kept as a
  fallback for variables the injector can't reach.
  ([#11](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/11))
- **Server `execute` command**: new JSON-RPC command that accepts
  either inline AL (`code`) or `sourcePaths` and runs the first
  codeunit's `OnRun` trigger in run-mode. Response mirrors
  `runTests` plus captured `messages` and optional `capturedValues`.
  ([#12](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/12))
- **Column precision in error mapping**: `TestResult` and
  `--output-json` now include `alSourceColumn` alongside
  `alSourceLine`. `FormatDiagnostic` emits `[AL line ~N col M in X]`.
  The existing `CoverageReport.ParseSourceSpans` encoding already
  carried columns; they were discarded.
  ([#13](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/13))
- **`Enum::X.Ordinals()` / `.Names()`** resolve against a transpile-time
  `EnumRegistry` built from the AL source. BC inlines enums so runtime
  reflection can't recover the member list. ([#17](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/17))
- **Enum-implements-interface dispatch** (`Flag := Strategy;`). BC stores
  the NavOption directly in the interface handle; `MockInterfaceHandle`
  now intercepts it, looks up the per-value
  `Implementation = "Iface" = "Codeunit"` mapping in `EnumRegistry`,
  and resolves the codeunit through the new `CodeunitNameRegistry`.
  ([#20](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/20))
- **Table `InitValue` defaults** applied by `Rec.Init()` via a new
  `TableInitValueRegistry` — supports Boolean, Integer, Decimal, Text
  and Enum member init values.
  ([#18](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/18))
- **FlowField `exist()` `CalcFields`** evaluated against in-memory
  tables via a new `CalcFormulaRegistry`. Supports
  `where(field = field(...))` and `where(field = const(...))`
  conditions; `count` / `sum` / `lookup` still return defaults.
  ([#15](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/15))
- **`NumberSequence`** replaced with a process-local
  `MockNumberSequence` keyed by name. `Exists` / `Insert` / `Next` /
  `Current` / `Restart` no longer throw `NullReferenceException` via
  `NavSession`. ([#14](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/14))
- **`Page "X"` local variables** transpile to a `MockFormHandle`
  stub (like the existing `MockInterfaceHandle` / `MockRecordRef`).
  ([#21](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/21))

### Fixed
- **`SetFilter` AND operator (`&`)** — AL filter expressions with AND
  chains were silently OR-ed, matching too many rows.
  `MatchesFilterExpression` now splits on `|` (OR) first, then on
  `&` (AND) inside each alternative, matching BC's precedence.
  Wildcards, `..` ranges, `@` case-insensitive, and per-field
  AND-across-fields all still work. `%1..%n` placeholder substitution
  covered for integer and text values, including inside mixed AND/OR
  precedence expressions.
  ([#19](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/19))
- **`Page.Run(Page::X, Rec)` / `Page.RunModal`** with fully-qualified
  `NavForm` method access, and `Page "X"` local variable initialisation
  via `NavFormHandle` — both no longer cascade-exclude the containing
  codeunit. (Follow-up to #6, with a real repro via #21.)
- **`RecordRef` 3-arg `Open(tableId, temporary, company)`** now has
  matching `ALOpen(CompilationTarget, int, bool, string)` overloads,
  and `ALIsEmpty` is exposed as a property to match BC's lowering of
  `!recRef.IsEmpty`.
  ([#16](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/16))
- `AL0791 namespace unknown` on an unused `using` directive no longer
  blocks compilation; added to the ignored-error set alongside
  `AL0432` / `AL0433`. Genuine unresolved uses still surface as
  separate errors. ([#8](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/8))
- Regression test for single-arg `Record.Validate("Field")` covering
  Decimal, DateFormula, and error propagation paths. The underlying
  2-arg `ALValidateSafe` overload was added before the report was
  filed; this commit just locks the behavior in.
  ([#7](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/7))

### Internal
- `Pipeline.Run` now redirects both `Console.Out` and `Console.Error`
  into the captured `StringWriter` instances for the duration of the
  run, so `AlDialog.Message` and `PrintResults` no longer corrupt
  the server's stdin/stdout JSON protocol.

## [1.0.2] — 2026-04-11

### Fixed
- `Page.RunModal(PageId, Rec)` as a bare statement no longer emits
  invalid C# (`default(FormResult);`). Strips `NavForm.Run/RunModal/SetRecord`
  at statement level. ([#6](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/6))
- `[TryFunction]`-attributed procedures now compile and run: `AlScope`
  gains `TryInvoke(Action)` / `TryInvoke<T>(Func<T>)` overloads that
  execute the delegate, catch any exception, and return true/false.
  ([#4](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/4))
- `List of [Interface X]` no longer cascades-excludes the containing
  object. New `MockObjectList<T>` replaces BC's `NavObjectList<T>`
  (which requires `T : ITreeObject` and a non-null Tree handler),
  and `ALCompiler.ToInterface(this, x)` is rewritten to
  `MockInterfaceHandle.Wrap(x)`.
  ([#3](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/3))
- Declaring `var RecRef: RecordRef` no longer cascades-excludes the
  containing codeunit. `NavRecordRef` is rewritten to a new
  parameterless `MockRecordRef` stub with no-op Open/Close/IsEmpty/
  Find/Next/Count. Consistent with the documented policy that
  RecordRef/FieldRef compile but do not function at runtime.
  ([#5](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/5))
- `AL0791 namespace unknown` on an unused `using` directive no longer
  blocks compilation; added to the ignored-error set alongside
  `AL0432` / `AL0433`. Genuine unresolved uses still surface as
  separate errors. ([#8](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/8))

### CI
- Publish workflow now mirrors the test matrix: runs the C# test
  project and excludes `tests/39-stubs/` from the bulk run, invoking
  it separately with `--stubs`. Builds `AlRunner.slnx` so the test
  DLL exists by the time `dotnet test --no-build` runs.

## [1.0.1] — 2026-04-10

### Changed
- Per-suite test invocation restored (single-invocation run had ID
  conflicts); test timings back to ~75 s total but reliable.

## [1.0.0] — 2026-04-10

### Added
- `--output-json` machine-readable test output.
- `--server` long-running JSON-RPC daemon over stdin/stdout.
- `--capture-values` variable-value capture for Quokka-style inline
  display.
- `--run <ProcedureName>` single-procedure execution.
- Error line mapping via last-statement tracking.
- C# test infrastructure (`AlRunner.Tests/`) covering pipeline,
  server, capture-values, single-procedure, error mapping and
  incremental server-mode caching.

### Changed
- All BC versions 26.0 → 27.5 now run on every push via the test
  matrix workflow.

## [0.2.0] — 2026-04-10

### Added
- `--coverage` Cobertura XML output wired into CI job summaries.
- NuGet package ID standardized to `MSDyn365BC.AL.Runner`.

## [0.1.0] — 2026-04-10

Initial release — AL transpile + Roslyn rewriter + in-memory execution
for pure-logic codeunits. No BC service tier, no Docker, no SQL, no
license. Test runner with `Subtype = Test` discovery and `Assert`
codeunit mock.

[1.0.7]: https://github.com/StefanMaron/BusinessCentral.AL.Runner/releases/tag/v1.0.7
[1.0.6]: https://github.com/StefanMaron/BusinessCentral.AL.Runner/releases/tag/v1.0.6
[1.0.5]: https://github.com/StefanMaron/BusinessCentral.AL.Runner/releases/tag/v1.0.5
[1.0.4]: https://github.com/StefanMaron/BusinessCentral.AL.Runner/releases/tag/v1.0.4
[1.0.3]: https://github.com/StefanMaron/BusinessCentral.AL.Runner/releases/tag/v1.0.3
[1.0.2]: https://github.com/StefanMaron/BusinessCentral.AL.Runner/releases/tag/v1.0.2
[1.0.1]: https://github.com/StefanMaron/BusinessCentral.AL.Runner/releases/tag/v1.0.1
[1.0.0]: https://github.com/StefanMaron/BusinessCentral.AL.Runner/releases/tag/v1.0.0
[0.2.0]: https://github.com/StefanMaron/BusinessCentral.AL.Runner/releases/tag/v0.2.0
[0.1.0]: https://github.com/StefanMaron/BusinessCentral.AL.Runner/releases/tag/v0.1.0
