# Changelog

All notable changes to this project are documented here. Format based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versioning follows
[SemVer](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **`System.Sleep` coverage (#462)** — `Sleep(Milliseconds)` is now a confirmed no-op
  in standalone mode. The existing `RoslynRewriter` rule rewrites `NavSession.Sleep(ms)`
  to `MockSession.Sleep(ms)` (a no-op stub). New suite `tests/bucket-2/152-sleep`
  adds 5 proving tests: Sleep(0) and Sleep(1) complete without error, return values
  after Sleep are correct, and error handling works normally after Sleep.
  Coverage map: `System.Sleep` moved from `gap` to `covered`.
- **`actionref_declaration` coverage (#388)** — Pages and page extensions containing
  `actionref` sections (promoted-action bindings) now compile and run correctly.
  The existing `RoslynRewriter` already handles the BC-generated C# for actionref
  (the actionref declarations are inside `InitializeComponent` which is stripped).
  New suite `tests/bucket-1/72-actionref` adds 2 proving tests confirming that a
  codeunit in the same compilation unit as a page with actionref compiles and executes.
  Coverage map: `actionref_declaration` moved from `gap` to `covered`.
- **`Database.SelectLatestVersion` coverage (#313)** — `SelectLatestVersion()` was already
  a no-op (stripped by `StripEntireCallMethods` in `RoslynRewriter`) but had no proving
  tests. New suite `tests/bucket-1/58-database-select-latest-version` adds 3 tests:
  call without error, call after insert still shows record with correct field value,
  multiple calls are all no-ops. Coverage map: `Database.SelectLatestVersion` moved
  from `gap` to `covered`.
- **`UserId()` default stub value fix (#314)** — `AlScope.UserId` previously defaulted
  to `""` (empty string), causing `Assert.AreNotEqual('', UserId())` to fail. Changed
  default to `"TESTUSER"` so AL code calling `UserId()` gets a stable non-empty value
  without requiring the `--user-id` CLI flag. New suite `tests/bucket-1/57-userid`
  adds 2 proving tests: UserId is non-empty, UserId is consistent across calls.
  Coverage map: `Database.UserId` moved from `gap` to `covered`.
- **`Database.SessionId` (#316)** — `SessionId()` now returns a stable non-zero integer (1).
  The BC compiler lowers this global function to `ALDatabase.ALSessionId` (a property access).
  `RoslynRewriter` now redirects that to `MockSession.GetSessionId()`. New suite
  `tests/bucket-1/58-database-sessionid` proves: positive call returns > 0, and consecutive
  calls return the same value. Coverage map: `Database.SessionId` moved from `gap` to `stub`.
- **`Guid.IsNullGuid` (#318)** — `IsNullGuid(G)` now correctly returns `true` for the
  all-zeros GUID and `false` for any non-zero GUID. The BC compiler lowers this global
  function to `ALDatabase.ALIsNullGuid(G)`; `RoslynRewriter` now redirects that to
  `AlCompat.ALIsNullGuid(G)`, which checks `NavGuid.ToGuid() == Guid.Empty`. New suite
  `tests/bucket-1/58-is-null-guid` proves both directions. Coverage map: `Guid.IsNullGuid`
  moved from `gap` to `covered`.
- **`Record.IsEmpty` coverage (#299)** — `ALIsEmpty` was already implemented in
  `MockRecordHandle` (`GetFilteredAndMarkedRecords().Count == 0`) but had no proving
  tests. New suite `tests/bucket-1/56-isempty` adds 5 proving tests: empty table →
  true, records exist → false, filter excludes all → true, filter matches some →
  false, Reset clears filter → false. Coverage map: `Table.IsEmpty` moved from `gap`
  to `covered`.
- **`Guid.CreateGuid` (#310)** — `CreateGuid()` now returns unique `NavGuid` values.
  The BC compiler lowers this global function to `ALDatabase.ALCreateGuid()`.
  `RoslynRewriter` now redirects that call to `AlCompat.ALCreateGuid()`, which wraps
  `System.Guid.NewGuid()`. New suite `tests/bucket-1/57-create-guid` proves the function
  returns a non-empty GUID, that two calls return distinct values, and that calling via
  a codeunit helper works correctly. Coverage map: `Guid.CreateGuid` moved from `gap`
  to `covered`. (`CreateSequentialGuid` remains a gap — see #318.)
- **`Database.Commit` coverage (#311)** — `Commit()` was already a no-op (stripped by
  `StripEntireCallMethods` in `RoslynRewriter`) but had no proving tests. New suite
  `tests/bucket-1/56-database-commit` adds 4 tests: commit without error, commit after
  insert preserves records, multiple commits are all no-ops, commit after modify preserves
  modified values. Coverage map: `Database.Commit` moved from `gap` to `covered`.
- **`Table.SetAscending` coverage (#305)** — New suite `tests/bucket-1/55-setascending`
  adds 6 proving tests: default PK ascending, `SetAscending(Name, false)` → descending,
  explicit `SetAscending(Name, true)`, composite key with mixed directions (Priority asc +
  Code desc), `Reset()` restores default ascending, and `FindLast` with descending key.
  Coverage map: `Table.SetAscending` moved from `gap` to `covered`.
- **`Table.TestField` enum coverage (#302)** — New suite `tests/bucket-1/54-testfield-enum`
  adds 6 proving tests for `TestField` with enum fields: matching enum value passes,
  wrong value errors, default-vs-non-zero, non-default passes the no-value check, and
  default value fails the no-value check. Coverage map: `Table.TestField` moved from
  `gap` to `covered` (also surfaces the existing `27-testfield-error` suite).
- **`Table.FindSet` / `SetCurrentKey` iteration coverage (#301)** — New suite
  `tests/bucket-1/53-findset` adds 8 proving tests: PK-order iteration (no
  `SetCurrentKey`), Name-key iteration, Priority-key iteration, filter+key
  combined, `FindSet` returns true/false, empty table → false, no-match filter
  → false, and `FindLast` with `SetCurrentKey`. `SetCurrentKey` sort was already
  wired via the PK-sort fix in #297. Coverage map: `Table.FindSet` moved from
  `gap` to `covered`.
- **`Table.FindFirst` coverage + PK sort fix (#297)** — New suite
  `tests/bucket-1/52-findfirst` adds 7 proving tests for `FindFirst`. The tests
  revealed a bug: `GetFilteredRecords` only sorted when `SetCurrentKey` had been
  called; without it, records were returned in insertion order instead of PK order.
  Fixed `GetFilteredRecords` to always sort — by `_currentKeyFields` when set,
  falling back to PK fields otherwise (matches BC behaviour). Also marks
  `Table.FindLast` as covered (existing suite `tests/bucket-1/258-findlast`).
  Coverage map: both `Table.FindFirst` and `Table.FindLast` moved from `gap` to
  `covered`.
- **`Table.ModifyAll` coverage.yaml fix (#292)** — `ALModifyAllSafe` was already
  implemented in `MockRecordHandle`; coverage map incorrectly listed it as `gap`.
  Existing suite `tests/bucket-1/30-modify-all` has 4 proving tests (update all,
  filter-scoped update, empty table no-op, runTrigger overload). Coverage map:
  `Table.ModifyAll` moved from `gap` to `covered`.
- **`Record.CalcSums` implementation (#293)** — `ALCalcSums` was a no-op stub; now
  sums each requested field across all records matching the current filters and writes
  the result back into the record's fields. Integer fields stay `NavInteger`; Decimal
  fields become `NavDecimal`. New suite `tests/bucket-1/51-calcsums` adds 6 proving
  tests: Decimal sum, Integer sum, filtered sum, multi-field sum, empty result (→ 0),
  and filter-excludes-all (→ 0). Coverage map: `Table.CalcSums` moved from `gap` to
  `covered`.
- **`Record.Copy` fix + coverage (#295)** — `ALCopy` only copied filters when
  `shareFilters=true` (wrong parameter name and wrong default). Fixed: filters
  are now always copied (AL `Copy` always transfers both field values and
  filters). Added ShareTable=true support for temporary records: when
  `shareTable=true` both temp record variables share the same row list so that
  inserts/deletes via one are visible via the other. New suite
  `tests/bucket-1/51-record-copy` adds 7 proving tests: field value transfer,
  filter transfer (GetFilters match, Count restricted), ShareTable=true shares
  temp data and new inserts are visible in both, ShareTable=false creates
  independent temp copies, and source filters not mutated by target changes.
  Coverage map: `Table.Copy` moved from `gap` to `covered`.
- **`Record.DeleteAll` coverage (#289)** — `ALDeleteAll` was already fully
  implemented in `MockRecordHandle` (no-filter variant clears all rows; filter
  variant removes only matching rows). New suite `tests/bucket-1/50-deleteall`
  adds 6 proving tests: delete all, SetRange partial delete, SetFilter partial
  delete, empty table (no error), non-matching filter on empty table, and
  count-after-partial-delete. Coverage map: `Table.DeleteAll` moved from `gap`
  to `covered`.
- **`table_relation_expression` syntax coverage (#285)** — tables with
  `TableRelation` field properties compile and all record operations work.
  New suite `tests/bucket-1/49-tablerelation` adds 6 tests: Insert+Get
  with no parent (FK not enforced), Insert with existing parent, Modify,
  Delete, Count with filter, and the explicit negative test proving orphan
  inserts succeed without error. Coverage map:
  `table_relation_expression` moved from `gap` to `covered`.
- **`Table.Validate` coverage.yaml fix (#270)** — `Table.Validate` was
  listed as `status: gap` in `docs/coverage.yaml` despite 5 proving tests
  already existing in `tests/bucket-1/18-validate-trigger` (OnValidate fires
  on name-uppercase, computed amount, direct-Validate, direct-assign-skips,
  zero-quantity). Corrected status to `covered`.
- **`Record.SetRecFilter` composite-PK fix + coverage (#286)** — `ALSetRecFilter`
  previously only filtered on field 1 of the PK, leaving composite-PK records
  under-filtered. Fixed to iterate all PK fields via `GetPrimaryKeyFields()` and
  set a range filter on each. New suite `tests/bucket-1/49-setrecfilter` adds 7
  proving tests: single-field PK (Count=1, FindSet iterates only current record,
  correct field data returned), composite PK (Count=1, correct row isolated),
  Reset clears the filter, and SetRecFilter on one variable does not affect
  another variable. Coverage map: `Table.SetRecFilter` moved from `gap` to `covered`.
- **`Table.CopyFilters` coverage (#274)** — `ALCopyFilters` was already
  implemented in `MockRecordHandle` but had no proving tests. New suite
  `tests/bucket-1/48-copyfilters` adds 7 tests: SetRange transfer, SetFilter
  expression transfer, multi-field transfer, overwrite of existing target
  filters, Count respects copied filter, empty source clears target filters,
  and source is not mutated by the copy. RED confirmed by temporarily no-oping
  `ALCopyFilters`. Coverage map: `Table.CopyFilters` moved from `gap` to
  `covered`.
- **`Record.Rename` coverage (#281)** — `MockRecordHandle.ALRename` was already
  fully implemented but `Table.Rename` remained `status: gap` in coverage.yaml
  because the existing `tests/bucket-2/107-rename` suite was not registered.
  That suite provides 9 proving tests: single-field PK update + old-key removal,
  composite PK rename, duplicate-key error, non-existent-record error, return-value
  (false) variants, and count-preservation. Coverage map: `Table.Rename` moved
  from `gap` to `covered`.
- **`fieldgroups` section syntax coverage (#279)** — tables with `fieldgroups`
  declarations (e.g. `DropDown`, `Brick`) compile and all record operations
  work correctly. New suite `tests/bucket-1/48-fieldgroups`: 5 positive tests
  (Insert+Get, Modify, Delete, FindSet iteration, Count) and 2 negative tests
  (Get non-existent key, duplicate-key Insert error). Coverage map:
  `fieldgroup_declaration` and `fieldgroups_section` moved from `gap` to `covered`.
- **`Table.SetCurrentKey` iteration-order coverage (#223)** — `ALSetCurrentKey`
  was already implemented in `MockRecordHandle` but had no proving tests that
  verify `FindSet`/`Next` actually returns records in the specified field order.
  Adds four new tests to `tests/bucket-2/109-currentkey`: sort by Name
  (ascending), sort by Sequence (ascending), default PK sort (no SetCurrentKey),
  and descending Name order via `SetAscending`. RED confirmed by temporarily
  setting `if (false && _currentKeyFields ...)` — all four new tests fail.
  Coverage map: `Table.SetCurrentKey` moved from `gap` to `covered`.
- **`Enum.Names` / `Enum.Ordinals` coverage (#271)** — both were `status: gap`
  in coverage.yaml despite having basic suites. Extended `tests/bucket-2/61-enum-names`
  with 4 new proving tests: second/third name by index, `Contains` positive, type-qualifier
  syntax (`Enum::"T".Names()`), and 2 negative tests (unknown name not contained,
  count ≠ 2). Extended `tests/bucket-2/50-enum-ordinals` with 6 new proving tests:
  all 4 ordinals by index, `Contains` positive, instance-variable syntax, and 2 negative
  tests (ordinal 9 not contained, count ≠ 3). Coverage map: `EnumType.Names` and
  `EnumType.Ordinals` moved from `gap` to `covered`.
- **Test coverage: `Record.Get` by primary key (#275)** — `MockRecordHandle.ALGet` was
  already implemented; new suite `tests/bucket-1/48-record-get` adds 6 proving tests:
  single-key Get retrieves correct record, Get returns true on match, Get on missing key
  throws "not found" error, Get distinguishes between different keys, composite PK Get
  loads the correct row, and composite-key not-found also errors correctly.
- **Record.Count with SetFilter expressions coverage (#260)** — `Count` with
  SetFilter comparators / OR-lists / range expressions was already honoured
  in `MockRecordHandle.ALCount` but had no dedicated proving test. New suite
  `tests/bucket-1/46-count-setfilter` covers `'>1'`, `'<2'`, `'<>2'`,
  `'1|3'`, `'2..3'`, no-match (0), and restoration after `Reset`. RED
  confirmed by pointing `ALCount` at the unfiltered row list.
- **Field `InitValue` property applied on `Record.Init()` (#237)** — `MockRecordHandle.ALInit()`
  now calls `TableInitValueRegistry.ApplyInitValues` which parses `InitValue = X` attributes
  from AL field declarations and applies them when `Init()` is called. Supports Integer,
  Text, Boolean, Decimal, and Enum field types. Fields without `InitValue` continue to
  receive type defaults (0, empty string, false). New suite `tests/bucket-1/47-initvalue`
  covers Integer/Text/Boolean/Decimal InitValue application, fields-without-InitValue
  staying at defaults, and `Init()` overwriting previously-set values.
- **`Record.Next(Steps)` overload (#262)** — `MockRecordHandle.ALNext(int)` is
  new (previously only the parameterless `ALNext()` existed). Positive steps
  move forward, negative steps move backward, and the return value is the
  signed number of steps actually moved — clamped to the remaining records
  at either end so the absolute return may be less than the request. Honors
  active filters (advances within the filtered result set). New suite
  `tests/bucket-1/45-next-steps` covers Next(1), Next(N) skip, past-end,
  at-end (returns 0), negative-step backward, past-start, and filter
  traversal. RED confirmed by compile error (overload missing). Coverage
  map: `Table.Next` moved from `gap` to `covered`.
- **Test coverage: `Record.FindLast()` (#258)** — `MockRecordHandle.ALFindLast`
  was already implemented; new suite `tests/bucket-1/258-findlast` adds 6
  proving tests: unfiltered positions to last PK, empty table returns false,
  filtered set returns filtered last, filter with no matches returns false,
  `SetFilter('<>Z')` proves filters are honoured (returns M, not Z), and
  `FindFirst`/`FindLast` return different records.
- **Record.Count with filters coverage (#257)** — `MockRecordHandle.ALCount`
  already honoured active filters via `GetFilteredAndMarkedRecords`, but had
  no dedicated proving test. New suite `tests/bucket-1/44-count-filtered`
  covers empty table (0), total count (5), filtered subset (3 for Status=1,
  2 for Status=2), zero-match filter, restoration after `Reset`, and range
  filter (`Amount` in 20..40). RED confirmed by temporarily pointing
  `ALCount` at the unfiltered row count. Coverage map: `Table.Count` moved
  from `gap` to `covered`.
- **`Record.SetCurrentKey` traversal-order coverage (#264)** — sort-order
  behavior was implemented in `MockRecordHandle` but had no proving tests for
  FindSet/Next traversal. Extended `tests/bucket-2/109-currentkey` with 5 new
  tests: SetCurrentKey by Name changes traversal order, SetCurrentKey by
  Sequence changes traversal order, resetting to primary key restores PK order,
  Name sort does not traverse in PK order (negative), and descending sort
  reverses traversal order.
- **Test coverage: `Record.IsTemporary()` (#254)** — `MockRecordHandle.ALIsTemporary`
  was already implemented; new suite `tests/bucket-1/254-record-istemporary`
  adds 5 proving tests: normal Record → false, `temporary` Record → true,
  stays temporary after Insert, temp store is isolated from the persisted
  table, and normal Record stays non-temporary after Insert.
- **Record.HasFilter coverage (#253)** — `MockRecordHandle.ALHasFilter`
  (`_filters.Count > 0`) now has dedicated proving tests. New suite
  `tests/bucket-1/43-hasfilter` covers fresh record (false), `SetRange`
  (true), `SetFilter` (true), `Reset()` (false), and clearing filters
  one-by-one (remains true until last cleared). RED confirmed by
  temporarily stubbing `ALHasFilter` to always return false. Coverage map:
  `Table.HasFilter` moved from `gap` to `covered`.
- **Record.LockTable coverage (#250)** — `ALLockTable` in `MockRecordHandle`
  is a correct no-op (the runner has no SQL transaction isolation) but
  previously had no proving test. New suite `tests/bucket-1/42-locktable`
  covers: LockTable does not throw on an empty table, subsequent Modify /
  Insert / Delete succeed, and repeated LockTable calls are idempotent. RED
  confirmed by temporarily making ALLockTable throw. Coverage map:
  `Table.LockTable` moved from `gap` to `covered`.
- **`CompanyName()` configurable (#242)** — was hard-coded to empty string.
  Now three-way configurable:
    * `--company-name <name>` CLI flag sets the default returned between tests.
    * AL tests can set it at runtime via the new stub codeunit
      `131100 "AL Runner Config"` → `SetCompanyName(Name: Text)`.
    * Defaults to empty string when neither is used (backwards-compatible).
  Per-test reset restores the CLI default so tests don't leak across each
  other. Rewriter maps `ALDatabase.ALCompanyName` to `MockSession.GetCompanyName()`.
  New suite `tests/bucket-1/242-company-name` covers default, set, clear,
  per-test reset, and composition with `StrSubstNo`.
- **`SetFilter` format-placeholder tests (#245)** — added proving tests for
  `SetFilter` with `%1`, `%2` substitution arguments: single placeholder (`>%1`),
  two-placeholder AND expression (`>%1&<%2`), wildcard suffix (`%1*`), exact
  equality, and integer field range. New suite
  `tests/bucket-1/44-setfilter-placeholder` confirms positive matches and
  negative exclusion.
- **`UserId()` configurable (#243)** — `UserId()` now returns the value set via
  the new `--user-id <value>` CLI flag (or `PipelineOptions.UserId`), defaulting
  to empty string for backwards compatibility. Tests that branch on user identity
  can now be driven with a configured user ID.
- **Record.GetFilters coverage & field-name fix (#246)** — `Record.GetFilters()`
  now emits real AL field names (e.g. `"Status: 1"`) instead of positional
  stubs (`"Field2: 1"`). `MockRecordHandle.GetFieldNameByNo` now prefers the
  transpile-time `TableFieldRegistry` metadata before falling back to the
  runtime-registered name dictionary. New suite
  `tests/bucket-1/41-getfilters` covers the empty case, single-field filter,
  combined multi-field filter, post-`Reset` clearing, and range-filter
  rendering (`1..5`). Coverage map: `Table.GetFilters` moved from `gap` to
  `covered`.
- **RecordRef.FieldCount coverage (#238)** — `MockRecordRef.ALFieldCount` and
  `MockRecordHandle.FieldCount` already preferred the schema field count
  (from `TableFieldRegistry`) over the runtime written-field count, but this
  behaviour was listed as a limitation and had no proving test. New suite
  `tests/bucket-1/40-recordref-fieldcount` covers fresh RecordRef (3 schema
  fields), invariance after writing, a different 5-field table, and the
  negative case that FieldCount is not the write count. Removed the
  `Record.FieldCount via RecordRef` row from `docs/limitations.md`.

### Changed
- **Coverage: `type_declaration` reclassified as out-of-scope (#232)** — this
  tree-sitter-al node is the `.NET` type alias declared inside
  `dotnet { assembly { type(...) {} } }` blocks (required field `dotnet_type`),
  not a general user-defined type alias. It requires BC runtime .NET interop,
  which is an architectural limit like `assembly_declaration`. Moved from
  `gap` to `out-of-scope` in `docs/coverage.yaml`.

### Added
- **Test coverage: `Record.SetRange(Field)` clears field filter (#240)** — new
  suite `tests/bucket-1/240-setrange-clear` proves that calling
  `SetRange(Field)` with no value argument removes only that field's filter
  while leaving other field filters intact, and that `FindSet()` iterates
  the full record set afterwards. Behaviour was already implemented in
  `MockRecordHandle.ALSetRangeSafe`; this adds the proving tests.
- **Record.TransferFields coverage (#224)** — `ALTransferFields` in
  `MockRecordHandle` now has dedicated proving tests. New suite
  `tests/bucket-1/39-transferfields` verifies matching fields are copied by
  field number (not name), the default overload copies the PK, the
  `TransferFields(src, false)` overload preserves the target's PK, and
  target-only fields (no counterpart on source) remain untouched. Coverage
  map: `Table.TransferFields` moved from `gap` to `covered`.
- **CalcField `lookup` formula coverage (#231)** — the `lookup(...)` CalcFormula
  kind in `MockRecordHandle.ALCalcFields` now has dedicated proving tests. New
  suite `tests/bucket-1/38-lookup-formula` exercises text lookup, decimal
  lookup, first-match disambiguation, and the no-match default path (empty
  text / zero decimal). Coverage map: `lookup_formula` moved from `gap` to
  `covered`.
- **Multi-token decimal format strings (#225)** — `Format(decimal, 0, '<Precision,2:2><Standard Format,0>')`
  now parses every `<...>` token in the picture string instead of only the
  first. For decimals, `<Precision,min:max>` wins over `<Standard Format,N>`
  when both are present. Single-token strings are unchanged. New suite
  `tests/bucket-1/225-format-multi-token` covers integer, fractional, and
  rounding cases.
- **`Record.FieldError` (#228)** — raises a field-level validation error
  (`"<FieldCaption> <Message> in <TableCaption>: <PK>"`). Supports both
  `FieldError(Field)` (default `"must have a value"` message) and
  `FieldError(Field, Text)`. Errors are catchable via `asserterror`.
- **Record.Mark / Record.MarkedOnly / Record.ClearMarks (#226)** — the
  record-variable marking surface is now functional (previously no-ops).
  `Mark(true/false)` flips the mark for the current record, `Mark()` returns
  the current state, `MarkedOnly(true)` filters subsequent `FindSet` /
  `FindFirst` / `FindLast` / `Next` / `Count` / `IsEmpty` iteration to the
  marked subset, and `ClearMarks()` wipes all marks. Marks are per
  record-variable instance, keyed on primary key values. New suite
  `tests/bucket-1/37-record-mark` exercises positive (marked subset),
  negative (MarkedOnly off), and reset (ClearMarks) paths.
- **Enum extension test coverage (#227)** — new suite
  `tests/bucket-1/36-enum-extension` confirms that `enumextension` objects
  transpile and run correctly: base enum values retain their ordinals,
  extension values resolve to their declared ordinals (100, 101), and
  `Format()` / `AsInteger()` work against extension members. Coverage map
  updated: `enumextension_declaration` moved from `gap` to `covered`.

## [1.0.15] - 2026-04-15

### Added
- **`--strict` flag** — New CLI flag that promotes exit code 2 (runner limitations)
  to exit code 1. In strict mode, any non-passing test fails the pipeline — use
  in CI to catch regressions where tests go from passing to blocked. Both CI
  workflows (test-matrix.yml and publish.yml) now use `--strict`.
- **AL language coverage map** — `docs/coverage.yaml` (machine-readable) and
  `docs/coverage.md` (rendered table) track every AL language construct from
  `tree-sitter-al` as covered, gap, not-possible, or out-of-scope. A generation
  script (`scripts/coverage-gen.js`) supports `--fetch`, `--render`, and
  `--validate` modes. CI validates that all covered entries reference existing
  test paths.
- **Runtime-API coverage layer (#202)** — the coverage map now has two layers.
  In addition to the syntax layer (tree-sitter constructs), a new `runtime-api`
  layer enumerates every BC built-in method from
  `Microsoft.Dynamics.Nav.CodeAnalysis` symbol tables via
  `tools/RuntimeApiEnumerator`, producing `scripts/runtime-api.json`
  (1294 methods across 95 types). `scripts/coverage-gen.js` scans
  `AlRunner/Runtime/Mock*.cs` + `AlScope.cs` for AL-prefixed methods to
  determine per-method coverage. Each `docs/coverage.yaml` entry now carries a
  `layer: syntax | runtime-api` field; curation is preserved across
  regenerations. CI runs `scripts/tests/coverage-gen.test.js` plus the schema
  validator.
- **HTTP mock types** — `NavHttpClient`, `NavHttpResponseMessage`, `NavHttpContent`,
  `NavHttpHeaders`, and `NavHttpRequestMessage` are replaced with in-memory mocks
  (`MockHttpClient`, `MockHttpResponseMessage`, `MockHttpContent`, `MockHttpHeaders`,
  `MockHttpRequestMessage`) that work without `NavSession`. `HttpContent.WriteFrom(Text)`
  / `ReadAs(var Text)` round-trips text. `HttpResponseMessage` defaults to status 200.
  `HttpHeaders.Add/Contains/Remove` work. `HttpClient.Send/Get/Post/Put/Delete/Patch`
  throw descriptive `NotSupportedException` recommending AL interface injection.
  `HttpContent.WriteFrom(InStream)` / `ReadAs(var InStream)` now round-trip content
  (previously ReadAs returned an empty stream). (#123)
- **RecordRef/FieldRef API completeness** — Mark/MarkedOnly/ClearMarks are now
  functional (in-memory HashSet tracking). FieldRef.GetFilter returns the active
  filter expression. FieldRef.GetRangeMin/GetRangeMax return the active range
  bounds. RecordRef.Ascending setter wires through to the handle's sort direction.
  FieldRef.Record() returns the owning RecordRef. RecordRef.KeyCount/KeyIndex/
  CurrentKeyIndex provide basic key metadata. (#115)
- **KeyRef support** — New `MockKeyRef` class replacing `NavKeyRef`. Provides
  FieldCount, FieldIndex(n), Record, Active, and ALAssign. The RoslynRewriter
  maps `NavKeyRef` → `MockKeyRef` with constructor arg stripping. (#115)
- **ReportHandler dispatch** — `[ReportHandler]` procedures now intercept `Report.Run()`,
  `Report.RunModal()`, and report variable `.Run()`/`.RunModal()` calls. The handler
  receives a `TestRequestPage` parameter, matching BC's test framework semantics.
  Static `Report.Run(id)` / `Report.RunModal(id)` calls (emitted as `NavReport.Run/RunModal`)
  are rewritten to `MockReportHandle.StaticRun/StaticRunModal`. `MockReportHandle.RunModal()`
  and `UseRequestPage(false)` (emitted as `UseRequestForm` property) are now supported.
  Running a report without a handler silently succeeds (no error). (#118)
- **SendNotificationHandler dispatch** — `HandlerRegistry` now supports
  `[SendNotificationHandler]` test handlers. `MockNotification.ALSend()` invokes
  the registered handler (passing a `ByRef<MockNotification>`) so tests can
  intercept and inspect `Notification.Send()` calls. Without a handler, Send
  remains a no-op. (#119)
- **TestPage method stubs** — `MockTestPageHandle` gains `ALEditable` (property,
  returns `true`), `ALValidationErrorCount()` (returns `0`), `ALLast()` (returns
  `false`), `ALPrevious()` (returns `false`), `ALExpand(bool)` (no-op), and
  `ALGetRecord()` (returns empty `MockRecordHandle`). These prevent CS1061
  compilation errors for common TestPage member accesses. (#119)
- **Field metadata infrastructure** — `TableFieldRegistry` now parses and stores
  field-level metadata (name, caption, type, length) and table-level metadata
  (name, caption) from AL source at transpile time. `MockRecordHandle.ALFieldCaption`,
  `ALTableCaption`, `ALTableName` return real values from the registry (falling back
  to stub defaults for unregistered tables). `MockFieldRef.ALName`, `ALCaption`,
  `ALType`, `ALLength` use the registry. `MockRecordRef.ALName` and `ALFieldCount`
  return schema-based values. `MockRecordHandle.FieldCount` returns the schema field
  count when metadata is available. Caption values with embedded apostrophes
  (e.g. `'Vendor''s Name'`) are unescaped correctly. (#114)
- **Temporary records** — `Record "X" temporary` variables now use an isolated in-memory
  store per handle instance, fully separated from non-temporary records of the same table.
  `IsTemporary()` returns the correct value. `RecordRef.Open(tableId, true)` creates a
  temporary RecordRef. (#120)
- **FlowField CalcFormula: count, sum, lookup** — `CalcFields` now evaluates `count(...)`,
  `sum(...)`, and `lookup(...)` formulas in addition to the existing `exist(...)` support.
  Count returns the number of matching rows, Sum aggregates a decimal field, and Lookup
  returns the target field value from the first matching row. (#120)
- **FieldRef enum introspection** — `MockFieldRef` now supports `ALIsEnum`,
  `ALOptionValueCount()`, `ALGetOptionValueName(index)`,
  `ALGetOptionValueCaption(index)`, and `ALGetOptionValueOrdinal(index)`.
  These methods use `TableFieldRegistry` (which now parses `Enum "X"` field
  type declarations) and `EnumRegistry.GetMembersByName()` to resolve enum
  metadata at runtime. (#126)
- **FieldRef.CalcSum** — `MockFieldRef.ALCalcSum()` sums a field's values
  across all filtered records in the underlying table. The result is returned
  via the next `ALValue` read, matching BC's CalcSum semantics. (#126)
- **RecordRef system-field number accessors** — Added `ALSystemCreatedAtNo`
  (2000000001), `ALSystemCreatedByNo` (2000000002), and `ALSystemModifiedByNo`
  (2000000004) to `MockRecordRef`. (#126)
- **ErrorInfo type & collectible errors** — `Error(ErrorInfo)` now uses
  `ErrorInfo.Message` for the error text (previously used `.ToString()` which
  included internal field metadata). Collectible errors are fully supported:
  mark `ErrorInfo.Collectible := true` and annotate procedures with
  `[ErrorBehavior(ErrorBehavior::Collect)]` to collect errors instead of
  throwing. Global functions `HasCollectedErrors()`, `GetCollectedErrors()`,
  `ClearCollectedErrors()`, and `IsCollectingErrors()` all work. (#117)
- **MockNotification** — In-memory replacement for `NavNotification`. Message,
  Send, Recall, SetData/GetData/HasData, AddAction, Id, Scope. Send and Recall
  are no-ops; data store is in-memory; Id auto-generates a Guid. (#121)
- **MockTaskScheduler** — CreateTask dispatches codeunit synchronously via
  MockCodeunitHandle (same pattern as MockSession.StartSession), returns a Guid.
  TaskExists returns false, CancelTask/SetTaskReady are no-ops. (#121)
- **MockDataTransfer** — Minimal stub so code using DataTransfer compiles and
  runs without error. SetTables, AddFieldValue, AddConstantValue, AddJoin,
  AddSourceFilter, CopyFields, CopyRows are all no-ops. (#121)
- **System, Database & Session utility stubs** — `Session.LogMessage()` (no-op),
  `Session.ApplicationArea()` (returns empty string), `Session.GetExecutionContext()` /
  `GetModuleExecutionContext()` (return `ExecutionContext.Normal`),
  `Database.LockTimeout(bool)` (no-op), `CompanyProperty.DisplayName()` / `UrlName()`
  (return stub company values), `RoundDateTime(dt, precision, direction)` (full implementation
  with ms precision and direction rounding). `ProductName.Full/Short/Marketing` use
  real BC types. `NormalDate/ClosingDate` wrappers added with explicit 0D handling. (#185)
- **ReportExtension scope class `Parent` property** — The rewriter now injects a
  public `Parent` property on scope classes (alongside the existing `_parent` field),
  fixing CS1061 errors when BC-generated report extension trigger scopes access
  `Parent` without the `base.` prefix. Also strips the broken `CurrReport` property
  on report extensions (which cast `ParentObject` from the removed base) and injects
  `CurrReport => this` as a self-referencing stub so `CurrReport.Skip()` /
  `CurrReport.Break()` still compile. (#177, #178, #179, #181)
- **Codeunit-not-found diagnostics** — When `Codeunit.Run(id)` fails because
  the target codeunit is absent from the assembly, the error message now:
  identifies system (1–9999) and test-toolkit (130000–139999) ranges, lists
  available codeunit IDs (up to 20), and suggests `--stubs` / `--generate-stubs`
  as resolution. (#176)
- **Cross-extension AL0275/AL0197 suppression** — When multiple AL source directories
  are compiled together (e.g., two extensions), false "ambiguous reference" (AL0275)
  and "already declared" (AL0197) errors from name collisions between different
  extensions are now suppressed. The classifier only suppresses extension object types
  (PageExtension, TableExtension, etc.) and uses a two-pass approach to avoid hiding
  genuine codeunit/table name collisions. (#182)

### Improved
- **XmlPort & Query runtime error messages** — `MockXmlPortHandle.Import/Export`
  and `MockQueryHandle.Open/Read` now throw descriptive `NotSupportedException`
  messages that mention "BC service tier" and suggest "AL interface injection"
  (XmlPort) or "Record operations" (Query) as actionable alternatives. (#124)

### Fixed
- **BigText mock (`MockBigText`)** — `NavBigText` is now replaced with `MockBigText`
  by the rewriter. In BC 28+, `NavBigText`'s static initializer loads
  `Microsoft.BusinessCentral.Telemetry.Abstractions` which is unavailable outside
  the service tier, causing `TypeInitializationException`. `MockBigText` provides
  the same API surface (`ALAddText`, `ALGetSubText`, `ALTextPos`, `ALLength`,
  `ALWrite`, `ALRead`) using a plain `StringBuilder`.
- **RoundDateTime avoids Telemetry.Abstractions** — `AlCompat.RoundDateTime` now
  uses `NavDateTime + Int64` (milliseconds) arithmetic instead of
  `NavDateTime.Create(DateTime)` which triggers `Telemetry.Abstractions` loading
  in BC 28+.
- **NavDateTime formatting** — `AlCompat.Format()` now handles `NavDateTime`
  values directly by casting to `DateTime`, avoiding the `NullReferenceException` in
  `NavDateTimeFormatter.GetStandardFormat` that occurred when `NavSession` was null.
  This fixes `Assert.AreEqual`/`AreNotEqual` comparisons involving DateTime values.

## [1.0.14] - 2026-04-14

### Added
- **Report `CurrReport.Skip()` and `CurrReport.Break()` support** — Report
  classes now include `Skip()` and `Break()` method stubs injected by the
  rewriter. Previously these calls caused CS1061 because the `NavReport` base
  class was stripped. (#168 related)
- **MockInStream: ALLength, ALPosition, ALResetPosition** — `MockInStream` now
  exposes `ALLength` (total stream length), `ALPosition` (current read position),
  and `ALResetPosition()` to reset the stream to the beginning. (#169)
- **MockRecordRef: 20+ missing methods** — Added `ALMark()`, `ALMarkedOnly`,
  `ALClearMarks`, `ALChangeCompany`, `ALAscending`, `ALHasFilter`, `ALGetFilters`,
  `ALGetPosition`, `ALSetPosition`, `ALRename`, `ALFieldExists`, `ALModifyAll`,
  `ALGetFilter`, `ALCurrentCompany` to `MockRecordRef`. (#170)
- **MockFile: ALUploadIntoStream / ALDownloadFromStream overloads** — Added the
  5-arg and 6-arg `ALUploadIntoStream` overloads (with dialog title, folder,
  filter, filename, and upload GUID) plus `ALDownloadFromStream` overloads.
  The rewriter now also redirects `NavFile.ALDownloadFromStream` to `MockFile`.
  (#171, #174)
- **MockFieldRef.ALSetTable** — No-op stub for `ALSetTable` emitted by BC
  compiler for page API extension code. (#172)
- **AlScope static stubs** — Added `ExitStatementNumber`, `MaxStackDepth`,
  `LastErrorCallStack`, `FindTryMethodScope()`, `MethodName()` static members
  to `AlScope` for NavMethodScope compatibility. (#173)
- **MockRecordHandle: FiltersActive, HasField** — `FiltersActive` property
  returns whether any SetRange/SetFilter is active. `HasField(int)` checks
  if a field has been set on the record.
- **Codeunit OnRun with record parameter** — `Codeunit.Run(codeunitId, record)`
  now correctly forwards the record to the target codeunit's `OnRun` trigger.
  Previously the record was silently dropped, causing `NullReferenceException`
  inside codeunits that declare `TableNo`. The rewriter now passes the 3rd
  argument of `NavCodeunit.RunCodeunit(DataError, id, record)` through to
  `MockCodeunitHandle.RunCodeunit(DataError, id, record)`. `RunCodeunitCore`
  looks up `OnRun(MockRecordHandle)` by exact signature and passes the record
  directly. `MockSession.StartSession` with a record parameter also forwards
  it to `OnRun`. Job-queue and batch-posting patterns that dispatch via
  `Codeunit.Run(codeunitId, rec)` now work correctly. (#135)
- **Generic catch-all runner-limitation detection** — The test executor now
  classifies additional exception types as runner limitations (`Status = Error`,
  `IsRunnerBug = true`) instead of misreporting them as test failures
  (`Status = Fail`):
  - `MissingMethodException` / `MissingMemberException` — a BC runtime method
    that has not yet been mocked by the runner.
  - Any exception whose call stack originates in `Microsoft.Dynamics.Nav.*` or
    `Microsoft.BusinessCentral.*` code — the BC service-tier context required
    by that method is not available in standalone mode.
  These cases now surface as `ERROR` with the "⚑ Runner limitation" hint
  and `IsRunnerBug = true` in JSON output, and contribute to exit code 2
  instead of exit code 1. (#131)
- **Per-object rewriter error handling** — When `RoslynRewriter.RewriteToTree`
  throws for an AL object (e.g. unexpected AL language construct in the C# AST),
  the runner now:
  - Catches the exception per-tree in the rewriter's `Parallel.For` loop
  - Reports a clean `⚑ These objects contain AL constructs not yet handled…` error
    block naming each failing object and the exception type/message
  - Populates `PipelineResult.RewriterErrors` so telemetry can include the gap
  - Fails with exit code 2 (runner limitation) instead of crashing with an
    unhandled `AggregateException`
- **Roslyn compilation failure hint** — When the C# compiler rejects the rewritten
  code, the error output now includes:
  `⚑ These errors may indicate AL constructs not yet handled by the runner's rewriter.`
  and a pointer to `--dump-rewritten` for debugging. Compiler errors are also
  stored in `PipelineResult.CompilationErrors` for telemetry.
- **Telemetry covers all pipeline stages** — `TelemetryReporter.TryReportPipelineGapsAsync`
  now accepts and reports rewriter gaps (`RewriterErrors`) and compilation gaps
  (`CompilationErrors`) in addition to runtime gaps, all in a single combined prompt.
- **Compilation error deduplication in telemetry** — Compilation errors are now
  grouped by CS error code + target type before display and telemetry send. E.g.
  74 CS1061 errors on `Report70400` collapse into one line `CS1061 on 'Report70400' (74×)`.
  Each deduplicated group is sent as its own telemetry report (instead of one
  joined blob), making the triage workflow able to create separate issues per
  unique error type.

### Removed
- **Dead `CompilationExcludedException` code** — The file-exclusion mechanism was
  removed in #80. The exception class and its two unreachable catch blocks have
  been deleted. The `TryReportPipelineGapsAsync` docstring no longer references
  the removed "iterative Roslyn retry" path.

### Fixed
- **Skip RDLC report layout generation** — `Compilation.Emit()` now uses
  `CompilationGenerationOptions.Code | Navigation` instead of `All`, skipping
  RDLC layout generation that crashes with `NullReferenceException` in
  `ReportRdlcUtilities.GenerateRdlcLayout` when running standalone. Report
  objects still emit C# code for dataset columns and triggers.
- **Telemetry triage KQL groups by message** — The triage workflow's KQL query
  now groups by `type, outerMessage` instead of just `type`, so each unique
  error message gets its own row instead of all `AlRunner.CompilationGap`
  exceptions collapsing into a single row.
- **Telemetry triage root-cause aggregation** — The triage workflow now pre-
  aggregates compilation gaps by root-cause pattern before sending to Copilot.
  CS0103 label-like variables (`*Lbl`, `*Txt`, etc.) collapse into one group;
  CS1061 errors on generated types (Report/Page/Extension) collapse by target;
  CS1061 errors on mock types keep separate entries per missing method. Handles
  scope-qualified type names (`Report70400.SomeScope` → `Report70400`) and
  truncated telemetry messages gracefully. A safety cap aborts issue creation if
  Copilot returns more than 15 new problems (likely a grouping failure).
- **Telemetry message truncation limit** — `ScrubMessage` now truncates at 500
  characters instead of 200, preserving full error context for long generated
  type names like `ReportExtension50506.DtldCustLedgEntries_…`.
- **`ALTransferFields` skips all PK fields** — When `initPrimaryKey=false`,
  `TransferFields` now skips all registered primary key fields instead of only
  field 1. Correctly handles composite primary keys. (#113)

### Added
- **`GetFilter` / `GetFilters` / `HasFilter`** — `GetFilter(fieldNo)` now returns
  the actual filter expression (equality value, `FROM..TO` range, or SetFilter
  expression) instead of empty string. `GetFilters` returns all active filters
  as a combined string. `HasFilter` returns true when any filter is active. (#113)
- **`CurrentKey` / `Ascending`** — `CurrentKey` property returns the current sort
  key field name(s), defaulting to PK. `Ascending` property returns whether the
  sort order is ascending (default true). (#113)
- **Record stub methods** — `CountApprox` (returns Count), `Consistent(bool)`
  (no-op), `FieldActive(fieldNo)` (returns true), `AddLink`/`DeleteLink`/
  `DeleteLinks`/`HasLinks` (in-memory tracking), `WritePermission` (returns true),
  `SetPermissionFilter` (no-op). (#113)
- **New test suite**: `108-getfilter` — 11 tests covering GetFilter, GetFilters,
  and HasFilter with range/expression filters and reset behavior.
- **New test suite**: `109-currentkey` — 4 tests covering CurrentKey and Ascending
  property getters.
- **New test suite**: `110-transferfields` — 3 tests covering TransferFields with
  PK handling.
- **New test suite**: `111-record-stubs` — 8 tests covering CountApprox, Consistent,
  FieldActive, AddLink/HasLinks/DeleteLinks, WritePermission, SetPermissionFilter.

## [1.0.13] - 2026-04-14

### Added
- **Event subscriber parameter forwarding** — Publisher event arguments (`ByRef<T>`
  and value parameters) are now forwarded from `βscope.RunEvent()` to subscriber
  methods via positional matching. Subscribers that modify `var` parameters (e.g.
  `var IsHandled: Boolean`) now write back correctly through shared `ByRef<T>`
  references. (#116)
- **Implicit DB trigger events** — `MockRecordHandle` now fires
  `OnBeforeInsertEvent`/`OnAfterInsertEvent`, `OnBeforeModifyEvent`/`OnAfterModifyEvent`,
  `OnBeforeDeleteEvent`/`OnAfterDeleteEvent` from `ALInsert`/`ALModify`/`ALDelete`, and
  `OnBeforeValidateEvent`/`OnAfterValidateEvent` from `ALValidateSafe`/`ALValidate`.
  Events fire regardless of `runTrigger` (matching BC behavior). xRec snapshots
  are captured before mutations. (#116)
- **BindSubscription / UnbindSubscription** — Manual event subscriber codeunits
  (`EventSubscriberInstance = Manual` in AL) are now detected via
  `[ManualEventSubscriber]` marker attribute emitted by the rewriter. The rewriter
  rewrites `ALSession.ALBindSubscription()`/`ALUnbindSubscription()` to
  `MockCodeunitHandle.Bind()`/`Unbind()`. Manual subscribers only fire when bound.
  Bindings are reset between tests. (#116)
- **`EventSubscriberRegistry` refactored** — Uses 3-tuple key
  `(ObjectType, ObjectId, EventName)` to prevent table/codeunit ID collision.
  Supports both automatic and manual subscriber classification.
- **New test suites**: `97-event-params` (2), `98-db-trigger-events` (5),
  `99-validate-events` (3), `100-bind-subscription` (3), `101-multi-subscribers` (2),
  `102-sender-pattern` (6), `103-before-db-events` (7), `104-xrec-behavior` (3),
  `105-subscriber-error` (4) — 35 new test cases total.
- **IncludeSender support** — `IntegrationEvent(true, false)` and
  `BusinessEvent(true)` now correctly pass the publishing codeunit instance as
  the first subscriber parameter via `MockCodeunitHandle.FromInstance(this)`.
  Subscribers can read/write publisher state through the sender handle. (#116)

### Fixed
- **`ALRename` properly updates table rows** — `MockRecordHandle.ALRename()` was
  a broken stub that only modified the handle's field bag without touching the
  in-memory table store. Now it: (1) finds the current record by its PK,
  (2) checks for key conflicts, (3) updates the actual table row, and
  (4) honors `errorLevel` (throws or returns false). Tested by `tests/bucket-2/107-rename/`
  (9 test cases). (#130)
- **`ALInsert` honors `DataError` level** — `MockRecordHandle.ALInsert()` now
  checks the `errorLevel` parameter before throwing on duplicate primary key.
  When AL code captures the return value (`if not Rec.Insert() then …`), the
  BC compiler passes `DataError.Never` and the method returns `false` instead
  of throwing. Previously it always threw regardless of error level. (#128)
- **`ALDelete` throws on missing record** — `MockRecordHandle.ALDelete()` now
  throws when the record does not exist and `errorLevel` is `DataError.ThrowError`
  (i.e. the return value is not captured in AL). Previously it silently returned
  `false` regardless of error level. Tested by `tests/bucket-2/106-dataerror-suppress/`
  (21 test cases). (#128)
- **`CS1503` in codeunits that call `HttpContent.WriteFrom(InStream)` or `HttpContent.ReadAs(var InStream)`** —
  After the `NavInStream → MockInStream` type rename in the rewriter, calls to
  `NavHttpContent.ALLoadFrom(MockInStream)` and `NavHttpContent.ALReadAs(ITreeObject, DataError, ByRef<MockInStream>)`
  failed with `CS1503` because `MockInStream` is not a subtype of `NavInStream`.
  The rewriter now intercepts:
  - `content.ALLoadFrom(arg)` (1-arg form) → `AlCompat.HttpContentLoadFrom(content, arg)`
  - `content.ALReadAs(scope, dataError, stream)` (3-arg form) → `AlCompat.HttpContentReadAs(content, scope, dataError, stream)`

  `AlCompat.HttpContentLoadFrom` has overloads for both `NavText` (delegating to the real
  method) and `MockInStream` (reads text from the stream then delegates).
  `AlCompat.HttpContentReadAs` is a no-op that initialises the stream variable to an empty
  `MockInStream` (HTTP is not available in standalone mode).
  The 2-arg text form of `ALReadAs(DataError, ByRef<NavText>)` is not affected.
  Codeunits such as "GO Express Request Builder" (codeunit 50611) that call
  `HttpContent.WriteFrom(InStream)` now compile and their pure-logic methods are testable.
  Fixes [#105](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/105).
  Tested by `tests/bucket-2/96-httpcontent-stream/` (5 test cases).
- **Missing BC runtime DLL classified as runner limitation** — `FileNotFoundException`
  and `FileLoadException` for `Microsoft.Dynamics.Nav.*` or `Microsoft.BusinessCentral.*`
  assemblies are now reported as ERROR (runner limitation, exit 2) instead of FAIL
  (test assertion failure, exit 1). This correctly classifies missing BC runtime DLLs
  (e.g. `Microsoft.BusinessCentral.Telemetry.Abstractions` introduced in BC 28) as
  a runner gap rather than a code bug.
- **Page without SourceTable compiles cleanly** — `SetSelectionFilter` no longer
  injects `this.Rec` into page classes that have no source table, fixing a CS1061
  Roslyn error for pages that only define helper procedures.

### Changed
- **Test matrix extended to BC 28.0** — `28.0` added to the version prefix list in
  `test-matrix.yml`. BC 28 introduced `Microsoft.BusinessCentral.Telemetry.Abstractions`
  as a new runtime dependency not yet fetched by the artifact downloader; one test
  (`SingleArgValidateFiresTrigger`) shows as a runner limitation (ERROR) on BC 28 only.
- **Vision reframe** — project rationale updated from "pure-logic codeunits only"
  to broad AL language compatibility. Docs, guide, and limitations page updated to
  reflect that unsupported AL constructs are gaps to fix, not design boundaries.
- **CONTRIBUTING.md** — added contributor guide covering TDD requirements, CI matrix,
  CHANGELOG policy, documentation checklist, and code-quality rules.
- **Test folder restructured into buckets** — `tests/` now has `bucket-1/`, `bucket-2/`,
  `stubs/`, and `excluded/` subdirectories. Each bucket is one `al-runner` invocation
  (all suites compile and run together), eliminating per-suite startup overhead. CI
  updated to loop over `bucket-*` directories. `39-stubs` moved to `stubs/`,
  `06-intentional-failure` and `46-missing-dep-hint` moved to `excluded/`. The
  `06-intentional-failure` fixture is now actively verified in CI (exit code must be 1).

### Added
- **Report variable support** — `NavReportHandle` is rewritten to `MockReportHandle`,
  a standalone replacement that supports `SetTableView()`, `Run()` (no-op), and
  `RunRequestPage()` (dispatches to `[RequestPageHandler]`). Report and report-extension
  generated classes are stubbed so BC-only layout/runtime infrastructure does not block
  compilation. `rendering { ... }` blocks and `DefaultRenderingLayout` properties are
  stripped from report AL source before transpilation.
  Tested by `tests/91-report-handle/` (6 test cases) and `tests/95-rendering-strip/` (2 test cases).
- **`[RequestPageHandler]` dispatch** — `HandlerRegistry` now registers and invokes
  `[RequestPageHandler]` procedures for `Report.RunRequestPage()` calls, with fallback
  to `[ModalPageHandler]` when no dedicated request-page handler is registered.
  Tested by `tests/92-request-page-handler/` (2 test cases).
- **Extended TestPage support** — `MockTestPageHandle` gains `GoToRecord()`, `Next()`,
  `New()`, `ClearReference()`, and `GetPart()` for subpage navigation.
  `MockTestPageField` gains assignable `ALValue`, `ALAsDecimal()`, and `ALEnabled()`.
  `MockTestPageFilter` now tracks filters with `ALGetFilter()`.
  Tested by `tests/90-testpage-extended/` (10 test cases).
- **`GetBySystemId`** — `MockRecordHandle` and `MockRecordRef` now support
  `ALGetBySystemId(Guid)` for looking up records by their system ID.
  Tested by `tests/93-record-getbysystemid/` (2 test cases).
- **`ClearFieldValue`** — `MockRecordHandle.ClearFieldValue(fieldNo)` resets a single
  field to its default. The rewriter redirects `ALSystemVariable.Clear(x)` to
  `x.Clear()` for RecordRef and similar types.
  Tested by `tests/94-clear-field-value/` (6 test cases).
- **`ALGetView` / `ALSetView`** — `MockRecordHandle` stores and returns view text.
  `MockRecordRef.ALSetView` now delegates to the underlying handle.
- **Global array variables** — `MockRecordHandle.GetGlobalArrayVariable()` returns
  typed `MockArray<T>` instances for Code, Text, Integer, Decimal, and Boolean.
- **`AlCompat.ObjectToMockArray<T>()`** — replacement for `ALCompiler.ObjectToNavArray`,
  converting runtime objects into the rewritten `MockArray` shape.
- **`MockFile.ALUploadIntoStream()`** — standalone replacement for `NavFile` upload
  dialogs. Returns `false` (no client surface) and clears the target stream.
- **`MockTextBuilder.ALAppendLine()`** — parameterless `AppendLine` overloads for
  appending a bare newline.
- **`MockInStream.Clear()`** — resets the in-memory stream to empty.
- **`MockSystemOperatingSystem.ALGetUrl()`** — returns a mock URL string.
- **`ClearApplicationMemberVariables()` stub** — injected into all codeunit classes
  so `TestRunner` codeunits compile after base-class removal.
- **`SetSelectionFilter` on page classes** — delegates to `ALCopy` + `ALSetRecFilter`.
- **Improved stub isolation** — built-in test stubs (Assert, Variable Storage) are
  compiled in isolation when real BC test packages are present, preventing symbol
  collisions. Stubs are skipped entirely when the source contains no test-library usage.
- **TestRunner codeunit support** — `NavTestRunnerCodeUnit` is now handled alongside
  other codeunit base types. BC-specific override members
  (`OnTestRunMethodsHaveTestPermissionsParameter`, `CommitTestCodeunits`,
  `CommitTestFunctions`) are stripped during rewriting.
- **Query object support** (`AlRunner/Runtime/MockQueryHandle.cs`, `AlRunner/RoslynRewriter.cs`) —
  AL `Query` objects now compile and run in standalone mode.  The BC compiler
  generates `QueryNNNN : NavQuery` classes that reference `NCLMetaQuery` and
  service-tier SQL views; the rewriter replaces the entire class with a minimal
  stub extending `MockQueryHandle` (same pattern used for XmlPort objects).
  `NavQueryHandle` is rewritten to `MockQueryHandle`.
  **Supported operations (no-ops allowing pre-Open setup code to run):**
  - `Q.Close()` — no-op
  - `Q.SetFilter(column, expression)` — no-op
  - `Q.SetRange(column [, from [, to]])` — no-op
  - `Q.TopNumberOfRows(n)` / `Q.ColumnCaption` / `Q.ColumnName` — property stubs
  **Operations that throw `NotSupportedException`** (query data access requires
  the BC service tier):
  - `Q.Open()`, `Q.Read()`
  - `Q.SaveAsCsv()`, `Q.SaveAsXml()`, `Q.SaveAsJson()`, `Q.SaveAsExcel()`
  Inject query dependencies via an AL interface to make query-dependent code
  unit-testable.  Tested by `tests/90-query-object/` (12 test cases).
  Fixes [#86](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/86).

### Performance
- **Cold-start optimizations** — `TieredPGO=false` and `QuickJitForLoops=true`
  reduce JIT overhead for short-lived CLI runs (~77 ms saved). `PublishReadyToRun`
  pre-compiles AlRunner to native code at publish time (~420 ms saved with
  `dotnet publish -r <rid>`).
- **Parallel AL parsing** — `ParseObjectText` calls run via `Parallel.For` instead
  of a sequential loop (3.3x speedup on multi-file projects).
- **Pre-sized MemoryStream** — Roslyn emit uses a 512 KB pre-allocated stream,
  avoiding 10+ resize-and-copy cycles per compilation.
- **Per-file rewrite cache (server mode)** — Rewritten Roslyn SyntaxTrees are
  cached by transpiled C# content. On warm re-run with one file changed, only
  that file is re-rewritten (41 ms → 1.7 ms, 24x speedup).
- **SyntaxTree cache (server mode)** — Parsed AL SyntaxTrees are cached by file
  content hash. Unchanged files skip `ParseObjectText` entirely.
- **Collectible AssemblyLoadContext** — Compiled test assemblies load into
  collectible ALCs. Memory is bounded by the 8-slot LRU cache instead of growing
  indefinitely.

### Fixed
- **`ObjectToDecimal` crash on `NavDecimal`** — `AlCompat.ObjectToDecimal()` now
  routes through `ExtractDecimal()` to handle BC's `NavDecimal` type, which does not
  implement `IConvertible`. Previously threw `InvalidCastException` when `TestPage`
  field `AsDecimal()` was called.
- **`CS1503` in codeunits that declare HTTP variables** — `AlScope` now implements
  `ITreeObject` (with stub `Tree`, `Type`, and `SingleThreaded` members), satisfying
  the parent-scope requirement of Nav* type constructors (`NavHttpClient`,
  `NavHttpRequestMessage`, `NavHttpResponseMessage`, `NavHttpContent`). Previously
  any codeunit that declared an `HttpClient`, `HttpRequestMessage`, `HttpResponseMessage`,
  or `HttpContent` local variable was excluded as a `CompilationGap` with
  `CS1503: cannot convert from AlScope to ITreeObject`. The null! catch-all rewriter
  rule that was masking the root cause has been removed.
  Codeunits with HTTP variables now compile; pure-logic methods in those codeunits
  (ones that don't actually send HTTP requests) are fully testable.
  Tested by `tests/89-nav-type-constructors/` (3 test cases).
- **`RecRef.Find()` (no-arg) compilation error** — The BC compiler emits
  `recRef.ALFind(DataError.ThrowError)` for AL's no-argument `RecRef.Find()`.
  `MockRecordRef` now provides a matching `ALFind(DataError)` overload that routes
  through `TryFind` so an empty table returns `false` instead of throwing.
  Previously caused `CS1503: cannot convert from DataError to string` at Roslyn
  compilation. Tested by `tests/88-recref-find/` (6 test cases).

## [1.0.12] — 2026-04-13

### Added
- **Multi-target net8.0 and net9.0** — `al-runner` now ships as a single NuGet
  package containing binaries for both .NET 8 and .NET 9. `dotnet tool install`
  automatically selects the build matching the installed runtime, so users with
  only .NET 9 no longer need to install .NET 8 separately.
  Closes [#75](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/75).
- **JUnit XML output (`--output-junit <path>`)** — Writes a standard JUnit XML test
  report alongside normal console output. GitHub Actions, Azure DevOps, and GitLab CI
  natively render JUnit XML as test annotations, summaries, and trend graphs. Combined
  with `--coverage` (Cobertura XML), this completes the CI integration story:
  - `--coverage` → coverage tab (Cobertura)
  - `--output-junit` → test results tab (JUnit XML)

  Tests are grouped by AL codeunit name as `<testsuite>` elements. Real assertion
  failures use `<failure>`; runner limitations use `<error>`.
  Closes [#72](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/72).
- **Compact summary line at end of test runs** — After each test run, the output
  now ends with a concise one-liner analogous to pytest/jest:
  - All pass: `42 passed in 1.8s`
  - With failures: `9 passed, 2 failed, 3 blocked (runner limitation) in 1.8s`
  - With setup errors: `9 passed, 1 errors in 0.3s`
  Only non-zero counts are shown. Runner-limitation errors (`IsRunnerBug=true`) are
  labelled `blocked (runner limitation)`; other errors are labelled `errors`.
  Elapsed time is always included when timing is available.
  Closes [#71](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/71).
- **`sourceFile` field on iterations and captured values** — The `--output-json`
  iteration and captured-value records now include a `sourceFile` property with the
  path to the AL file that contains the loop or variable. A new `SourceFileMapper`
  class resolves AL object names to source files at input-loading time.
  Tested by `tests/67-iteration-tracking/`.
- **Stream assignment, binary I/O, and `COPYSTREAM`** — Three new stream capabilities:
  - `MockOutStream.ALAssign` — enables `OutStr2 := OutStr1` stream assignment in AL
  - `MockStream.ALWrite`/`ALRead` overloads for `Integer`, `Boolean`, `Decimal18` — binary read/write via `OStr.Write(value)` / `IStr.Read(value)` in AL
  - `MockStream.ALCopyStream` — implements `COPYSTREAM(OutStr, InStr)`; rewriter redirects `ALSystemVariable.ALCopyStream` to `MockStream.ALCopyStream`

  Tested by `tests/79-stream-surface/` (10 cases).
  Fixes [#65](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/65).
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
- **Deduplicate repeated error blocks in output** — When multiple tests share the
  same runner-limitation error (e.g. 66 tests all blocked by the same
  `CompilationExcludedException`), `PrintResults` now prints the message once as a
  `WARN` block with a count, then compact `ERROR TestName (blocked)` lines. With
  `-v`/`--verbose`, the old full per-test detail is preserved. Single/unique errors
  and all `FAIL` tests are never deduplicated.
  Closes [#70](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/70).
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
- **`FieldRef.SetRange(Variant)` never matched records** — When AL code assigned a
  text literal to a `Variant` and then called `FieldRef.SetRange(v)`, the filter never
  matched because `MockVariant`'s implicit `NavValue?` operator returned `null` for
  non-NavValue content (raw CLR strings). The operator now converts primitive CLR values
  to their NavValue equivalents (`string→NavText`, `int→NavInteger`, `bool→NavBoolean`,
  `long→NavBigInteger`). Additionally, `NavValueToString` now trims trailing spaces from
  `NavCode` values (which BC pads to `maxLength`), fixing equality comparisons between
  `Code[N]` fields and `NavText` filter values.
  Tested by `tests/82-recref-fieldindex/` and `tests/87-fieldref-setrange-types/` (8 cases).
- **`GlobalLanguage()` NullReferenceException in standalone mode** — `ALSystemLanguage.get_ALGlobalLanguage` and `set_ALGlobalLanguage` crashed because there is no live BC session context in the runner. The rewriter now intercepts `ALSystemLanguage.ALGlobalLanguage` (both get and set) and routes them to `MockLanguage.ALGlobalLanguage`, a static int property backed by an in-memory field defaulting to 1033 (ENU). `MockLanguage.Reset()` is called between tests to restore the default. Fixes [#82](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/82). Tested by `tests/86-global-language/` (5 cases).
- **`FieldRef.SetRange` CS0121 ambiguity resolved** — `MockFieldRef.ALSetRange` had
  both `ALSetRange(NavValue)` and `ALSetRange(MockVariant)` overloads. Because
  `MockVariant` defines implicit conversions to and from `NavValue`, C# overload
  resolution could not pick one when the argument was a `NavValue` subtype (e.g.
  `NavInteger`, `NavOption`, `NavDecimal`), producing `CS0121`. The
  `ALSetRange(MockVariant)` overload has been removed; its logic is merged into the
  existing `ALSetRange(object)` catch-all, which already handles `MockVariant`
  correctly. Tested by `tests/87-fieldref-setrange-types/` (8 test cases).
  Fixes [#84](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/84).
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
