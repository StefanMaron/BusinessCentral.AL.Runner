# Changelog

All notable changes to this project are documented here. Format based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versioning follows
[SemVer](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
