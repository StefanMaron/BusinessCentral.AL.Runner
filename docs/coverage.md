# AL Language Coverage Map

Auto-generated from `docs/coverage.yaml`. Do not edit directly.

## Per-overload signature tracking

Runtime-API coverage is tracked at **per-overload signature granularity** since [PR #1363](https://github.com/StefanMaron/BusinessCentral.AL.Runner/pull/1363). Each BC built-in method overload gets its own entry, keyed by `Type.Method (ParamTypes)`.

### Status meanings for runtime-api

| Status | Meaning |
|--------|---------|
| ✅ covered | Single overload and `AL<Method>` found in mock — confirmed implemented |
| 🔶 not-tested | `AL<Method>` found in mock but method has multiple overloads — name is implemented but per-overload coverage is unconfirmed |
| 🔲 gap | `AL<Method>` not found in any mapped mock file — not yet implemented |
| ❌ not-possible | Architectural limit (parallel session, real HTTP I/O, debugger) |

## Summary — syntax

| Status | Count |
|--------|-------|
| ✅ Covered | 144 |
| 🔶 Not tested (overload) | 0 |
| 🔲 Gap | 0 |
| ❌ Not possible | 3 |
| ⬜ Out of scope | 7 |
| **Total** | **154** |

## Summary — runtime-api

| Status | Count |
|--------|-------|
| ✅ Covered | 1408 |
| 🔶 Not tested (overload) | 164 |
| 🔲 Gap | 56 |
| ❌ Not possible | 33 |
| ⬜ Out of scope | 0 |
| **Total** | **1691** |

# Syntax layer

## Object

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `assembly_declaration` | ⬜ out-of-scope | — | .NET interop — requires BC runtime |
| `codeunit_declaration` | ✅ covered | `01-pure-function`, `10-cross-codeunit`, `112-codeunit-onrun-record`, `216-inherent-permissions` |  |
| `controladdin_declaration` | ⬜ out-of-scope | — | UI rendering — requires BC client |
| `dotnet_declaration` | ⬜ out-of-scope | — | .NET interop — requires BC runtime |
| `entitlement_declaration` | ❌ not-possible | — | Permission system — requires BC service tier |
| `enum_declaration` | ✅ covered | `20-option-fields`, `123-fieldref-enum`, `50-enum-ordinals`, `53-enum-interface`, `61-enum-names` |  |
| `interface_declaration` | ✅ covered | `03-interface-injection`, `31-interface-return`, `32-interface-param`, `68-interface-local-var`, `42-list-of-interface`, `53-enum-interface` |  |
| `page_declaration` | ✅ covered | `71-testpage`, `40-page-run-record`, `48-page-variable`, `65-page-helper`, `73-modal-handler` |  |
| `permissionset_declaration` | ❌ not-possible | — | Permission system — requires BC service tier |
| `profile_declaration` | ⬜ out-of-scope | — | UI profile — requires BC client |
| `query_declaration` | ✅ covered | `125-xmlport-query-diagnostics`, `90-query-object` |  |
| `report_declaration` | ✅ covered | `112-report-dataset-columns`, `113-report-labels`, `119-report-skip`, `133-report-handler`, `91-report-handle` |  |
| `table_declaration` | ✅ covered | `02-record-operations`, `07-composite-pk`, `18-validate-trigger`, `19-table-procedures`, `62-pk-unique`, `63-oninsert-trigger` |  |
| `xmlport_declaration` | ✅ covered | `125-xmlport-query-diagnostics`, `84-xmlport` |  |

## Extension

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `enumextension_declaration` | ✅ covered | `36-enum-extension` |  |
| `pagecustomization_declaration` | ✅ covered | `260-pagecustomization` |  |
| `pageextension_declaration` | ✅ covered | `82-pageextension`, `36-page-ext-no-cascade`, `38-page-ext-currpage` |  |
| `permissionsetextension_declaration` | ❌ not-possible | — | Permission system — requires BC service tier |
| `profileextension_declaration` | ⬜ out-of-scope | — | UI profile — requires BC client |
| `reportextension_declaration` | ✅ covered | `130-reportext-header-scope`, `129-reportext-parent` | > |
| `tableextension_declaration` | ✅ covered | `28-table-extension-fields`, `130-cross-ext-al0275`, `33-extension-validate`, `34-extension-parent-object` |  |

## Statement

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `asserterror_statement` | ✅ covered | `04-asserterror`, `21-expected-error-substring`, `25-expected-error-code` |  |
| `assignment_statement` | ✅ covered | `01-pure-function`, `15-codeunit-assign` |  |
| `break_statement` | ✅ covered | `67-iteration-tracking` |  |
| `case_branch` | ✅ covered | `01-pure-function` |  |
| `case_else_branch` | ✅ covered | `01-pure-function` |  |
| `case_statement` | ✅ covered | `01-pure-function` |  |
| `continue_statement` | ✅ covered | `67-iteration-tracking` |  |
| `empty_statement` | ✅ covered | `01-pure-function` |  |
| `exit_statement` | ✅ covered | `79-exit-this` |  |
| `for_statement` | ✅ covered | `01-pure-function`, `67-iteration-tracking` |  |
| `foreach_statement` | ✅ covered | `77-json-types`, `42-list-of-interface`, `83-list-byref` |  |
| `if_statement` | ✅ covered | `01-pure-function`, `09-setfilter-expressions` |  |
| `repeat_statement` | ✅ covered | `08-sort-ordering`, `67-iteration-tracking` |  |
| `using_statement` | ✅ covered | `45-unknown-namespace-using` |  |
| `while_statement` | ✅ covered | `67-iteration-tracking` |  |
| `with_statement` | ✅ covered | `02-record-operations` | Deprecated in AL but still parsed |

## Expression

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `additive_expression` | ✅ covered | `01-pure-function` |  |
| `assignment_expression` | ✅ covered | `01-pure-function` |  |
| `call_expression` | ✅ covered | `01-pure-function`, `10-cross-codeunit` |  |
| `comparison_expression` | ✅ covered | `01-pure-function`, `09-setfilter-expressions` |  |
| `database_reference` | ✅ covered | `02-record-operations` |  |
| `logical_expression` | ✅ covered | `01-pure-function` |  |
| `member_expression` | ✅ covered | `01-pure-function`, `77-json-types` |  |
| `multiplicative_expression` | ✅ covered | `01-pure-function` |  |
| `parenthesized_expression` | ✅ covered | `01-pure-function` |  |
| `qualified_enum_value` | ✅ covered | `50-enum-ordinals`, `53-enum-interface` |  |
| `range_expression` | ✅ covered | `09-setfilter-expressions`, `87-fieldref-setrange-types` |  |
| `subscript_expression` | ✅ covered | `77-json-types` |  |
| `ternary_expression` | ✅ covered | `175-ternary-expression` | inline if-then-else expression; nested form tested; BC transpiler handles natively |
| `unary_expression` | ✅ covered | `01-pure-function`, `166-unary-expression` |  |

## Type

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `array_type` | ✅ covered | `83-list-byref` |  |
| `basic_type` | ✅ covered | `01-pure-function`, `12-format-string`, `26-time-format`, `60-guid-text-get` |  |
| `code_type` | ✅ covered | `02-record-operations` |  |
| `dictionary_type` | ✅ covered | `138-dictionary` |  |
| `list_type` | ✅ covered | `42-list-of-interface`, `83-list-byref` |  |
| `object_reference_type` | ✅ covered | `15-codeunit-assign`, `48-page-variable` |  |
| `option_type` | ✅ covered | `20-option-fields` |  |
| `record_type` | ✅ covered | `02-record-operations`, `112-codeunit-onrun-record` |  |
| `text_type` | ✅ covered | `01-pure-function`, `17-text-builder` |  |
| `type_declaration` | ⬜ out-of-scope | — | .NET type alias (nested in dotnet/assembly blocks) — requires BC runtime .NET interop |
| `type_specification` | ✅ covered | `01-pure-function` |  |

## Procedure

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `event_declaration` | ✅ covered | `100-bind-subscription`, `225-db-event-byref-params`, `37-event-scope`, `66-event-subscribers`, `97-event-params` |  |
| `interface_procedure` | ✅ covered | `03-interface-injection`, `31-interface-return` |  |
| `interface_procedure_suffix` | ✅ covered | `03-interface-injection` |  |
| `procedure` | ✅ covered | `01-pure-function`, `10-cross-codeunit`, `19-table-procedures`, `41-try-function` |  |
| `procedure_modifier` | ✅ covered | `41-try-function` | local, internal, [TryFunction] modifiers |
| `trigger_declaration` | ✅ covered | `18-validate-trigger`, `225-db-event-byref-params`, `63-oninsert-trigger`, `98-db-trigger-events`, `99-validate-events` |  |

## Variable

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `label_attribute` | ✅ covered | `113-report-labels` |  |
| `label_declaration` | ✅ covered | `12-format-string`, `113-report-labels` |  |
| `parameter` | ✅ covered | `01-pure-function`, `32-interface-param`, `83-list-byref` |  |
| `parameter_list` | ✅ covered | `01-pure-function` |  |
| `var_attribute_item` | ✅ covered | `49-var-attributes` |  |
| `var_attribute_open` | ✅ covered | `49-var-attributes` |  |
| `var_section` | ✅ covered | `01-pure-function` |  |
| `variable_declaration` | ✅ covered | `01-pure-function` |  |

## Table

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `aggregate_formula` | ✅ covered | `55-flowfield-exist`, `56-flowfield-multi` |  |
| `aggregate_function` | ✅ covered | `55-flowfield-exist`, `56-flowfield-multi` |  |
| `calc_field_reference` | ✅ covered | `124-temp-records-flowfields`, `55-flowfield-exist`, `56-flowfield-multi` |  |
| `field_declaration` | ✅ covered | `02-record-operations`, `07-composite-pk`, `20-option-fields`, `126-field-metadata` |  |
| `fieldgroup_declaration` | ✅ covered | `48-fieldgroups` |  |
| `fieldgroups_section` | ✅ covered | `48-fieldgroups` |  |
| `fields_section` | ✅ covered | `02-record-operations` |  |
| `fixed_section` | ✅ covered | `59-table-fixed-section` | fixed() layout group on pages — BC compiler treats it identically to group(); TestPage field access works unchanged |
| `key_declaration` | ✅ covered | `07-composite-pk`, `08-sort-ordering`, `109-currentkey` |  |
| `keys_section` | ✅ covered | `07-composite-pk` |  |
| `lookup_formula` | ✅ covered | `38-lookup-formula`, `124-temp-records-flowfields` |  |
| `table_relation_expression` | ✅ covered | `49-tablerelation` | Table relations are parsed but not enforced at runtime |

## Page

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `action_area_section` | ✅ covered | `71-testpage`, `74-testpage-navigation` |  |
| `action_declaration` | ✅ covered | `71-testpage`, `74-testpage-navigation` |  |
| `action_group_section` | ✅ covered | `74-testpage-navigation` |  |
| `actionref_declaration` | ✅ covered | `72-actionref` |  |
| `actions_section` | ✅ covered | `71-testpage` |  |
| `area_section` | ✅ covered | `71-testpage`, `36-page-ext-no-cascade` |  |
| `cuegroup_section` | ✅ covered | `71-cuegroup-section` |  |
| `customaction_declaration` | ✅ covered | `75-customaction` |  |
| `fileuploadaction_declaration` | ✅ covered | `206-fileupload-action` |  |
| `grid_section` | ✅ covered | `74-grid-section` |  |
| `group_section` | ✅ covered | `71-testpage`, `36-page-ext-no-cascade` |  |
| `layout_section` | ✅ covered | `71-testpage`, `36-page-ext-no-cascade` |  |
| `page_field` | ✅ covered | `71-testpage`, `132-testpage-stubs`, `90-testpage-extended` |  |
| `part_section` | ✅ covered | `261-page-part-section` |  |
| `repeater_section` | ✅ covered | `74-testpage-navigation` |  |
| `separator_action` | ✅ covered | `73-separator-action` |  |
| `systemaction_declaration` | ✅ covered | `73-systemaction` | BC compiler emits C# for systemaction() entries; runner accepts the generated output unchanged (no rewriter rule needed) |
| `systempart_section` | ✅ covered | `261-page-part-section` |  |
| `usercontrol_section` | ⬜ out-of-scope | — | User controls require BC client rendering |
| `view_definition` | ✅ covered | `72-views-section` |  |
| `views_section` | ✅ covered | `72-views-section` |  |

## Report

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `dataset_section` | ✅ covered | `112-report-dataset-columns` |  |
| `rendering_layout` | ✅ covered | `95-rendering-strip` |  |
| `rendering_section` | ✅ covered | `95-rendering-strip` |  |
| `report_column` | ✅ covered | `112-report-dataset-columns` |  |
| `report_dataitem` | ✅ covered | `112-report-dataset-columns`, `119-report-skip` |  |
| `requestpage_section` | ✅ covered | `92-request-page-handler` |  |

## Query

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `query_column` | ✅ covered | `90-query-object` |  |
| `query_dataitem` | ✅ covered | `90-query-object` |  |
| `query_filter` | ✅ covered | `90-query-object` |  |

## Xmlport

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `elements_section` | ✅ covered | `84-xmlport` |  |
| `schema_section` | ✅ covered | `69-xmlport-schema`, `84-xmlport` |  |
| `xmlport_attribute` | ✅ covered | `100-xmlport-attribute` |  |
| `xmlport_element` | ✅ covered | `84-xmlport` |  |

## Enum

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `enum_value_declaration` | ✅ covered | `20-option-fields`, `50-enum-ordinals`, `61-enum-names` |  |
| `implements_clause` | ✅ covered | `53-enum-interface` |  |

## Modification

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `add_dataset_modification` | ✅ covered | `141-add-dataset` |  |
| `addafter_action_modification` | ✅ covered | `68-addafter-action` |  |
| `addafter_dataset_modification` | ✅ covered | `154-addafter-dataset` |  |
| `addafter_modification` | ✅ covered | `70-pageext-addafter-addbefore` |  |
| `addafter_views_modification` | ✅ covered | `76-addafter-views` |  |
| `addbefore_action_modification` | ✅ covered | `75-addbefore-action` |  |
| `addbefore_dataset_modification` | ✅ covered | `136-addbefore-dataset` |  |
| `addbefore_modification` | ✅ covered | `70-pageext-addafter-addbefore` |  |
| `addbefore_views_modification` | ✅ covered | `81-addbefore-views` |  |
| `addfirst_action_modification` | ✅ covered | `76-addfirst-action` |  |
| `addfirst_dataset_modification` | ✅ covered | `135-addfirst-dataset` |  |
| `addfirst_fieldgroup_modification` | ✅ covered | `277-addfirst-fieldgroup` | > |
| `addfirst_modification` | ✅ covered | `36-page-ext-no-cascade` |  |
| `addfirst_views_modification` | ✅ covered | `84-addfirst-views` |  |
| `addlast_action_modification` | ✅ covered | `78-addlast-action` |  |
| `addlast_dataset_modification` | ✅ covered | `136-addlast-dataset` |  |
| `addlast_fieldgroup_modification` | ✅ covered | `138-addlast-fieldgroup` |  |
| `addlast_modification` | ✅ covered | `36-page-ext-no-cascade`, `38-page-ext-currpage` |  |
| `addlast_views_modification` | ✅ covered | `139-addlast-views` |  |
| `modify_action_modification` | ✅ covered | `80-modify-action` |  |
| `modify_modification` | ✅ covered | `33-extension-validate`, `34-extension-parent-object` |  |
| `moveafter_modification` | ✅ covered | `137-moveafter` |  |
| `movebefore_modification` | ✅ covered | `82-movebefore` |  |
| `movefirst_modification` | ✅ covered | `137-movefirst` |  |
| `movelast_modification` | ✅ covered | `83-movelast` |  |

## Label

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `label_section` | ✅ covered | `113-report-labels` |  |
| `labels_section` | ✅ covered | `113-report-labels` |  |

## Namespace

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `namespace_declaration` | ✅ covered | `45-unknown-namespace-using` |  |
| `namespace_name` | ✅ covered | `45-unknown-namespace-using` |  |

## Attribute

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `attribute_item` | ✅ covered | `83-attribute-item`, `100-bind-subscription`, `41-try-function`, `66-event-subscribers` |  |

# Runtime API layer

Source: `Microsoft.Dynamics.Nav.CodeAnalysis` method symbol tables. Coverage = AL-prefixed method present in `AlRunner/Runtime/*.cs`. Each row is one overload signature.

## BigInteger  (1/1)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `ToText` | `()` | ✅ covered | Format(BigInteger) dispatches through AlCompat.Format which handles NavBigInteger via ((long)nbi).ToString() |

## BigText  (6/8)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AddText` | `(BigText, Integer)` | ✅ covered |  |
| `AddText` | `(Text, Integer)` | 🔶 not-tested |  |
| `GetSubText` | `(BigText, Integer, Integer)` | ✅ covered |  |
| `GetSubText` | `(Text, Integer, Integer)` | 🔶 not-tested |  |
| `Length` | `()` | ✅ covered |  |
| `Read` | `(InStream)` | ✅ covered |  |
| `TextPos` | `(Text)` | ✅ covered |  |
| `Write` | `(OutStream)` | ✅ covered |  |

## Blob  (6/6)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `CreateInStream` | `(InStream, TextEncoding)` | ✅ covered | chained-call pattern (CreateInStream().ReadText()) supported via ALCompiler.ObjectToNavInStream→AlCompat.ObjectToMockInStream rewrite (issue #1026) |
| `CreateOutStream` | `(OutStream, TextEncoding)` | ✅ covered | chained-call pattern (CreateOutStream().WriteText()) supported via ALCompiler.ObjectToNavOutStream→AlCompat.ObjectToMockOutStream rewrite (issue #1026) |
| `Export` | `(Text)` | ✅ covered | stream-form (Blob → OutStream via CreateOutStream) works standalone. File-form Export(Filename) is out-of-scope (no filesystem in runner). |
| `HasValue` | `()` | ✅ covered |  |
| `Import` | `(Text)` | ✅ covered | stream-form (InStream → Blob via CreateInStream) works standalone. File-form Import(Filename) is out-of-scope (no filesystem in runner). |
| `Length` | `()` | ✅ covered |  |

## Boolean  (1/2)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `ToText` | `()` | ✅ covered | Format(B) returns Yes/No; Format(B,0,'<Standard Format,2>') returns 1/0 |
| `ToText` | `(Boolean)` | 🔶 not-tested |  |

## Byte  (1/2)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `ToText` | `()` | ✅ covered | ToText() rewritten to AlCompat.Format(expr) by RoslynRewriter |
| `ToText` | `(Boolean)` | 🔶 not-tested |  |

## Codeunit  (1/2)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Run` | `(Integer, Table)` | ✅ covered | > |
| `Run` | `(Text, Table)` | 🔲 gap |  |

## CodeunitInstance  (1/1)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Run` | `(Table)` | ✅ covered | MockCodeunitHandle.Run now fires OnRun on the handle's own instance (state mutations persist across calls). |

## CompanyProperty  (3/3)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `DisplayName` | `()` | ✅ covered | "overloads=1; configurable via AL Runner Config.SetCompanyDisplayName(); default: \"My Company\"" |
| `ID` | `()` | ✅ covered | "overloads=1; configurable via AL Runner Config.SetCompanyId(); default: fixed non-empty GUID" |
| `UrlName` | `()` | ✅ covered | "overloads=1; configurable via AL Runner Config.SetCompanyUrlName(); default: \"My%20Company\"" |

## Cookie  (7/7)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Domain` | `()` | ✅ covered |  |
| `Expires` | `()` | ✅ covered |  |
| `HttpOnly` | `()` | ✅ covered |  |
| `Name` | `(Text)` | ✅ covered |  |
| `Path` | `()` | ✅ covered |  |
| `Secure` | `()` | ✅ covered |  |
| `Value` | `(Text)` | ✅ covered |  |

## DataTransfer  (8/9)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AddConstantValue` | `(Joker, Integer)` | ✅ covered |  |
| `AddDestinationFilter` | `(Integer, Text, Joker)` | 🔲 gap |  |
| `AddFieldValue` | `(Integer, Integer)` | ✅ covered |  |
| `AddJoin` | `(Integer, Integer)` | ✅ covered |  |
| `AddSourceFilter` | `(Integer, Text, Joker)` | ✅ covered |  |
| `CopyFields` | `()` | ✅ covered |  |
| `CopyRows` | `()` | ✅ covered |  |
| `SetTables` | `(Integer, Integer)` | ✅ covered |  |
| `UpdateAuditFields` | `(Boolean)` | ✅ covered | auto-property added to MockDataTransfer (default false; round-trips on set). CopyRows/CopyFields remain no-ops as there is no real DB. |

## Database  (28/30)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AlterKey` | `(KeyRef, Boolean)` | ✅ covered | no-op stub (DDL not supported standalone); tests/bucket-1/record-table/151-database-alterkey |
| `ChangeUserPassword` | `(Text, Text)` | ✅ covered | rewriter strips the entire call (no user system standalone). Signature is (OldPassword, NewPassword). |
| `CheckLicenseFile` | `(Integer)` | ✅ covered | no-op stub (no license system standalone); tests/bucket-1/record-table/153-database-checklicensefile |
| `Commit` | `()` | ✅ covered |  |
| `CompanyName` | `()` | ✅ covered | returns "CRONUS" by default; configurable via --company-name CLI flag or "AL Runner Config".SetCompanyName(). Per-test reset restores the default. |
| `CopyCompany` | `(Text, Text)` | ✅ covered | no-op stub (no multi-company store in standalone mode); ALCopyCompany stripped by RoslynRewriter |
| `CurrentTransactionType` | `(TransactionType)` | ❓ stub | stub returning TransactionType::Update (ordinal 2); runner has no real transaction tracking |
| `DataFileInformation` | `(Boolean, Text, Text, Boolean, Boolean, Boolean, Text, DateTime, Table)` | ✅ covered | no-op stub; ALDataFileInformation added to StripEntireCallMethods in RoslynRewriter; var params remain at defaults; tests/bucket-1/record-table/154-datafileinformation |
| `ExportData` | `(Boolean, Text, Text, Boolean, Boolean, Boolean, Table)` | ✅ covered | no-op stub (no file I/O in standalone mode); ALExportData stripped by RoslynRewriter |
| `GetDefaultTableConnection` | `(TableConnectionType)` | ✅ covered | rewriter stubs ALGetDefaultTableConnection(ct) to empty string (no external connections standalone). |
| `HasTableConnection` | `(TableConnectionType, Text)` | ✅ covered | returns false for unregistered connections. |
| `ImportData` | `(Boolean, Text, Boolean, Boolean, Table)` | ✅ covered | no-op stub (no file I/O in standalone mode); ALImportData stripped by RoslynRewriter |
| `IsInWriteTransaction` | `()` | ✅ covered | RoslynRewriter rewrites ALDatabase.ALIsInWriteTransaction() to false literal; runner has no real transactions; tests/bucket-1/record-table/157-isinwritetransaction |
| `LastUsedRowVersion` | `()` | ✅ covered | method stubbed to `0L` via rewriter (no real DB ⇒ no rows written). |
| `LockTimeout` | `(Boolean)` | ✅ covered | property get stubbed to `true` via rewriter (BC default); setter already a no-op. |
| `LockTimeoutDuration` | `(Integer)` | ✅ covered | property get stubbed to `0L` via rewriter (no timeout), flows through ALCompiler.ToDuration. |
| `MinimumActiveRowVersion` | `()` | ✅ covered | method stubbed to `0L` via rewriter (no real DB ⇒ no active transactions). |
| `RegisterTableConnection` | `(TableConnectionType, Text, Text)` | ✅ covered | rewriter strips the entire call (no external connections standalone). Signature is (ConnectionType, Name, Connection). |
| `SelectLatestVersion` | `()` | ✅ covered | (no-arg and optional Boolean); both stripped to no-op by RoslynRewriter |
| `SelectLatestVersion` | `(Integer)` | 🔲 gap |  |
| `SerialNumber` | `()` | ✅ covered | rewriter stubs ALSerialNumber (property and method) to the fixed string "STANDALONE". |
| `ServiceInstanceId` | `()` | ✅ covered | rewriter stubs ALServiceInstanceID() to 1 (non-zero instance id). |
| `SessionId` | `()` | ✅ covered | returns 1 (stable non-zero stub); proved non-zero and stable. |
| `SetDefaultTableConnection` | `(TableConnectionType, Text, Boolean)` | ✅ covered | rewriter strips the entire call (no external connections standalone). |
| `SetUserPassword` | `(Guid, Text)` | ✅ covered | (userId, newPassword); rewriter strips the entire call (no service-tier user system standalone). |
| `SID` | `(Text)` | ✅ covered |  |
| `TenantId` | `()` | ✅ covered | rewriter stubs ALTenantID (property and method) to the fixed string "STANDALONE". |
| `UnregisterTableConnection` | `(TableConnectionType, Text)` | ✅ covered | rewriter strips the entire call (no external connections standalone). Pairs with RegisterTableConnection / SetDefaultTableConnection. |
| `UserId` | `()` | ✅ covered | default stub value "TESTUSER" (configurable via --user-id flag) |
| `UserSecurityId` | `()` | ✅ covered | rewriter routes to AlCompat.UserSecurityId which returns a fixed non-null Guid stable across reads. |

## Date  (6/6)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Day` | `()` | ✅ covered | via Date2DMY(D,1) |
| `DayOfWeek` | `()` | ✅ covered | via Date2DWY(D,1) |
| `Month` | `()` | ✅ covered | via Date2DMY(D,2) |
| `ToText` | `(Boolean)` | ✅ covered | via Format(D) handled by AlCompat.Format |
| `WeekNo` | `()` | ✅ covered | via Date2DWY(D,2) |
| `Year` | `()` | ✅ covered | via Date2DMY(D,3) |

## DateTime  (3/3)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Date` | `()` | ✅ covered | DT2Date / DT2Time / CreateDateTime — BC runtime types handle these natively |
| `Time` | `()` | ✅ covered | DT2Date / DT2Time / CreateDateTime — BC runtime types handle these natively |
| `ToText` | `(Boolean)` | ✅ covered | rewriter redirects `<navDateTime>.ToText(null!)` to `AlCompat.Format` (session-free). |

## Debugger  (3/19)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Activate` | `()` | ✅ covered | . Stripped via StripEntireCallMethods — no debugger infrastructure standalone. |
| `Attach` | `(Integer)` | ❌ not-possible |  |
| `Break` | `()` | ❌ not-possible |  |
| `BreakOnError` | `(Boolean)` | ❌ not-possible |  |
| `BreakOnRecordChanges` | `(Boolean)` | ❌ not-possible |  |
| `Continue` | `()` | ❌ not-possible |  |
| `Deactivate` | `()` | ✅ covered | . Stripped via StripEntireCallMethods — no debugger infrastructure standalone. |
| `DebuggedSessionID` | `()` | ❌ not-possible |  |
| `DebuggingSessionID` | `()` | ❌ not-possible |  |
| `EnableSqlTrace` | `(Integer, Boolean)` | ❌ not-possible |  |
| `GetLastErrorText` | `()` | ❌ not-possible |  |
| `IsActive` | `()` | ✅ covered | . Rewriter replaces ALDebugger.ALIsActive() with false — no debugger attached standalone. |
| `IsAttached` | `()` | ❌ not-possible |  |
| `IsBreakpointHit` | `()` | ❌ not-possible |  |
| `SkipSystemTriggers` | `(Boolean)` | ❌ not-possible |  |
| `StepInto` | `()` | ❌ not-possible |  |
| `StepOut` | `()` | ❌ not-possible |  |
| `StepOver` | `()` | ❌ not-possible |  |
| `Stop` | `()` | ❌ not-possible |  |

## Decimal  (1/2)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `ToText` | `()` | ✅ covered | BC native `ALCompiler.ToText(null, decimal)` works standalone. |
| `ToText` | `(Boolean)` | 🔶 not-tested |  |

## Dialog  (9/11)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Close` | `()` | ✅ covered |  |
| `Confirm` | `(Text, Boolean, Joker)` | ✅ covered |  |
| `Error` | `(ErrorInfo)` | ✅ covered | AlDialog.Error implemented in Runtime/AlScope.cs (throws NavNCLMessageException). Asserterror-catchable per existing suites. |
| `Error` | `(Text, Joker)` | 🔲 gap |  |
| `HideSubsequentDialogs` | `(Boolean)` | ✅ covered | emitted as property-set on MockDialog; no-op standalone. |
| `LogInternalError` | `(Text, DataClassification, Verbosity)` | ✅ covered | static MockDialog.ALLogInternalError stub (no telemetry pipeline standalone). |
| `LogInternalError` | `(Text, Text, DataClassification, Verbosity)` | 🔶 not-tested |  |
| `Message` | `(Text, Joker)` | ✅ covered | > |
| `Open` | `(Text, Joker)` | ✅ covered |  |
| `StrMenu` | `(Text, Integer, Text)` | ✅ covered | (3 C# overloads 3/4/5-arg); MockDialog.ALStrMenu returns defaultNo (or 0 when omitted) — "use default or cancel" convention standalone. |
| `Update` | `(Integer, Joker)` | ✅ covered | (NavCode overload for CS0121 #1179; removed int overload to fix NavValue/int CS0121 ambiguity #1279) |

## Duration  (1/1)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `ToText` | `(Boolean)` | ✅ covered |  |

## EnumType  (4/4)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AsInteger` | `()` | ✅ covered |  |
| `FromInteger` | `(Integer)` | ✅ covered | BC emits NavOption.Create(NCLEnumMetadata.Create(N),I) for FromInteger; CreateTaggedOption validates ordinal via EnumRegistry for Extensible=false enums, throws on invalid ordinal |
| `Names` | `()` | ✅ covered | (instance E.Names() and type-qualifier Enum::"T".Names()) |
| `Ordinals` | `()` | ✅ covered | (instance E.Ordinals() and type-qualifier Enum::"T".Ordinals()) |

## ErrorInfo  (18/21)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AddAction` | `(Text, Integer, Text, Text)` | 🔲 gap |  |
| `AddAction` | `(Text, Integer, Text)` | ✅ covered | (3-arg + 4-arg with description); rewriter strips the entire call. ALAddAction on NavALErrorInfo crashes standalone (null parent in NavApplicationObjectBaseHandle ctor); stripping is safe because interactive drill-down actions don't fire without a UI. |
| `AddNavigationAction` | `(Text, Text)` | 🔲 gap |  |
| `AddNavigationAction` | `(Text)` | ✅ covered | (1-arg caption-only + 2-arg caption+description); rewriter strips the entire call. Navigation drill-downs require a UI client to open; stripping is safe in standalone mode. |
| `Callstack` | `()` | ✅ covered | BC native works standalone on default-initialised ErrorInfo values. ErrorInfo.Create() itself has a separate DLL-loading gap (loads Microsoft.Dynamics.Nav.CodeAnalysis 16.4.x). |
| `Collectible` | `(Boolean)` | ✅ covered | ErrorInfo.Collectible := true marks errors for collection. [ErrorBehavior(ErrorBehavior::Collect)] on source-side methods works via AlScope.RunBehavior. [ErrorBehavior(ErrorBehavior::Collect)] on test procedures handled by executor detecting ldc.i4.1+box in test method IL and calling AlScope.RunWithCollecting. HasCollectedErrors, ClearCollectedErrors, IsCollectingErrors, GetCollectedErrors all functional. |
| `ControlName` | `(Text)` | ✅ covered | NavALErrorInfo.ALControlName works via BC runtime DLL without session |
| `Create` | `()` | ✅ covered | NavALErrorInfo.ALCreate(...) loads Microsoft.Dynamics.Nav.CodeAnalysis at runtime. Intercepted in RoslynRewriter — rewritten to AlCompat.CreateErrorInfo(msg) which creates NavALErrorInfo() directly and sets ALMessage. Default (no-arg) variant also intercepted. String overload added in #1278 to fix CS1503 when BC emits string literal for the message argument. |
| `Create` | `(Text, Boolean, Table, Integer, Integer, Text, Verbosity, DataClassification, Dictionary)` | 🔶 not-tested |  |
| `CustomDimensions` | `(Dictionary)` | ✅ covered | BC native property works standalone. Dictionary of [Text, Text] get/set with empty default. Setter replaces (not merges). |
| `DataClassification` | `(DataClassification)` | ✅ covered | ALDataClassification() getter + ALDataClassification(int) setter on NavALErrorInfo |
| `DetailedMessage` | `(Text)` | ✅ covered | BC native property works standalone on default-initialised ErrorInfo values. AL emits as property get/set. |
| `ErrorType` | `(ErrorType)` | ✅ covered | ALErrorType property get/set on NavALErrorInfo works standalone. Default is Client (not Internal). |
| `FieldNo` | `(Integer)` | ✅ covered | BC native property works standalone. Integer get/set with 0 as fresh default. |
| `Message` | `(Text)` | ✅ covered | ALMessage property get/set on NavALErrorInfo works standalone. Empty string on default-initialised ErrorInfo. |
| `PageNo` | `(Integer)` | ✅ covered | NavALErrorInfo.ALPageNo works via BC runtime DLL without session |
| `RecordId` | `(RecordId)` | ✅ covered | getter works standalone (fresh ErrorInfo returns default RecordId with TableNo 0). Setter (`ei.RecordId := rec.RecordId()`) does not persist — NavALErrorInfo.ALRecordId is tied to an internal NavRecord that requires a live session. Getter coverage is what works today. |
| `SystemId` | `(Guid)` | ✅ covered | getter ALSystemId() + setter ALSystemId(Guid) on NavALErrorInfo |
| `TableId` | `(Integer)` | ✅ covered | BC native property works standalone. Integer get/set with 0 as fresh default. |
| `Title` | `(Text)` | ✅ covered | NavALErrorInfo.ALTitle() works via BC runtime DLL without session. |
| `Verbosity` | `(Verbosity)` | ✅ covered | NavALErrorInfo.ALVerbosity works standalone via BC runtime DLL |

## FieldRef  (31/67)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Active` | `()` | ✅ covered |  |
| `CalcField` | `()` | ✅ covered |  |
| `CalcSum` | `()` | ✅ covered |  |
| `Caption` | `()` | ✅ covered | reads from TableFieldRegistry — returns declared caption, falls back to field name when no Caption set; tableextension fields also covered |
| `Class` | `()` | ✅ covered |  |
| `EnumValueCount` | `()` | ✅ covered | . Covered: returns member count for enum fields; returns 0 for non-enum fields. |
| `FieldError` | `(ErrorInfo)` | ✅ covered |  |
| `FieldError` | `(Text)` | 🔶 not-tested |  |
| `GetEnumValueCaption` | `(Integer)` | ✅ covered | . Covered: 1-based index → caption. Standalone note — EnumRegistry only captures names, so Caption returns the AL identifier (same as Name). |
| `GetEnumValueCaptionFromOrdinalValue` | `(Integer)` | ✅ covered | MockFieldRef.ALGetEnumValueCaptionFromOrdinalValue looks up by ordinal value (not 1-based index). EnumRegistry does not track captions separately from names standalone, so the caption equals the member name. |
| `GetEnumValueName` | `(Integer)` | ✅ covered | . Covered: 1-based index → member name; out-of-range throws. |
| `GetEnumValueNameFromOrdinalValue` | `(Integer)` | ✅ covered | MockFieldRef.ALGetEnumValueNameFromOrdinalValue returns the AL identifier of the enum member whose ordinal matches. |
| `GetEnumValueOrdinal` | `(Integer)` | ✅ covered | . Covered: 1-based index → ordinal; out-of-range throws. |
| `GetFilter` | `()` | ✅ covered |  |
| `GetRangeMax` | `()` | ✅ covered |  |
| `GetRangeMin` | `()` | ✅ covered |  |
| `IsEnum` | `()` | ✅ covered |  |
| `IsOptimizedForTextSearch` | `()` | ✅ covered |  |
| `Length` | `()` | ✅ covered |  |
| `Name` | `()` | ✅ covered | reads from TableFieldRegistry — returns declared field name including quoted names; tableextension fields also covered |
| `Number` | `()` | ✅ covered |  |
| `OptionCaption` | `()` | ✅ covered |  |
| `OptionMembers` | `()` | ✅ covered |  |
| `OptionString` | `()` | ✅ covered |  |
| `Record` | `()` | ✅ covered |  |
| `Relation` | `()` | ✅ covered |  |
| `SetFilter` | `(Text, Joker)` | ✅ covered |  |
| `SetRange` | `(Joker, Joker)` | ✅ covered |  |
| `TestField` | `()` | ✅ covered |  |
| `TestField` | `(BigInteger, ErrorInfo)` | 🔶 not-tested |  |
| `TestField` | `(BigInteger)` | 🔶 not-tested |  |
| `TestField` | `(Boolean, ErrorInfo)` | 🔶 not-tested |  |
| `TestField` | `(Boolean)` | 🔶 not-tested |  |
| `TestField` | `(Byte, ErrorInfo)` | 🔶 not-tested |  |
| `TestField` | `(Byte)` | 🔶 not-tested |  |
| `TestField` | `(Char, ErrorInfo)` | 🔶 not-tested |  |
| `TestField` | `(Char)` | 🔶 not-tested |  |
| `TestField` | `(Code, ErrorInfo)` | 🔶 not-tested |  |
| `TestField` | `(Code)` | 🔶 not-tested |  |
| `TestField` | `(Date, ErrorInfo)` | 🔶 not-tested |  |
| `TestField` | `(Date)` | 🔶 not-tested |  |
| `TestField` | `(DateTime, ErrorInfo)` | 🔶 not-tested |  |
| `TestField` | `(DateTime)` | 🔶 not-tested |  |
| `TestField` | `(Decimal, ErrorInfo)` | 🔶 not-tested |  |
| `TestField` | `(Decimal)` | 🔶 not-tested |  |
| `TestField` | `(Enum, ErrorInfo)` | 🔶 not-tested |  |
| `TestField` | `(Enum)` | 🔶 not-tested |  |
| `TestField` | `(ErrorInfo)` | 🔶 not-tested |  |
| `TestField` | `(Guid, ErrorInfo)` | 🔶 not-tested |  |
| `TestField` | `(Guid)` | 🔶 not-tested |  |
| `TestField` | `(Integer, ErrorInfo)` | 🔶 not-tested |  |
| `TestField` | `(Integer)` | 🔶 not-tested |  |
| `TestField` | `(Joker, ErrorInfo)` | 🔶 not-tested |  |
| `TestField` | `(Joker)` | 🔶 not-tested |  |
| `TestField` | `(Label, ErrorInfo)` | 🔶 not-tested |  |
| `TestField` | `(Label)` | 🔶 not-tested |  |
| `TestField` | `(Option, ErrorInfo)` | 🔶 not-tested |  |
| `TestField` | `(Option)` | 🔶 not-tested |  |
| `TestField` | `(Text, ErrorInfo)` | 🔶 not-tested |  |
| `TestField` | `(Text)` | 🔶 not-tested |  |
| `TestField` | `(Time, ErrorInfo)` | 🔶 not-tested |  |
| `TestField` | `(Time)` | 🔶 not-tested |  |
| `TestField` | `(Variant, ErrorInfo)` | 🔶 not-tested |  |
| `TestField` | `(Variant)` | 🔶 not-tested |  |
| `Type` | `()` | ✅ covered |  |
| `Validate` | `(Joker)` | ✅ covered |  |
| `Value` | `(Joker)` | ✅ covered | > |

## File  (28/48)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Close` | `()` | ✅ covered | MockFile.ALClose() resets position |
| `Copy` | `(Text, Text)` | ✅ covered | MockFile.ALCopy() no-op in standalone |
| `Create` | `(Text, TextEncoding)` | ✅ covered | MockFile.ALCreate() opens in-memory buffer, returns true (Boolean result per AL spec) |
| `CreateInStream` | `(InStream)` | ✅ covered | MockFile.ALCreateInStream() fills stream from buffer |
| `CreateOutStream` | `(OutStream)` | ✅ covered | MockFile.ALCreateOutStream() writes back to buffer |
| `CreateTempFile` | `(TextEncoding)` | ✅ covered | MockFile.ALCreateTempFile() no-op in standalone |
| `Download` | `(Text, Text, Text, Text, Text)` | ✅ covered | MockFile.ALDownload() no-op (no browser/UI in standalone) |
| `DownloadFromStream` | `(InStream, Text, Text, Text, Text)` | ✅ covered |  |
| `Erase` | `(Text)` | ✅ covered | MockFile.ALErase() no-op in standalone |
| `Exists` | `(Text)` | ✅ covered | MockFile.ALExists() always returns false (no real FS) |
| `GetStamp` | `(Text, Date, Time)` | ✅ covered | MockFile.ALGetStamp() returns NavDateTime.Default |
| `IsPathTemporary` | `(Text)` | ✅ covered | MockFile.ALIsPathTemporary() returns false |
| `Len` | `()` | ✅ covered | MockFile.ALLen() returns byte length of in-memory buffer |
| `Name` | `()` | ✅ covered | MockFile.ALName() returns name set by Create/Open |
| `Open` | `(Text, TextEncoding)` | ✅ covered | MockFile.ALOpen() resets position for reading |
| `Pos` | `()` | ✅ covered | MockFile.ALPos() returns current position |
| `Read` | `(Joker)` | ✅ covered | MockFile.ALRead() reads UTF-8 line from in-memory buffer |
| `Rename` | `(Text, Text)` | ✅ covered | MockFile.ALRename() no-op in standalone |
| `Seek` | `(Integer)` | ✅ covered | MockFile.ALSeek() moves position in in-memory buffer |
| `SetStamp` | `(Text, Date, Time)` | ✅ covered | MockFile.ALSetStamp() no-op (no real FS) |
| `TextMode` | `(Boolean)` | ✅ covered | MockFile.ALTextMode property get/set |
| `Trunc` | `()` | ✅ covered | MockFile.ALTrunc() truncates buffer at current position |
| `Upload` | `(Text, Text, Text, Text, Text)` | ✅ covered | MockFile.ALUpload() no-op (no browser/UI in standalone) |
| `UploadIntoStream` | `(Text, InStream)` | ✅ covered | 5-arg and 6-arg (with/without Guid) for BC≤25 5-param AL form; 4-arg (no DataError/Folder/Guid) for newer BC 4-param AL form; 2-param AL form (Title, var InStream) emitting (DataError, string, ByRef&lt;MockInStream&gt;, Guid) — both (object?, string, ByRef&lt;MockInStream&gt;, Guid) and DataError-typed (DataError, string, ByRef&lt;MockInStream&gt;, Guid) overloads for issues #1213/#1214; all no-op stubs returning false |
| `UploadIntoStream` | `(Text, Text, Text, Text, InStream)` | 🔶 not-tested |  |
| `View` | `(Text, Boolean)` | ✅ covered | MockFile.ALView() no-op (no UI in standalone) |
| `ViewFromStream` | `(InStream, Text, Boolean)` | ✅ covered | static overload (scope, InStream, FileName, IsEditable); 3-arg AL form File.ViewFromStream(InStream, FileName, IsEditable); fixes CS1501 telemetry gap |
| `Write` | `(BigInteger)` | ✅ covered | MockFile.ALWrite() appends UTF-8 bytes to in-memory buffer |
| `Write` | `(BigText)` | 🔶 not-tested |  |
| `Write` | `(Boolean)` | 🔶 not-tested |  |
| `Write` | `(Byte)` | 🔶 not-tested |  |
| `Write` | `(Char)` | 🔶 not-tested |  |
| `Write` | `(Code)` | 🔶 not-tested |  |
| `Write` | `(Date)` | 🔶 not-tested |  |
| `Write` | `(DateFormula)` | 🔶 not-tested |  |
| `Write` | `(DateTime)` | 🔶 not-tested |  |
| `Write` | `(Decimal)` | 🔶 not-tested |  |
| `Write` | `(Duration)` | 🔶 not-tested |  |
| `Write` | `(Guid)` | 🔶 not-tested |  |
| `Write` | `(Integer)` | 🔶 not-tested |  |
| `Write` | `(Joker)` | 🔶 not-tested |  |
| `Write` | `(Label)` | 🔶 not-tested |  |
| `Write` | `(Option)` | 🔶 not-tested |  |
| `Write` | `(RecordId)` | 🔶 not-tested |  |
| `Write` | `(Table)` | 🔶 not-tested |  |
| `Write` | `(Text)` | 🔶 not-tested |  |
| `Write` | `(Time)` | 🔶 not-tested |  |
| `WriteMode` | `(Boolean)` | ✅ covered | MockFile.ALWriteMode property get/set |

## FileUpload  (2/3)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `CreateInStream` | `(InStream, TextEncoding)` | 🔶 not-tested |  |
| `CreateInStream` | `(InStream)` | ✅ covered | MockFileUpload.ALCreateInStream(parent, stream) and ALCreateInStream(parent, stream, encoding) |
| `FileName` | `()` | ✅ covered | MockFileUpload.ALFileName() returns '' for default instance |

## FilterPageBuilder  (11/12)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AddField` | `(Text, FieldRef, Text)` | ✅ covered | MockFilterPageBuilder; DataError overload delegates to base |
| `AddField` | `(Text, Joker, Text)` | 🔲 gap |  |
| `AddFieldNo` | `(Text, Integer, Text)` | ✅ covered | > |
| `AddRecord` | `(Text, Table)` | ✅ covered | MockFilterPageBuilder |
| `AddRecordRef` | `(Text, RecordRef)` | ✅ covered | MockFilterPageBuilder |
| `AddTable` | `(Text, Integer)` | ✅ covered | MockFilterPageBuilder; Count increments per call |
| `Count` | `()` | ✅ covered | MockFilterPageBuilder |
| `GetView` | `(Text, Boolean)` | ✅ covered | > |
| `Name` | `(Integer)` | ✅ covered | MockFilterPageBuilder; 1-based index into registered captions |
| `PageCaption` | `(Text)` | ✅ covered | MockFilterPageBuilder; get/set property |
| `RunModal` | `()` | ✅ covered | "overloads=2; MockFilterPageBuilder; returns bool (true=OK) — fixed CS0019 (was returning FormResult causing 'bool & FormResult' error in compound boolean expressions); BC emits ALRunModal(DataError) expecting bool return" |
| `SetView` | `(Text, Text)` | ✅ covered | MockFilterPageBuilder; stores view string per caption |

## Guid  (3/4)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `CreateGuid` | `()` | ✅ covered |  |
| `CreateSequentialGuid` | `()` | ✅ covered | . Covered: non-null, uniqueness (two successive calls differ). Standalone note — the sequential-ordering guarantee is not modelled; the helper delegates to Guid.NewGuid. |
| `ToText` | `()` | ✅ covered |  |
| `ToText` | `(Boolean)` | 🔶 not-tested |  |

## HttpClient  (10/18)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AddCertificate` | `(SecretText, SecretText)` | ✅ covered |  |
| `AddCertificate` | `(Text, Text)` | 🔶 not-tested |  |
| `Clear` | `()` | ✅ covered | instance method client.Clear() via ALClear(); global Clear(client) via MockHttpClient.Clear() — issue #1334 |
| `DefaultRequestHeaders` | `()` | ✅ covered |  |
| `Delete` | `(Text, HttpResponseMessage)` | ❌ not-possible |  |
| `Get` | `(Text, HttpResponseMessage)` | ❌ not-possible |  |
| `GetBaseAddress` | `()` | ✅ covered |  |
| `Patch` | `(Text, HttpContent, HttpResponseMessage)` | ❌ not-possible |  |
| `Post` | `(Text, HttpContent, HttpResponseMessage)` | ❌ not-possible |  |
| `Put` | `(Text, HttpContent, HttpResponseMessage)` | ❌ not-possible |  |
| `Send` | `(HttpRequestMessage, HttpResponseMessage)` | ❌ not-possible |  |
| `SetBaseAddress` | `(Text)` | ✅ covered |  |
| `Timeout` | `(Duration)` | ✅ covered |  |
| `UseDefaultNetworkWindowsAuthentication` | `()` | ✅ covered |  |
| `UseResponseCookies` | `(Boolean)` | ✅ covered |  |
| `UseServerCertificateValidation` | `(Boolean)` | ✅ covered |  |
| `UseWindowsAuthentication` | `(SecretText, SecretText, SecretText)` | ✅ covered |  |
| `UseWindowsAuthentication` | `(Text, Text, Text)` | 🔶 not-tested |  |

## HttpContent  (7/9)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Clear` | `()` | ✅ covered | ALClear() resets stored text content and headers |
| `GetHeaders` | `(HttpHeaders)` | ✅ covered | . MockHttpContent.ALGetHeaders is now a method (was a property; BC emits it as a method call with a ByRef out parameter). |
| `IsSecretContent` | `()` | ✅ covered | method ALIsSecretContent() always returns false |
| `ReadAs` | `(InStream)` | ✅ covered | ALReadAs returns bool (not void) so 'if Content.ReadAs(T) then' compiles without CS0019 (#1250) |
| `ReadAs` | `(SecretText)` | 🔶 not-tested |  |
| `ReadAs` | `(Text)` | 🔶 not-tested |  |
| `WriteFrom` | `(InStream)` | ✅ covered | text overload via ALLoadFrom(NavText); stream overload via AlCompat.HttpContentLoadFrom(MockInStream); SecretText overload via AlCompat.HttpContentLoadFrom(NavSecretText) — unwraps secret and stores as plain text (#1086) |
| `WriteFrom` | `(SecretText)` | ✅ covered | SecretText unwrapped via AlCompat.HttpContentLoadFrom(NavSecretText); round-trip via ReadAs(Text) proves value is stored (#1381) |
| `WriteFrom` | `(Text)` | ✅ covered | NavText passthrough via AlCompat.HttpContentLoadFrom(NavText) → ALLoadFrom(NavText); round-trip via ReadAs(Text) proves value is stored (#1381) |

## HttpHeaders  (10/13)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Add` | `(Text, SecretText)` | ✅ covered | Text overload (string key, string value); SecretText overload (string key, NavSecretText) — unwraps secret (#1086) |
| `Add` | `(Text, Text)` | 🔶 not-tested |  |
| `Clear` | `()` | ✅ covered |  |
| `Contains` | `(Text)` | ✅ covered |  |
| `ContainsSecret` | `(Text)` | ✅ covered | > |
| `GetSecretValues` | `(Text, Array)` | ✅ covered |  |
| `GetSecretValues` | `(Text, List)` | ✅ covered | ALGetValues(DataError, NavText, NavList<NavSecretText>) now populates the list; string-key overload also added for literal header names (#1381) |
| `GetValues` | `(Text, Array)` | ✅ covered | array-form (MockArray<NavText>) and list-form (NavList<NavText>) both covered; fixes issue #1080 |
| `GetValues` | `(Text, List)` | 🔶 not-tested |  |
| `Keys` | `()` | ✅ covered |  |
| `Remove` | `(Text)` | ✅ covered |  |
| `TryAddWithoutValidation` | `(Text, SecretText)` | ✅ covered | NavText/NavText overload; string/NavSecretText overload for literal-name + SecretText-value pattern — resolves string→NavText (#1091) and NavSecretText→NavText gaps |
| `TryAddWithoutValidation` | `(Text, Text)` | 🔶 not-tested |  |

## HttpRequestMessage  (11/12)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Content` | `(HttpContent)` | ✅ covered |  |
| `GetCookie` | `(Text, Cookie)` | ✅ covered | ALGetCookie(DataError, string, ByRef<MockCookie>) returns true/false and sets out-param. |
| `GetCookieNames` | `()` | ✅ covered | ALGetCookieNames() returns NavList<NavText> of all stored cookie names. |
| `GetHeaders` | `(HttpHeaders)` | ✅ covered |  |
| `GetRequestUri` | `()` | ✅ covered |  |
| `GetSecretRequestUri` | `()` | ✅ covered | ALGetSecretRequestUri(DataError, ByRef<string>) returns true when secret URI set, false otherwise. |
| `Method` | `(Text)` | ✅ covered |  |
| `RemoveCookie` | `(Text)` | ✅ covered | ALRemoveCookie(DataError, string) removes cookie by name, no-op if not found. |
| `SetCookie` | `(Cookie)` | ✅ covered | ALSetCookie(DataError, string, string) stores MockCookie keyed by name (case-insensitive). |
| `SetCookie` | `(Text, Text)` | 🔶 not-tested |  |
| `SetRequestUri` | `(Text)` | ✅ covered |  |
| `SetSecretRequestUri` | `(SecretText)` | ✅ covered | ALSetSecretRequestUri(DataError, string) stores URI string (SecretText unwrapped by BC). |

## HttpResponseMessage  (8/8)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Content` | `()` | ✅ covered |  |
| `GetCookie` | `(Text, Cookie)` | ✅ covered | ALGetCookie(DataError, NavText, ByRef<MockCookie>) always returns false |
| `GetCookieNames` | `()` | ✅ covered | ALGetCookieNames() → NavList<NavText>.Default (0-arg; GetCookieNames() returns list directly) |
| `Headers` | `()` | ✅ covered |  |
| `HttpStatusCode` | `()` | ✅ covered |  |
| `IsBlockedByEnvironment` | `()` | ✅ covered | property ALIsBlockedByEnvironment always returns false |
| `IsSuccessStatusCode` | `()` | ✅ covered |  |
| `ReasonPhrase` | `()` | ✅ covered |  |

## InStream  (6/14)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `EOS` | `()` | ✅ covered | BC emits ALEOS() method on NavInStream; MockInStream.ALEOS() added returning pos >= length |
| `Length` | `()` | ✅ covered |  |
| `Position` | `(BigInteger)` | ✅ covered |  |
| `Read` | `(BigInteger, Integer)` | ✅ covered |  |
| `Read` | `(Boolean, Integer)` | 🔶 not-tested |  |
| `Read` | `(Byte, Integer)` | 🔶 not-tested |  |
| `Read` | `(Char, Integer)` | 🔶 not-tested |  |
| `Read` | `(Decimal, Integer)` | 🔶 not-tested |  |
| `Read` | `(Guid, Integer)` | 🔶 not-tested |  |
| `Read` | `(Integer, Integer)` | 🔶 not-tested |  |
| `Read` | `(Joker, Integer)` | 🔶 not-tested |  |
| `Read` | `(Text, Integer)` | 🔶 not-tested |  |
| `ReadText` | `(Text, Integer)` | ✅ covered | chained-call pattern supported (e.g. blob.CreateInStream().ReadText(...)) — see issue #1026 |
| `ResetPosition` | `()` | ✅ covered |  |

## Integer  (1/1)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `ToText` | `()` | ✅ covered | RoslynRewriter redirects `<expr>.ToText()` to `AlCompat.Format(expr)`; NavInteger handled in AlScope.cs |

## IsolatedStorage  (5/11)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Contains` | `(Text, DataScope, Boolean)` | 🔶 not-tested |  |
| `Contains` | `(Text, DataScope)` | ✅ covered |  |
| `Delete` | `(Text, DataScope)` | ✅ covered |  |
| `Get` | `(Text, DataScope, SecretText)` | 🔶 not-tested |  |
| `Get` | `(Text, DataScope, Text)` | 🔶 not-tested |  |
| `Get` | `(Text, SecretText)` | ✅ covered |  |
| `Get` | `(Text, Text)` | 🔶 not-tested |  |
| `Set` | `(Text, SecretText, DataScope)` | ✅ covered |  |
| `Set` | `(Text, Text, DataScope)` | 🔶 not-tested |  |
| `SetEncrypted` | `(Text, SecretText, DataScope)` | ✅ covered | (with/without DataScope; Text and NavSecretText value). Rewriter routes to MockIsolatedStorage.ALSetEncrypted which stores plaintext — encryption is transparent standalone, value round-trips through Get/Contains. |
| `SetEncrypted` | `(Text, Text, DataScope)` | 🔶 not-tested |  |

## JsonArray  (73/90)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Add` | `(BigInteger)` | ✅ covered | . Covered via NavJsonArray native — Integer, Text, Boolean, and JsonObject Add forms all append and retain typed values. |
| `Add` | `(Boolean)` | 🔶 not-tested |  |
| `Add` | `(Byte)` | 🔶 not-tested |  |
| `Add` | `(Char)` | 🔶 not-tested |  |
| `Add` | `(Date)` | 🔶 not-tested |  |
| `Add` | `(DateTime)` | 🔶 not-tested |  |
| `Add` | `(Decimal)` | 🔶 not-tested |  |
| `Add` | `(Duration)` | 🔶 not-tested |  |
| `Add` | `(Integer)` | 🔶 not-tested |  |
| `Add` | `(JsonArray)` | 🔶 not-tested |  |
| `Add` | `(JsonObject)` | 🔶 not-tested |  |
| `Add` | `(JsonToken)` | 🔶 not-tested |  |
| `Add` | `(JsonValue)` | 🔶 not-tested |  |
| `Add` | `(Option)` | 🔶 not-tested |  |
| `Add` | `(Text)` | 🔶 not-tested |  |
| `Add` | `(Time)` | 🔶 not-tested |  |
| `AsToken` | `()` | ✅ covered | . Works natively via NavJsonToken.ALAsToken() — no rewriter redirect needed. Covered with IsArray() and Count() round-trip checks. |
| `Clone` | `()` | ✅ covered | . ALClone → MockJsonHelper.Clone (deep-clones via Newtonsoft DeepClone). Covered with empty-array, element-copy, and independence (mutation-after-clone) tests. |
| `Count` | `()` | ✅ covered | BC native NavJsonArray.ALCount works standalone. |
| `Get` | `(Integer, JsonToken)` | ✅ covered | (index, var JsonToken). Covered via NavJsonArray native — returns Boolean (true in-range, false out-of-range). |
| `GetArray` | `(Integer)` | ✅ covered | . Indirectly covered via JsonArray.Get + JsonToken.AsArray(). The BC 21+ typed GetArray(idx) overload is not present in the AL 16.2 compiler bundled with the runner. |
| `GetBigInteger` | `(Integer)` | ✅ covered | . Works natively via NavJsonArray; single-arg form GetBigInteger(idx) returns BigInteger directly. |
| `GetBoolean` | `(Integer)` | ✅ covered | . Indirectly covered via JsonArray.Get + JsonToken.AsValue().AsBoolean(). The BC 21+ typed GetBoolean(idx) overload is not present in the AL 16.2 compiler bundled with the runner. |
| `GetByte` | `(Integer)` | ✅ covered | . Works natively via NavJsonArray; single-arg form GetByte(idx) returns Byte directly. |
| `GetChar` | `(Integer)` | ✅ covered | . Works natively via NavJsonArray; single-arg form GetChar(idx) returns Char directly. |
| `GetDate` | `(Integer)` | ✅ covered | . Works natively via NavJsonArray; single-arg form GetDate(idx) returns Date directly. |
| `GetDateTime` | `(Integer)` | ✅ covered | . Works natively via NavJsonArray; single-arg form GetDateTime(idx) returns DateTime directly. |
| `GetDecimal` | `(Integer)` | ✅ covered | . Indirectly covered via JsonArray.Get + JsonToken.AsValue().AsDecimal(). The BC 21+ typed GetDecimal(idx) overload is not present in the AL 16.2 compiler bundled with the runner. |
| `GetDuration` | `(Integer)` | ✅ covered | . Works natively via NavJsonArray; single-arg form GetDuration(idx) returns Duration directly. |
| `GetInteger` | `(Integer)` | ✅ covered | . Indirectly covered via JsonArray.Get + JsonToken.AsValue().AsInteger(). The BC 21+ typed GetInteger(idx) overload is not present in the AL 16.2 compiler bundled with the runner. |
| `GetObject` | `(Integer)` | ✅ covered | . Integer-indexed overload GetObject(idx) now covered via MockJsonHelper.GetObject(token, int) — issue #1025. |
| `GetOption` | `(Integer)` | ✅ covered | . Works natively via NavJsonArray; single-arg form GetOption(idx) returns Integer ordinal directly. |
| `GetText` | `(Integer)` | ✅ covered | . Indirectly covered via JsonArray.Get + JsonToken.AsValue().AsText(). The BC 21+ typed GetText(idx) overload is not present in the AL 16.2 compiler bundled with the runner. |
| `GetTime` | `(Integer)` | ✅ covered | . Works natively via NavJsonArray; single-arg form GetTime(idx) returns Time directly. |
| `IndexOf` | `(BigInteger)` | ✅ covered | . Covered via NavJsonArray native — returns 0-based index when found, -1 when absent. |
| `IndexOf` | `(Boolean)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `IndexOf` | `(Byte)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `IndexOf` | `(Char)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `IndexOf` | `(Date)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `IndexOf` | `(DateTime)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `IndexOf` | `(Decimal)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `IndexOf` | `(Duration)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `IndexOf` | `(Integer)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `IndexOf` | `(JsonArray)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `IndexOf` | `(JsonObject)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `IndexOf` | `(JsonToken)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `IndexOf` | `(JsonValue)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `IndexOf` | `(Option)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `IndexOf` | `(Text)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `IndexOf` | `(Time)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Insert` | `(Integer, BigInteger)` | ✅ covered | . Covered via NavJsonArray native — increases Count, shifts existing elements, middle-position insertion correct. |
| `Insert` | `(Integer, Boolean)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Insert` | `(Integer, Byte)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Insert` | `(Integer, Char)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Insert` | `(Integer, Date)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Insert` | `(Integer, DateTime)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Insert` | `(Integer, Decimal)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Insert` | `(Integer, Duration)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Insert` | `(Integer, Integer)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Insert` | `(Integer, JsonArray)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Insert` | `(Integer, JsonObject)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Insert` | `(Integer, JsonToken)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Insert` | `(Integer, JsonValue)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Insert` | `(Integer, Option)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Insert` | `(Integer, Text)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Insert` | `(Integer, Time)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Path` | `()` | ✅ covered | . Covered via NavJsonArray native — root returns "$", nested returns "$.key" (JSONPath notation). |
| `ReadFrom` | `(InStream)` | ✅ covered |  |
| `ReadFrom` | `(Text)` | 🔶 not-tested |  |
| `RemoveAt` | `(Integer)` | ✅ covered | . Covered via NavJsonArray native — decreases Count, shifts remaining elements left, not-a-no-op trap. |
| `SelectToken` | `(Text, JsonToken)` | ✅ covered |  |
| `SelectTokens` | `(Text, List)` | ✅ covered |  |
| `Set` | `(Integer, BigInteger)` | ✅ covered | . Covered via NavJsonArray native — replaces element at index, Count unchanged. |
| `Set` | `(Integer, Boolean)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Set` | `(Integer, Byte)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Set` | `(Integer, Char)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Set` | `(Integer, Date)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Set` | `(Integer, DateTime)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Set` | `(Integer, Decimal)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Set` | `(Integer, Duration)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Set` | `(Integer, Integer)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Set` | `(Integer, JsonArray)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Set` | `(Integer, JsonObject)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Set` | `(Integer, JsonToken)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Set` | `(Integer, JsonValue)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Set` | `(Integer, Option)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Set` | `(Integer, Text)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Set` | `(Integer, Time)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `WriteTo` | `(OutStream)` | ✅ covered |  |
| `WriteTo` | `(Text)` | 🔶 not-tested |  |

## JsonObject  (46/66)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Add` | `(Text, BigInteger)` | ✅ covered | works natively via NavJsonObject (no TrappableOperationExecutor path); used in GetBoolean tests |
| `Add` | `(Text, Boolean)` | 🔶 not-tested |  |
| `Add` | `(Text, Byte)` | 🔶 not-tested |  |
| `Add` | `(Text, Char)` | 🔶 not-tested |  |
| `Add` | `(Text, Date)` | 🔶 not-tested |  |
| `Add` | `(Text, DateTime)` | 🔶 not-tested |  |
| `Add` | `(Text, Decimal)` | 🔶 not-tested |  |
| `Add` | `(Text, Duration)` | 🔶 not-tested |  |
| `Add` | `(Text, Integer)` | 🔶 not-tested |  |
| `Add` | `(Text, JsonArray)` | 🔶 not-tested |  |
| `Add` | `(Text, JsonObject)` | 🔶 not-tested |  |
| `Add` | `(Text, JsonToken)` | 🔶 not-tested |  |
| `Add` | `(Text, JsonValue)` | 🔶 not-tested |  |
| `Add` | `(Text, Option)` | 🔶 not-tested |  |
| `Add` | `(Text, Text)` | 🔶 not-tested |  |
| `Add` | `(Text, Time)` | 🔶 not-tested |  |
| `AsToken` | `()` | ✅ covered | works natively via NavJsonObject; tested |
| `Clone` | `()` | ✅ covered | rewriter redirects ALClone to MockJsonHelper.Clone (deep-clone via Newtonsoft.Json) |
| `Contains` | `(Text)` | ✅ covered | rewriter redirects ALContains to MockJsonHelper.Contains |
| `Get` | `(Text, JsonToken)` | ✅ covered | rewriter redirects ALGet to MockJsonHelper.Get (JObject key lookup, returns ByRef NavJsonToken) |
| `GetArray` | `(Text, Boolean)` | ✅ covered | rewriter redirects ALGetArray to MockJsonHelper.GetArray |
| `GetBigInteger` | `(Text, Boolean)` | ✅ covered | works natively via NavJsonObject (no TrappableOperationExecutor path); BC stores BigInteger as integer in JSON |
| `GetBoolean` | `(Text, Boolean)` | ✅ covered | 2-arg overload (key, requireValueExists); returns false when key is missing and requireValueExists=false |
| `GetByte` | `(Text, Boolean)` | ✅ covered | works natively via NavJsonObject (no TrappableOperationExecutor path); BC stores Byte as integer in JSON |
| `GetChar` | `(Text, Boolean)` | ✅ covered | works natively via NavJsonObject (no TrappableOperationExecutor path); BC stores Char as integer code point in JSON |
| `GetDate` | `(Text, Boolean)` | ✅ covered | works natively via NavJsonObject (no TrappableOperationExecutor path); BC stores Date as ISO 8601 string in JSON |
| `GetDateTime` | `(Text, Boolean)` | ✅ covered | works natively via NavJsonObject (no TrappableOperationExecutor path); BC stores DateTime as ISO 8601 string in JSON |
| `GetDecimal` | `(Text, Boolean)` | ✅ covered | 2-arg overload (key, requireValueExists); returns 0 when key is missing and requireValueExists=false |
| `GetDuration` | `(Text, Boolean)` | ✅ covered | works natively via NavJsonObject (no TrappableOperationExecutor path); BC stores Duration as integer milliseconds in JSON |
| `GetInteger` | `(Text, Boolean)` | ✅ covered | 2-arg overload (key, requireValueExists); returns 0 when key is missing and requireValueExists=false |
| `GetObject` | `(Text, Boolean)` | ✅ covered | rewriter redirects ALGetObject to MockJsonHelper.GetObject |
| `GetOption` | `(Text, Boolean)` | ✅ covered | works natively via NavJsonObject (no TrappableOperationExecutor path); returns integer ordinal |
| `GetText` | `(Text, Boolean)` | ✅ covered | rewriter redirects ALGetText to MockJsonHelper.GetText; 2-arg bool overload GetText(key, requireValueExists) now covered — issue #1025 |
| `GetTime` | `(Text, Boolean)` | ✅ covered | works natively via NavJsonObject (no TrappableOperationExecutor path); BC stores Time as integer milliseconds-from-midnight in JSON |
| `Keys` | `()` | ✅ covered | rewriter redirects ALKeys to MockJsonHelper.Keys (returns NavList<NavText>) |
| `Path` | `()` | ✅ covered | rewriter intercepts ALPath property → MockJsonHelper.Path; root object returns "$" |
| `ReadFrom` | `(InStream)` | ✅ covered |  |
| `ReadFrom` | `(Text)` | 🔶 not-tested |  |
| `ReadFromYaml` | `(InStream)` | ✅ covered | rewriter redirects ALReadFromYaml to MockJsonHelper.ReadFromYaml; stub delegates to ReadFrom (JSON round-trip) — YamlDotNet not available in runner |
| `ReadFromYaml` | `(Text)` | 🔶 not-tested |  |
| `Remove` | `(Text)` | ✅ covered | rewriter redirects ALRemove to MockJsonHelper.Remove |
| `Replace` | `(Text, BigInteger)` | ✅ covered | rewriter redirects ALReplace to MockJsonHelper.Replace |
| `Replace` | `(Text, Boolean)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Replace` | `(Text, Byte)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Replace` | `(Text, Char)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Replace` | `(Text, Date)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Replace` | `(Text, DateTime)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Replace` | `(Text, Decimal)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Replace` | `(Text, Duration)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Replace` | `(Text, Integer)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Replace` | `(Text, JsonArray)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Replace` | `(Text, JsonObject)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Replace` | `(Text, JsonToken)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Replace` | `(Text, JsonValue)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Replace` | `(Text, Option)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Replace` | `(Text, Text)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `Replace` | `(Text, Time)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `SelectToken` | `(Text, JsonToken)` | ✅ covered |  |
| `SelectTokens` | `(Text, List)` | ✅ covered |  |
| `Values` | `()` | ✅ covered | works natively via NavJsonObject (no TrappableOperationExecutor path); returns List of [JsonToken] in insertion order |
| `WriteTo` | `(OutStream)` | ✅ covered |  |
| `WriteTo` | `(Text)` | 🔶 not-tested |  |
| `WriteToYaml` | `(OutStream)` | ✅ covered | rewriter redirects ALWriteToYaml to MockJsonHelper.WriteToYaml; stub delegates to WriteTo (JSON serialization — valid YAML) — YamlDotNet not available in runner |
| `WriteToYaml` | `(Text)` | 🔶 not-tested |  |
| `WriteWithSecretsTo` | `(Dictionary, SecretText)` | ✅ covered | rewriter redirects ALWriteWithSecretsTo to MockJsonHelper.WriteWithSecretsTo; in runner secrets are treated as plain text — serializes JSON to NavSecretText; secrets dict ignored |
| `WriteWithSecretsTo` | `(Text, SecretText, SecretText)` | 🔶 not-tested |  |

## JsonToken  (12/14)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AsArray` | `()` | ✅ covered | works natively via BC runtime (no mock needed) |
| `AsObject` | `()` | ✅ covered | works natively via BC runtime (no mock needed) |
| `AsValue` | `()` | ✅ covered | works natively via BC runtime (no mock needed) |
| `Clone` | `()` | ✅ covered | redirected via MockJsonHelper.Clone |
| `IsArray` | `()` | ✅ covered | redirected via MockJsonHelper.IsArray |
| `IsObject` | `()` | ✅ covered | redirected via MockJsonHelper.IsObject |
| `IsValue` | `()` | ✅ covered | redirected via MockJsonHelper.IsValue |
| `Path` | `()` | ✅ covered | BC emits ALPath as property access; VisitMemberAccessExpression intercepts ALPath→MockJsonHelper.Path(token) which converts Newtonsoft format to BC $-prefixed format |
| `ReadFrom` | `(InStream)` | ✅ covered |  |
| `ReadFrom` | `(Text)` | 🔶 not-tested |  |
| `SelectToken` | `(Text, JsonToken)` | ✅ covered |  |
| `SelectTokens` | `(Text, List)` | ✅ covered |  |
| `WriteTo` | `(OutStream)` | ✅ covered |  |
| `WriteTo` | `(Text)` | 🔶 not-tested |  |

## JsonValue  (34/37)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AsBigInteger` | `()` | ✅ covered | Covered via NavJsonValue native — SetValue + typed As* round-trip |
| `AsBoolean` | `()` | ✅ covered | BC native NavJsonValue.ALAsBoolean works standalone |
| `AsByte` | `()` | ✅ covered | Covered via NavJsonValue native — SetValue + typed As* round-trip |
| `AsChar` | `()` | ✅ covered | Covered via NavJsonValue native — SetValue + typed As* round-trip |
| `AsCode` | `()` | ✅ covered | Covered via NavJsonValue native — SetValue + typed As* round-trip |
| `AsDate` | `()` | ✅ covered | Covered via NavJsonValue native — SetValue + typed As* round-trip |
| `AsDateTime` | `()` | ✅ covered | Covered via NavJsonValue native — SetValue + typed As* round-trip |
| `AsDecimal` | `()` | ✅ covered | BC native NavJsonValue.ALAsDecimal works standalone |
| `AsDuration` | `()` | ✅ covered | Covered via NavJsonValue native — SetValue + typed As* round-trip |
| `AsInteger` | `()` | ✅ covered | BC native NavJsonValue.ALAsInteger works standalone |
| `AsOption` | `()` | ✅ covered | Covered via NavJsonValue native — SetValue + typed As* round-trip |
| `AsText` | `()` | ✅ covered | BC native NavJsonValue.ALAsText works standalone |
| `AsTime` | `()` | ✅ covered | Covered via NavJsonValue native — SetValue + typed As* round-trip |
| `AsToken` | `()` | ✅ covered | . Covered via NavJsonValue native — AsToken().AsValue().AsInteger() round-trips. |
| `Clone` | `()` | ✅ covered | . Covered via NavJsonValue native — Clone produces independent copy. |
| `IsNull` | `()` | ✅ covered | BC native NavJsonValue.ALIsNull works standalone |
| `IsUndefined` | `()` | ✅ covered | . Covered via NavJsonValue native — default-initialised returns false (null != undefined), after SetValue also false. |
| `Path` | `()` | ✅ covered | . Covered via NavJsonValue native — nested value under "score" returns "$.score" (JSONPath notation). |
| `ReadFrom` | `(InStream)` | ✅ covered |  |
| `ReadFrom` | `(Text)` | 🔶 not-tested |  |
| `SelectToken` | `(Text, JsonToken)` | ✅ covered |  |
| `SetValue` | `(BigInteger)` | ✅ covered | text/integer/boolean/decimal overloads proven; BC native works standalone |
| `SetValue` | `(Boolean)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `SetValue` | `(Byte)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `SetValue` | `(Char)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `SetValue` | `(Date)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `SetValue` | `(DateTime)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `SetValue` | `(Decimal)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `SetValue` | `(Duration)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `SetValue` | `(Integer)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `SetValue` | `(Option)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `SetValue` | `(Text)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `SetValue` | `(Time)` | ✅ covered | works natively via NavJsonToken implicit-conversion (BC runtime); covered by tests/bucket-2/data-formats/309-json-primitive-overloads |
| `SetValueToNull` | `()` | ✅ covered | BC native works standalone; verified with IsNull |
| `SetValueToUndefined` | `()` | ❓ stub | BC 21+ method not tested in AL 16.2; the underlying NavJsonValue method exists but no AL syntax available in 16.2 to exercise it. |
| `WriteTo` | `(OutStream)` | ✅ covered |  |
| `WriteTo` | `(Text)` | 🔶 not-tested |  |

## KeyRef  (4/4)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Active` | `()` | ✅ covered |  |
| `FieldCount` | `()` | ✅ covered |  |
| `FieldIndex` | `(Integer)` | ✅ covered |  |
| `Record` | `()` | ✅ covered |  |

## Label  (19/19)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Contains` | `(Text)` | ✅ covered | BC native NavTextExtensions.ALContains works standalone when called on Label-typed values. |
| `EndsWith` | `(Text)` | ✅ covered | BC native works standalone. |
| `IndexOf` | `(Text, Integer)` | ✅ covered | BC native works standalone (1-based, 0 when not found). |
| `IndexOfAny` | `(List, Integer)` | ✅ covered | . Covered via NavText native — returns 1-based position of the earliest matching char; 0 when none match. |
| `IndexOfAny` | `(Text, Integer)` | ✅ covered | BC native NavTextExtensions.ALIndexOfAny works standalone (2-arg startIndex form). Tested positive and negative cases. |
| `LastIndexOf` | `(Text, Integer)` | ✅ covered | . Covered via NavText native — 1-based last occurrence, 0 when not found. |
| `PadLeft` | `(Integer, Char)` | ✅ covered | (with padChar). Covered via NavText native. |
| `PadRight` | `(Integer, Char)` | ✅ covered | (with padChar). Covered via NavText native. |
| `Remove` | `(Integer, Integer)` | ✅ covered | (from-index). Covered via NavText native — 1-based AL convention. |
| `Replace` | `(Text, Text)` | ✅ covered | BC native works standalone. No-op when substring not found. |
| `Split` | `(List)` | ✅ covered | (Char, Text, List of [Char]). Covered via NavText native. |
| `Split` | `(Text)` | ✅ covered | BC native NavText.ALSplit works on Label values. Multi-char separator tested — positive and negative (absent separator) cases. |
| `StartsWith` | `(Text)` | ✅ covered | BC native works standalone. |
| `Substring` | `(Integer, Integer)` | ✅ covered | (from-index, from-index+length). Covered via NavText native. |
| `ToLower` | `()` | ✅ covered | BC native works standalone. |
| `ToUpper` | `()` | ✅ covered | BC native works standalone. |
| `Trim` | `()` | ✅ covered | BC native works standalone (removes leading/trailing whitespace). |
| `TrimEnd` | `(Text)` | ✅ covered | . Covered via NavText native — trailing strip only, differs-from-TrimStart trap. |
| `TrimStart` | `(Text)` | ✅ covered | . Covered via NavText native — leading strip only. |

## Media  (7/8)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `ExportFile` | `(Text)` | ✅ covered | "overloads=2; BC emits ALExport(DataError, fileName) — returns false (no data in standalone)" |
| `ExportStream` | `(OutStream)` | ✅ covered | "overloads=2; BC emits ALExport(DataError, OutStream) for ExportStream — stream overload added to MockMedia; no-op in standalone mode" |
| `FindOrphans` | `()` | ✅ covered | "Static method returning List of [Guid]; MockMedia.ALFindOrphans() returns NavList<Guid>.Default (empty list) — no orphaned media in standalone mode." |
| `HasValue` | `()` | ✅ covered | "overloads=1; BC emits ALHasValue property (not method)" |
| `ImportFile` | `(Text, Text, Text)` | ✅ covered | "overloads=4; BC emits ALImport(DataError, fileName, description[, mimeType]) — returns Guid (media ID)" |
| `ImportStream` | `(InStream, Text, Text, Text)` | 🔲 gap |  |
| `ImportStream` | `(InStream, Text, Text)` | ✅ covered | "overloads=2; BC emits ALImport(DataError, InStream, description) for ImportStream — stream overload added to MockMedia; sets HasValue=true" |
| `MediaId` | `()` | ✅ covered | "MockMedia.ALMediaId property (not method); fixes CS1503 method-group-to-object when used as argument" |

## MediaSet  (9/9)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Count` | `()` | ✅ covered | "MockMediaSet.ALCount property; returns count of inserted items" |
| `ExportFile` | `(Text)` | ✅ covered | "MockMediaSet.ALExport returns 0 (no blob data in standalone mode); BC return type is Integer, not Boolean" |
| `FindOrphans` | `()` | ✅ covered | "Static method returning List of [Guid]; MockMediaSet.ALFindOrphans() returns NavList<Guid>.Default (empty list) — no orphaned media in standalone mode." |
| `ImportFile` | `(Text, Text, Text)` | ✅ covered | "MockMediaSet.ALImport returns a new Guid (media ID); BC return type is Guid, not Boolean" |
| `ImportStream` | `(InStream, Text, Text)` | ✅ covered | "MockMediaSet.ALImport(DataError, MockInStream, string) overload; adds a new Guid to the set" |
| `Insert` | `(Guid)` | ✅ covered | "MockMediaSet.ALInsert(DataError, Guid) returns true; adds GUID to in-memory list" |
| `Item` | `(Integer)` | ✅ covered | "MockMediaSet.ALItem(int index) returns 1-based GUID from in-memory list" |
| `MediaId` | `()` | ✅ covered | "MockMediaSet.ALMediaId property; stable per-instance GUID" |
| `Remove` | `(Guid)` | ✅ covered | "MockMediaSet.ALRemove(DataError, Guid) returns true if found, false if absent" |

## ModuleDependencyInfo  (3/3)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Id` | `()` | ✅ covered |  |
| `Name` | `()` | ✅ covered |  |
| `Publisher` | `()` | ✅ covered |  |

## ModuleInfo  (7/7)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AppVersion` | `()` | ✅ covered | default returns Version 0.0.0.0; tests/bucket-1/codeunit-runtime/267-moduleinfo-properties |
| `DataVersion` | `()` | ✅ covered | default returns Version 0.0.0.0; tests/bucket-1/codeunit-runtime/267-moduleinfo-properties |
| `Dependencies` | `()` | ✅ covered | default returns empty List of [ModuleDependencyInfo]; tests/bucket-1/codeunit-runtime/267-moduleinfo-properties |
| `Id` | `()` | ✅ covered | default returns empty GUID; tests/bucket-1/codeunit-runtime/267-moduleinfo-properties |
| `Name` | `()` | ✅ covered | default returns empty string; tests/bucket-1/codeunit-runtime/267-moduleinfo-properties |
| `PackageId` | `()` | ✅ covered | default returns empty GUID; tests/bucket-1/codeunit-runtime/267-moduleinfo-properties |
| `Publisher` | `()` | ✅ covered | default returns empty string; tests/bucket-1/codeunit-runtime/267-moduleinfo-properties |

## NavApp  (16/16)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `DeleteArchiveData` | `(Integer)` | ✅ covered | . No-op standalone — no archive store. Stub on MockNavApp. |
| `GetArchiveRecordRef` | `(Integer, RecordRef)` | ✅ covered | no-op standalone — leaves RecordRef unbound. Stub on MockNavApp. |
| `GetArchiveVersion` | `()` | ✅ covered | . Returns empty — no archive in standalone mode. Stub on MockNavApp. |
| `GetCallerCallstackModuleInfos` | `()` | ✅ covered |  |
| `GetCallerModuleInfo` | `(ModuleInfo)` | ✅ covered |  |
| `GetCurrentModuleInfo` | `(ModuleInfo)` | ✅ covered |  |
| `GetModuleInfo` | `(Guid, ModuleInfo)` | ✅ covered |  |
| `GetResource` | `(Text, InStream, TextEncoding)` | ✅ covered | > |
| `GetResourceAsJson` | `(Text, TextEncoding)` | ✅ covered | (NavText/string × with/without TextEncoding); MockNavApp.ALGetResourceAsJson() returns default NavJsonObject — no .app in standalone mode; string overloads fix CS1503 (issue #1107) |
| `GetResourceAsText` | `(Text, TextEncoding)` | ✅ covered | (NavText/string × with/without TextEncoding); MockNavApp.ALGetResourceAsText() returns NavText.Empty — no .app in standalone mode; string overloads fix CS1503 (issue #1107) |
| `IsEntitled` | `(Text, Guid)` | ✅ covered | (NavText 2-arg, NavText 3-arg with AppId, string 2-arg, string 3-arg); string overloads added in #1231 to fix CS1503 when BC emits string literal for Text argument; MockNavApp.ALIsEntitled() returns true (always entitled in standalone) |
| `IsInstalling` | `()` | ✅ covered | MockNavApp.ALIsInstalling() returns false (no install lifecycle) |
| `IsUnlicensed` | `(Guid)` | ✅ covered | MockNavApp.ALIsUnlicensed() returns false (no license enforcement) |
| `ListResources` | `(Text)` | ✅ covered | (with/without ResourceType); MockNavApp.ALListResources() returns NavList<NavText>.Default — no .app in standalone mode |
| `LoadPackageData` | `(Integer)` | ✅ covered | . No-op standalone — no .app package data to import. Stub on MockNavApp. |
| `RestoreArchiveData` | `(Integer, Boolean)` | ✅ covered | . No-op standalone — no archive store. Stub on MockNavApp. |

## Notification  (9/10)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AddAction` | `(Text, Integer, Text, Text)` | 🔶 not-tested |  |
| `AddAction` | `(Text, Integer, Text)` | ✅ covered |  |
| `GetData` | `(Text)` | ✅ covered |  |
| `HasData` | `(Text)` | ✅ covered |  |
| `Id` | `(Guid)` | ✅ covered |  |
| `Message` | `(Text)` | ✅ covered |  |
| `Recall` | `()` | ✅ covered | > |
| `Scope` | `(NotificationScope)` | ✅ covered | > |
| `Send` | `()` | ✅ covered |  |
| `SetData` | `(Text, Text)` | ✅ covered |  |

## NumberSequence  (7/8)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Current` | `(Text, Boolean)` | ✅ covered |  |
| `Delete` | `(Text, Boolean)` | ✅ covered |  |
| `Exists` | `(Text, Boolean)` | ✅ covered |  |
| `Insert` | `(Text, BigInteger, BigInteger, Boolean)` | ✅ covered |  |
| `Next` | `(Text, Boolean)` | ✅ covered |  |
| `Range` | `(Text, Integer, BigInteger, Boolean)` | 🔶 not-tested |  |
| `Range` | `(Text, Integer, Boolean)` | ✅ covered | added ALRange(name, count) and ALRange(name, count, companySpecific) to MockNumberSequence; also added ALInsert 4-arg overload; reserves Count values, returns first |
| `Restart` | `(Text, BigInteger, Boolean)` | ✅ covered |  |

## OutStream  (2/23)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Write` | `(BigInteger, Integer)` | ✅ covered | chained-call pattern supported (e.g. blob.CreateOutStream().Write(...)) — see issue #1026 |
| `Write` | `(BigText, Integer)` | 🔶 not-tested |  |
| `Write` | `(Boolean, Integer)` | 🔶 not-tested |  |
| `Write` | `(Byte, Integer)` | 🔶 not-tested |  |
| `Write` | `(Char, Integer)` | 🔶 not-tested |  |
| `Write` | `(Code, Integer)` | 🔶 not-tested |  |
| `Write` | `(Date, Integer)` | 🔶 not-tested |  |
| `Write` | `(DateFormula, Integer)` | 🔶 not-tested |  |
| `Write` | `(DateTime, Integer)` | 🔶 not-tested |  |
| `Write` | `(Decimal, Integer)` | 🔶 not-tested |  |
| `Write` | `(Duration, Integer)` | 🔶 not-tested |  |
| `Write` | `(Guid, Integer)` | 🔶 not-tested |  |
| `Write` | `(Integer, Integer)` | 🔶 not-tested |  |
| `Write` | `(Joker, Integer)` | 🔶 not-tested |  |
| `Write` | `(Label, Integer)` | 🔶 not-tested |  |
| `Write` | `(Option, Integer)` | 🔶 not-tested |  |
| `Write` | `(RecordId, Integer)` | 🔶 not-tested |  |
| `Write` | `(Table, Integer)` | 🔶 not-tested |  |
| `Write` | `(Text, Integer)` | 🔶 not-tested |  |
| `Write` | `(TextConst, Integer)` | 🔶 not-tested |  |
| `Write` | `(Time, Integer)` | 🔶 not-tested |  |
| `Write` | `(Variant, Integer)` | 🔶 not-tested |  |
| `WriteText` | `(Text, Integer)` | ✅ covered | chained-call pattern supported (e.g. blob.CreateOutStream().WriteText(...)) — see issue #1026 |

## Page  (29/29)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Activate` | `(Boolean)` | ✅ covered |  |
| `CancelBackgroundTask` | `(Integer)` | ✅ covered | no-op in standalone mode |
| `Caption` | `(Text)` | ✅ covered |  |
| `Close` | `()` | ✅ covered |  |
| `Editable` | `(Boolean)` | ✅ covered |  |
| `EnqueueBackgroundTask` | `(Integer, Integer, Dictionary, Integer, PageBackgroundTaskErrorLevel)` | ✅ covered | overload=1; AL(taskId, codeunitId, params, timeout, PageBackgroundTaskErrorLevel) → C#(DataError, ByRef<int>, int, NavDictionary, int, PageBackgroundTaskErrorLevel); closes #1327 |
| `GetBackgroundParameters` | `()` | ✅ covered | returns empty NavDictionary in standalone mode |
| `GetRecord` | `(Table)` | ✅ covered |  |
| `LookupMode` | `(Boolean)` | ✅ covered | . Also proven on instance Page<N> — setter + getter round-trip. Injected on Page<N> class so CurrPage.LookupMode inside a page trigger compiles (issue #1079). |
| `ObjectId` | `(Boolean)` | ✅ covered |  |
| `PromptMode` | `(PromptMode)` | ✅ covered | MockCurrPage.PromptMode and MockFormHandle.PromptMode NavOption stubs; injected on Page<N> class for CurrPage.PromptMode access inside page triggers (issue #1079); RoslynRewriter converts static self-reference Page<N>.PromptMode → this.PromptMode to fix CS0120 (issue #1266) |
| `Run` | `()` | ✅ covered |  |
| `Run` | `(Integer, Table, Integer)` | ✅ covered | 3rd argument (position/focus field) accepted and ignored — no real UI in standalone mode. |
| `Run` | `(Integer, Table, Joker)` | ✅ covered | 3rd argument (position/focus field as Joker) accepted and ignored — no real UI in standalone mode. |
| `Run` | `(Text, Table, Integer)` | ✅ covered | Text page-ID form. 3rd argument (position/focus field) accepted and ignored — no real UI in standalone mode. |
| `Run` | `(Text, Table, Joker)` | ✅ covered | Text page-ID form. 3rd argument (position/focus field as Joker) accepted and ignored — no real UI in standalone mode. |
| `RunModal` | `()` | ✅ covered | . Instance form Page<N>.RunModal() dispatches to ModalPageHandler. Injected on Page<N> class so CurrPage.RunModal() inside a page trigger compiles (issue #1079). |
| `RunModal` | `(Integer, Table, FieldRef)` | ✅ covered | 3rd argument (position as FieldRef) accepted and ignored — no real UI in standalone mode. Returns default(FormResult) = Action::None. |
| `RunModal` | `(Integer, Table, Integer)` | ✅ covered | 3rd argument (position as integer field number) accepted and ignored — no real UI in standalone mode. Returns default(FormResult) = Action::None. |
| `RunModal` | `(Integer, Table, Joker)` | ✅ covered | 3rd argument (position as Joker) accepted and ignored — no real UI in standalone mode. Returns default(FormResult) = Action::None. |
| `RunModal` | `(Text, Table, FieldRef)` | ✅ covered | Text page-ID form. 3rd argument (position as FieldRef) accepted and ignored — no real UI in standalone mode. Returns default(FormResult) = Action::None. |
| `RunModal` | `(Text, Table, Integer)` | ✅ covered | Text page-ID form. 3rd argument (position as integer field number) accepted and ignored — no real UI in standalone mode. Returns default(FormResult) = Action::None. |
| `RunModal` | `(Text, Table, Joker)` | ✅ covered | Text page-ID form. 3rd argument (position as Joker) accepted and ignored — no real UI in standalone mode. Returns default(FormResult) = Action::None. |
| `SaveRecord` | `()` | ✅ covered |  |
| `SetBackgroundTaskResult` | `(Dictionary)` | ✅ covered | no-op in standalone mode |
| `SetRecord` | `(Table)` | ✅ covered |  |
| `SetSelectionFilter` | `(Table)` | ✅ covered |  |
| `SetTableView` | `(Table)` | ✅ covered |  |
| `Update` | `(Boolean)` | ✅ covered | > |

## ProductName  (3/3)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Full` | `()` | ✅ covered |  |
| `Marketing` | `()` | ✅ covered |  |
| `Short` | `()` | ✅ covered |  |

## Query  (3/5)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `SaveAsCsv` | `(Integer, OutStream, Integer, Text)` | ✅ covered |  |
| `SaveAsCsv` | `(Integer, Text, Integer, Text)` | 🔶 not-tested |  |
| `SaveAsJson` | `(Integer, OutStream)` | ✅ covered |  |
| `SaveAsXml` | `(Integer, OutStream)` | ✅ covered |  |
| `SaveAsXml` | `(Integer, Text)` | 🔶 not-tested |  |

## QueryInstance  (15/17)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Close` | `()` | ✅ covered | MockQueryHandle.ALClose is a no-op stub. |
| `ColumnCaption` | `(Joker)` | ✅ covered | MockQueryHandle.ALColumnCaption returns stub "Column{n}". |
| `ColumnName` | `(Joker)` | ✅ covered | MockQueryHandle.ALColumnName returns stub "Column{n}". |
| `ColumnNo` | `(Joker)` | ✅ covered | MockQueryHandle.ALColumnNo returns the column number as-is. |
| `GetFilter` | `(Joker)` | ✅ covered | MockQueryHandle.ALGetFilter returns empty string (filters not tracked). |
| `GetFilters` | `()` | ✅ covered | MockQueryHandle.ALGetFilters property returns empty string. |
| `Open` | `()` | ✅ covered | MockQueryHandle.ALOpen reads from in-memory table store via QueryFieldRegistry metadata; falls back to NotSupportedException for unregistered queries. |
| `Read` | `()` | ✅ covered | MockQueryHandle.ALRead iterates in-memory result set built by ALOpen; returns column values via hash mapping. |
| `SaveAsCsv` | `(OutStream, Integer, Text)` | ✅ covered | MockQueryHandle.ALSaveAsCsv throws NotSupportedException. |
| `SaveAsCsv` | `(Text, Integer, Text)` | 🔲 gap |  |
| `SaveAsJson` | `(OutStream)` | ✅ covered | MockQueryHandle.ALSaveAsJson throws NotSupportedException. |
| `SaveAsXml` | `(OutStream)` | ✅ covered | MockQueryHandle.ALSaveAsXml throws NotSupportedException. |
| `SaveAsXml` | `(Text)` | 🔲 gap |  |
| `SecurityFiltering` | `(SecurityFilter)` | ✅ covered | MockQueryHandle.ALSecurityFiltering property get/set. |
| `SetFilter` | `(Joker, Text, Joker)` | ✅ covered | MockQueryHandle.ALSetFilter tracks column filters applied during ALOpen. |
| `SetRange` | `(Joker, Joker, Joker)` | ✅ covered | MockQueryHandle.ALSetRangeSafe tracks range filters applied during ALOpen (clear/single/range). |
| `TopNumberOfRows` | `(Integer)` | ✅ covered | MockQueryHandle.ALTopNumberOfRowsToReturn property get/set. |

## RecordId  (2/2)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `GetRecord` | `()` | ✅ covered | the 1-argument form GetRecord(var Rec) does not exist in BC 26–28 (AL0126). The 0-argument form GetRecord() is intercepted by RoslynRewriter (ALGetRecord → new MockRecordRef()) and returns an unbound RecordRef in standalone mode. |
| `TableNo` | `()` | ✅ covered | BC native NavRecordId.ALTableNo works standalone — returns 0 for default/empty RecordId. |

## RecordRef  (75/86)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AddLink` | `(Text, Text)` | ✅ covered | MockRecordRef.ALAddLink no-op, returns 0 (no BC link service in standalone) |
| `AddLoadFields` | `(Integer)` | ✅ covered | MockRecordRef.ALAddLoadFields no-op (all fields always loaded in standalone) |
| `AreFieldsLoaded` | `(Integer)` | ✅ covered | MockRecordRef.ALAreFieldsLoaded always returns true in standalone |
| `Ascending` | `(Boolean)` | ✅ covered |  |
| `Caption` | `()` | ✅ covered |  |
| `ChangeCompany` | `(Text)` | ✅ covered |  |
| `ClearMarks` | `()` | ✅ covered |  |
| `Close` | `()` | ✅ covered |  |
| `Copy` | `(RecordRef, Boolean)` | ✅ covered |  |
| `Copy` | `(Table, Boolean)` | 🔶 not-tested |  |
| `CopyLinks` | `(RecordRef)` | ✅ covered | MockRecordRef.ALCopyLinks no-op (no BC link service in standalone) |
| `CopyLinks` | `(Table)` | 🔶 not-tested |  |
| `CopyLinks` | `(Variant)` | 🔶 not-tested |  |
| `Count` | `()` | ✅ covered |  |
| `CountApprox` | `()` | ✅ covered |  |
| `CurrentCompany` | `()` | ✅ covered |  |
| `CurrentKey` | `()` | ✅ covered |  |
| `CurrentKeyIndex` | `(Integer)` | ✅ covered | getter + setter — setting re-sorts iteration by the Nth declared key (1-based). Invalid index throws. All keys parsed and registered via TableFieldRegistry. Issue #1218. |
| `Delete` | `(Boolean)` | ✅ covered |  |
| `DeleteAll` | `(Boolean)` | ✅ covered |  |
| `DeleteLink` | `(Integer)` | ✅ covered |  |
| `DeleteLinks` | `()` | ✅ covered |  |
| `Duplicate` | `()` | ✅ covered |  |
| `Field` | `(Integer)` | ✅ covered |  |
| `Field` | `(Text)` | 🔶 not-tested |  |
| `FieldCount` | `()` | ✅ covered |  |
| `FieldExist` | `(Integer)` | ✅ covered | . ALFieldExists now checks TableFieldRegistry so it reports true for metadata-registered fields even with no data rows. Known/unknown/second-field tested. |
| `FieldExist` | `(Text)` | 🔲 gap |  |
| `FieldIndex` | `(Integer)` | ✅ covered | uses TableFieldRegistry.GetFieldIds for ordinal-to-field-number mapping so Name/Caption are correct on the returned FieldRef |
| `FilterGroup` | `(Integer)` | ✅ covered | . ALFilterGroup property/method on MockRecordRef delegates to MockRecordHandle — no-op standalone, reads return 0. |
| `Find` | `(Text)` | ✅ covered |  |
| `FindFirst` | `()` | ✅ covered |  |
| `FindLast` | `()` | ✅ covered |  |
| `FindSet` | `(Boolean, Boolean)` | 🔶 not-tested |  |
| `FindSet` | `(Boolean)` | ✅ covered |  |
| `FullyQualifiedName` | `()` | 🔲 gap |  |
| `Get` | `(RecordId)` | ✅ covered |  |
| `GetBySystemId` | `(Guid)` | ✅ covered |  |
| `GetFilters` | `()` | ✅ covered |  |
| `GetPosition` | `(Boolean)` | ✅ covered | GetPosition(Boolean) stub ignores the useNames flag |
| `GetTable` | `(Table)` | ✅ covered |  |
| `GetView` | `(Boolean)` | ✅ covered | ALGetView() and ALGetView(bool useNames) — useNames flag ignored in standalone |
| `HasFilter` | `()` | ✅ covered |  |
| `HasLinks` | `()` | ✅ covered |  |
| `Init` | `()` | ✅ covered |  |
| `Insert` | `()` | ✅ covered |  |
| `Insert` | `(Boolean, Boolean)` | 🔶 not-tested |  |
| `Insert` | `(Boolean)` | 🔶 not-tested |  |
| `IsDirty` | `()` | ✅ covered | MockRecordRef.ALIsDirty always false (no dirty tracking in standalone) |
| `IsEmpty` | `()` | ✅ covered |  |
| `IsTemporary` | `()` | ✅ covered |  |
| `KeyCount` | `()` | ✅ covered | returns number of declared keys (PK + secondaries) from TableFieldRegistry. Issue #1218. |
| `KeyIndex` | `(Integer)` | ✅ covered | returns MockKeyRef for the Nth declared key (1-based). Out-of-range index throws. Issue #1218. |
| `LoadFields` | `(Integer)` | ✅ covered | MockRecordRef.ALLoadFields no-op (deprecated alias for SetLoadFields) |
| `LockTable` | `(Boolean, Boolean)` | ✅ covered |  |
| `Mark` | `(Boolean)` | ✅ covered |  |
| `MarkedOnly` | `(Boolean)` | ✅ covered |  |
| `Modify` | `(Boolean)` | ✅ covered |  |
| `Name` | `()` | ✅ covered |  |
| `Next` | `(Integer)` | ✅ covered |  |
| `Number` | `()` | ✅ covered |  |
| `Open` | `(Integer, Boolean, Text)` | ✅ covered |  |
| `Open` | `(Text, Boolean, Text)` | 🔶 not-tested |  |
| `ReadConsistency` | `()` | ✅ covered | MockRecordRef.ALReadConsistency always false (no SQL in standalone) |
| `ReadIsolation` | `(IsolationLevel)` | ✅ covered |  |
| `ReadPermission` | `()` | ✅ covered | MockRecordRef.ALReadPermission always returns true in standalone (no permission enforcement) |
| `RecordId` | `()` | ✅ covered |  |
| `RecordLevelLocking` | `()` | ✅ covered | . Standalone: always true (row-level locking, no SQL table hints). Property on MockRecordRef. |
| `Rename` | `(Joker, Joker)` | ✅ covered |  |
| `Reset` | `()` | ✅ covered |  |
| `SecurityFiltering` | `(SecurityFilter)` | ✅ covered | MockRecordRef.ALSecurityFiltering get/set property stub |
| `SetAutoCalcFields` | `(Integer)` | ✅ covered | MockRecordRef.ALSetAutoCalcFields(params int[]) and ALSetAutoCalcFields(DataError, params int[]); no-op in standalone (all fields always available in memory). Fixed CS1061 in issue #1326. |
| `SetLoadFields` | `(Integer)` | ✅ covered |  |
| `SetPermissionFilter` | `()` | ✅ covered |  |
| `SetPosition` | `(Text)` | ✅ covered |  |
| `SetRecFilter` | `()` | ✅ covered |  |
| `SetTable` | `(Table, Boolean)` | 🔶 not-tested |  |
| `SetTable` | `(Table)` | ✅ covered |  |
| `SetView` | `(Text)` | ✅ covered |  |
| `SystemCreatedAtNo` | `()` | ✅ covered |  |
| `SystemCreatedByNo` | `()` | ✅ covered |  |
| `SystemIdNo` | `()` | ✅ covered |  |
| `SystemModifiedAtNo` | `()` | ✅ covered |  |
| `SystemModifiedByNo` | `()` | ✅ covered |  |
| `Truncate` | `(Boolean)` | ✅ covered | MockRecordRef.ALTruncate delegates to ALDeleteAll without triggers |
| `WritePermission` | `()` | ✅ covered |  |

## Report  (17/21)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `DefaultLayout` | `(Integer)` | ✅ covered | NavReport.DefaultLayout → MockReportHandle.StaticDefaultLayout (returns 0). |
| `ExcelLayout` | `(Integer, InStream)` | ✅ covered | NavReport.ExcelLayout → MockReportHandle.StaticExcelLayout (returns 0). |
| `Execute` | `(Integer, Text, RecordRef)` | ✅ covered | StaticExecute (static) + instance Execute(xmlText) — both no-op in standalone. |
| `Execute` | `(Text, Text, RecordRef)` | 🔲 gap |  |
| `GetSubstituteReportId` | `(Integer)` | ✅ covered | NavReport.GetSubstituteReportId → MockReportHandle.StaticGetSubstituteReportId (returns input id). |
| `Print` | `(Integer, Text, Text, RecordRef)` | ✅ covered | NavReport.Print → MockReportHandle.StaticPrint (no-op). |
| `RdlcLayout` | `(Integer, InStream)` | ✅ covered | NavReport.RdlcLayout → MockReportHandle.StaticRdlcLayout (returns 0). |
| `Run` | `(Integer, Boolean, Boolean, Table)` | ✅ covered | "Report.Run(ReportId, RequestPage, SystemPrinter) — 3-arg overload. BC emits this when no record is passed. Fixes CS7036 'systemPrinter' missing argument — issue #1336." |
| `Run` | `(Text, Boolean, Boolean, Table)` | 🔲 gap |  |
| `RunModal` | `(Integer, Boolean, Boolean, Table)` | ✅ covered | Rep.RunRequestPage(requestParameters) instance form — BC emits this when a Report variable calls RunRequestPage with one Text argument (e.g. SuggestVendorPayments.RunRequestPage(OldParameters)). Returns empty string in standalone mode. Fixes #1333. |
| `RunModal` | `(Text, Boolean, Boolean, Table)` | 🔲 gap |  |
| `RunRequestPage` | `(Integer, Text)` | 🔲 gap |  |
| `SaveAs` | `(Integer, Text, ReportFormat, OutStream, RecordRef)` | ✅ covered | (path-only, OutStream, OutStream+RecordRef); NavReport.SaveAs → MockReportHandle.StaticSaveAs (no-op). Fixes #1088. |
| `SaveAsExcel` | `(Integer, Text, Table)` | ✅ covered | NavReport.SaveAsExcel → MockReportHandle.StaticSaveAsExcel (no-op). |
| `SaveAsHtml` | `(Integer, Text, Table)` | ✅ covered | NavReport.SaveAsHtml → MockReportHandle.StaticSaveAsHtml (no-op). |
| `SaveAsPdf` | `(Integer, Text, Table)` | ✅ covered | NavReport.SaveAsPdf → MockReportHandle.StaticSaveAsPdf (no-op). |
| `SaveAsWord` | `(Integer, Text, Table)` | ✅ covered | NavReport.SaveAsWord → MockReportHandle.StaticSaveAsWord (no-op). |
| `SaveAsXml` | `(Integer, Text, Table)` | ✅ covered | NavReport.SaveAsXml → MockReportHandle.StaticSaveAsXml (no-op). |
| `ValidateAndPrepareLayout` | `(Integer, InStream, InStream, ReportLayoutType)` | ✅ covered | NavReport.ValidateAndPrepareLayout → MockReportHandle.StaticValidateAndPrepareLayout (no-op). |
| `WordLayout` | `(Integer, InStream)` | ✅ covered | NavReport.WordLayout → MockReportHandle.StaticWordLayout (returns 0). |
| `WordXmlPart` | `(Integer, Boolean)` | ✅ covered | NavReport.WordXmlPart → MockReportHandle.StaticWordXmlPart (returns empty string). |

## ReportInstance  (36/38)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Break` | `()` | ✅ covered | Injected as no-op method into stripped report class by RoslynRewriter. |
| `CreateTotals` | `(Array)` | ✅ covered | MockReportHandle.CreateTotals() (0-arg) and CreateTotals(params object[]) (N-arg) — both no-ops in standalone mode. |
| `CreateTotals` | `(Decimal, Decimal)` | 🔲 gap |  |
| `DefaultLayout` | `()` | ✅ covered | MockReportHandle.DefaultLayout() returns default NavDefaultLayout. |
| `ExcelLayout` | `(InStream)` | ✅ covered | MockReportHandle.ExcelLayout() returns false (no layout data). |
| `Execute` | `(Text, RecordRef)` | ✅ covered | MockReportHandle.StaticExecute(id, requestPage) — no-op in standalone mode. |
| `FormatRegion` | `(Text)` | ✅ covered | MockReportHandle.FormatRegion property (get/set). |
| `IsReadOnly` | `()` | ✅ covered | MockReportHandle.ALIsReadOnly always returns false. |
| `Language` | `(Integer)` | ✅ covered | MockReportHandle.Language property (get/set). |
| `NewPage` | `()` | ✅ covered | Deprecated in BC; BC compiler compiles CurrReport.NewPage() to a blank statement — no injection needed. |
| `NewPagePerRecord` | `(Boolean)` | ✅ covered | Both CurrReport.NewPagePerRecord (in trigger) and Rep.NewPagePerRecord (instance setter) compile to blank statements — no injection needed. |
| `ObjectId` | `(Boolean)` | ✅ covered | > |
| `PageNo` | `(Integer)` | ✅ covered | Injected as public int PageNo() => 0 into stripped report class by RoslynRewriter. |
| `PaperSource` | `(Integer, Integer)` | ✅ covered | Deprecated in BC; BC compiler compiles CurrReport.PaperSource() to a blank statement — no injection needed. |
| `Preview` | `()` | ✅ covered | BC emits CurrReport.Preview (bool property) and CurrReport.PreviewCanPrint (bool property) on the report class — both return false in standalone mode (no print-preview UI); injected as CurrReport stubs in RoslynRewriter.cs |
| `Print` | `(Text, Text, RecordRef)` | ✅ covered | MockReportHandle.Print(requestPageXml) instance method — no-op in standalone mode. |
| `PrintOnlyIfDetail` | `(Boolean)` | ✅ covered | Injected as bool property into stripped report class by RoslynRewriter. |
| `Quit` | `()` | ✅ covered | Injected as no-op method into stripped report class by RoslynRewriter. |
| `RDLCLayout` | `(InStream)` | ✅ covered | MockReportHandle.RDLCLayout() returns false (no layout data). |
| `Run` | `()` | ✅ covered | MockReportHandle.Run() executes full report lifecycle. |
| `RunModal` | `()` | ✅ covered | MockReportHandle.RunModal() executes full report lifecycle. |
| `RunRequestPage` | `(Text)` | ✅ covered | MockReportHandle.RunRequestPage() returns placeholder text. |
| `SaveAs` | `(Text, ReportFormat, OutStream, RecordRef)` | ✅ covered | MockReportHandle.SaveAs(errorLevel, requestParams, format, outStream) — no-op in standalone mode. |
| `SaveAsExcel` | `(Text)` | ✅ covered | MockReportHandle.SaveAsExcel() is a no-op in standalone mode. |
| `SaveAsHtml` | `(Text)` | ✅ covered | MockReportHandle.SaveAsHtml() is a no-op in standalone mode. |
| `SaveAsPdf` | `(Text)` | ✅ covered | MockReportHandle.SaveAsPdf() is a no-op in standalone mode. |
| `SaveAsWord` | `(Text)` | ✅ covered | MockReportHandle.SaveAsWord() is a no-op in standalone mode. |
| `SaveAsXml` | `(Text)` | ✅ covered | MockReportHandle.SaveAsXml() is a no-op in standalone mode. |
| `SetTableView` | `(Table)` | ✅ covered | MockReportHandle.SetTableView() copies table view to report's Rec. |
| `ShowOutput` | `()` | ✅ covered | Deprecated in BC; BC compiler compiles CurrReport.ShowOutput to default(bool) = false — no injection needed. |
| `ShowOutput` | `(Boolean)` | 🔲 gap |  |
| `Skip` | `()` | ✅ covered | Injected as no-op method into stripped report class by RoslynRewriter. |
| `TargetFormat` | `()` | ✅ covered | MockReportHandle.ALTargetFormat returns default NavReportFormat. |
| `TotalsCausedBy` | `()` | ✅ covered | Deprecated in BC; BC compiler compiles CurrReport.TotalsCausedBy to default(int) = 0 — no injection needed. |
| `UseRequestPage` | `(Boolean)` | ✅ covered | MockReportHandle.UseRequestForm property (get/set). |
| `ValidateAndPrepareLayout` | `(InStream, InStream, ReportLayoutType)` | ✅ covered | MockReportHandle.StaticValidateAndPrepareLayout(errorLevel, id, inStreamIn, ByRef<inStreamOut>, layoutType) — no-op in standalone mode. |
| `WordLayout` | `(InStream)` | ✅ covered | MockReportHandle.WordLayout() returns false (no layout data). |
| `WordXmlPart` | `(Boolean)` | ✅ covered | MockReportHandle.WordXmlPart() returns empty string. |

## RequestPage  (9/9)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Activate` | `(Boolean)` | ✅ covered | no-op stub on MockCurrPage (page extension CurrPage); compilation-tested in 91-requestpage-currpage |
| `Caption` | `(Text)` | ✅ covered | get/set NavText property on MockCurrPage; compilation-tested in 91-requestpage-currpage |
| `Close` | `()` | ✅ covered | no-op stub on MockCurrPage; compilation-tested in 91-requestpage-currpage |
| `Editable` | `(Boolean)` | ✅ covered | bool get/set on MockCurrPage; existing coverage in 38-page-ext-currpage |
| `LookupMode` | `(Boolean)` | ✅ covered | bool get/set on MockCurrPage; compilation-tested in 91-requestpage-currpage |
| `ObjectId` | `(Boolean)` | ✅ covered | returns NavText.Empty in standalone mode; compilation-tested in 91-requestpage-currpage |
| `SaveRecord` | `()` | ✅ covered | no-op stub on MockCurrPage; compilation-tested in 91-requestpage-currpage |
| `SetSelectionFilter` | `(Table)` | ✅ covered | no-op stub on MockCurrPage; compilation-tested in 91-requestpage-currpage |
| `Update` | `(Boolean)` | ✅ covered | no-op stub on MockCurrPage; existing coverage in 38-page-ext-currpage |

## SecretText  (3/3)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `IsEmpty` | `()` | ✅ covered |  |
| `SecretStrSubstNo` | `(Text, SecretText)` | ✅ covered | intercepted via ALSystemString.ALSecretStrSubstNo → AlCompat.SecretStrSubstNo |
| `Unwrap` | `()` | ✅ covered | NavSecretText.ALUnwrap() works without NavSession (proved by test) |

## Session  (17/22)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `ApplicationArea` | `(Text)` | ✅ covered | . Rewriter redirects ALSession.ALApplicationArea to AlCompat.ApplicationArea → empty string. |
| `ApplicationIdentifier` | `()` | ✅ covered | . Rewriter replaces ALSession.ALApplicationIdentifier with "" (no BC app context standalone). |
| `BindSubscription` | `(Codeunit)` | ✅ covered |  |
| `CurrentClientType` | `()` | ✅ covered | . Rewriter replaces ALSession.ALCurrentClientType with NavClientType.Background (no NavSession standalone). |
| `CurrentExecutionMode` | `()` | ✅ covered | . Rewriter replaces ALSession.ALGetCurrentExecutionMode with ExecutionMode.Standard. |
| `DefaultClientType` | `()` | ✅ covered | . Rewriter replaces ALSession.ALDefaultClientType with NavClientType.Background. |
| `EnableVerboseTelemetry` | `(Boolean, Duration)` | ✅ covered | . Stripped via StripEntireCallMethods — no telemetry config standalone. |
| `GetCurrentModuleExecutionContext` | `()` | ✅ covered | . Rewriter redirects to AlCompat.GetExecutionContext → Normal. |
| `GetExecutionContext` | `()` | ✅ covered | . Rewriter redirects to AlCompat.GetExecutionContext → Normal. |
| `GetModuleExecutionContext` | `(Guid)` | ✅ covered | . Rewriter redirects to AlCompat.GetExecutionContext → Normal. |
| `IsSessionActive` | `(Integer)` | ✅ covered |  |
| `LogAuditMessage` | `(Text, SecurityOperationResult, AuditCategory, Integer, Integer, Dictionary)` | ✅ covered | . Covered via BC native — telemetry is silently dropped in standalone mode. |
| `LogMessage` | `(Text, Text, Verbosity, DataClassification, TelemetryScope, Dictionary)` | ✅ covered | . Covered via BC native — telemetry is silently dropped in standalone mode. |
| `LogMessage` | `(Text, Text, Verbosity, DataClassification, TelemetryScope, Text, Text, Text, Text)` | 🔲 gap |  |
| `LogSecurityAudit` | `(Text, SecurityOperationResult, Text, AuditCategory, Array, Array)` | ✅ covered | . Stripped via StripEntireCallMethods — needs OpenTelemetry.Audit.Geneva DLL that's missing standalone. |
| `SendTraceTag` | `(Text, Text, Verbosity, Text, DataClassification)` | ✅ covered | . Stripped via StripEntireCallMethods — deprecated telemetry; no-op standalone. |
| `SetDocumentServiceToken` | `(Text)` | ❓ stub | . Stripped via StripEntireCallMethods — OneDrive integration; no-op standalone. No explicit test (no return value to assert). |
| `StartSession` | `(Integer, Integer, Duration, Text, Table)` | ❌ not-possible |  |
| `StartSession` | `(Integer, Integer, Text, Table, Duration)` | ❌ not-possible |  |
| `StartSession` | `(Integer, Integer, Text, Table)` | ❌ not-possible |  |
| `StopSession` | `(Integer, Text)` | ✅ covered |  |
| `UnbindSubscription` | `(Codeunit)` | ✅ covered |  |

## SessionInformation  (4/4)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AITokensUsed` | `()` | ✅ covered | . Rewriter replaces ALSessionInformation.ALAITokensUsed with 0L. Standalone: no AI calls, so always 0. |
| `Callstack` | `()` | ✅ covered | . Rewriter replaces ALSessionInformation.GetALCallstack(session) with "". Standalone: no call stack to report. |
| `SqlRowsRead` | `()` | ✅ covered | . Rewriter replaces ALSessionInformation.ALSqlRowsRead with 0L. Standalone: no SQL. |
| `SqlStatementsExecuted` | `()` | ✅ covered | . Rewriter replaces ALSessionInformation.ALSqlStatementsExecuted with 0L. Standalone: no SQL. |

## SessionSettings  (9/9)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Company` | `(Text)` | ✅ covered | . MockSessionSettings holds setting in-memory; setter + getter round-trip tested. |
| `Init` | `()` | ✅ covered | . NavSessionSettings is rewritten to MockSessionSettings; ALInit populates defaults and never dereferences NavSession. |
| `LanguageId` | `(Integer)` | ✅ covered | . Integer setter + getter round-trip on MockSessionSettings. |
| `LocaleId` | `(Integer)` | ✅ covered | . Integer setter + getter round-trip on MockSessionSettings. |
| `ProfileAppId` | `(Guid)` | ✅ covered | . NavGuid setter + getter round-trip; defaults to empty GUID. |
| `ProfileId` | `(Text)` | ✅ covered | . Text setter + getter round-trip on MockSessionSettings. |
| `ProfileSystemScope` | `(Boolean)` | ✅ covered | . Boolean setter + getter round-trip; defaults to false on MockSessionSettings. |
| `RequestSessionUpdate` | `(Boolean)` | ✅ covered | (with/without reloadUserProfile flag). Standalone no-op — no service-tier session to refresh. Preserves local state. |
| `TimeZone` | `(Text)` | ✅ covered | . Text setter + getter round-trip on MockSessionSettings. |

## System  (68/79)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Abs` | `(Decimal)` | ✅ covered | . Covered via ALSystemNumeric.ALAbs (integer + decimal, positive/negative/zero). |
| `ApplicationPath` | `()` | ✅ covered | MockSystemOperatingSystem.ALApplicationPath → AppContext.BaseDirectory |
| `ArrayLen` | `(Array, Integer)` | ✅ covered | MockArray stores dimensions; 1-arg and 2-arg forms tested |
| `CalcDate` | `(DateFormula, Date)` | ✅ covered | . Routed through AlCompat.CalcDate which tries BC's ALCalcDate first and falls back to .NET date arithmetic when NavNCLDateInvalidException is thrown (issue #1258, Windows with null session). |
| `CalcDate` | `(Text, Date)` | 🔲 gap |  |
| `CanLoadType` | `(DotNet)` | ❌ not-possible | requires DotNet type parameter — unavailable in standalone mode; no DotNet type resolution without BC service tier |
| `CaptionClassTranslate` | `(Text)` | ✅ covered |  |
| `Clear` | `(Array)` | ✅ covered | . Covered: Text, Integer, Decimal, Boolean, Date, Record, List — each resets to type default. |
| `Clear` | `(Joker)` | 🔲 gap |  |
| `Clear` | `(SecretText)` | 🔲 gap |  |
| `ClearAll` | `()` | ✅ covered | . Covered: resets all codeunit globals on the calling codeunit via BC-emitted OnClear. |
| `ClearCollectedErrors` | `()` | ✅ covered | AlScope.ClearCollectedErrors(); tested in 176-system-error-utils |
| `ClearLastError` | `()` | ✅ covered | rewrites to AlScope.LastErrorText = ""; tested in 176-system-error-utils |
| `ClosingDate` | `(Date)` | ✅ covered | AlCompat.ClosingDate wraps ALSystemDate.ALClosingDate; tested in 177-system-enc-date |
| `CodeCoverageInclude` | `(Table)` | ✅ covered | ALCodeCoverageInclude → stripped (no-op) via StripEntireCallMethods in RoslynRewriter |
| `CodeCoverageLoad` | `()` | ✅ covered | ALCodeCoverageLoadFromTable → stripped (no-op) via StripEntireCallMethods in RoslynRewriter |
| `CodeCoverageLog` | `(Boolean, Boolean)` | ✅ covered | ALCodeCoverageLog → stripped (no-op) via StripEntireCallMethods in RoslynRewriter |
| `CodeCoverageRefresh` | `()` | ✅ covered | ALCodeCoverageRefreshTable → stripped (no-op) via StripEntireCallMethods in RoslynRewriter |
| `CompressArray` | `(Array)` | ✅ covered | . ALSystemArray.ALCompressArray redirected to AlCompat.ALCompressArray; shifts non-blank elements to front, fills tail with default. |
| `CopyArray` | `(Array, Array, Integer, Integer)` | ✅ covered | . ALSystemArray.ALCopyArray redirected to AlCompat.ALCopyArray; 4-arg overload copies count elements from 1-based fromIndex; 3-arg overload (no count) copies all remaining elements from fromIndex to end. Fixed CS0411/NullRef for page-level array[N] of Text[M] vars: rewriter now preserves a clean InitializeComponent for page classes (strips BC-only calls, keeps field inits) so MockFormHandle.Invoke can initialise MockArray fields (issue #1232). |
| `CopyStream` | `(OutStream, InStream, Integer)` | ✅ covered | . ALSystemVariable.ALCopyStream redirected to MockStream.ALCopyStream. |
| `CreateDateTime` | `(Date, Time)` | ✅ covered |  |
| `CreateEncryptionKey` | `()` | ✅ covered | no-op stub in standalone runner; tested in 177-system-enc-date |
| `CreateGuid` | `()` | ✅ covered | . ALDatabase.ALCreateGuid redirected to AlCompat.ALCreateGuid; returns new NavGuid(Guid.NewGuid()). |
| `CurrentDateTime` | `()` | ✅ covered | BC native returns DateTime.Now, works standalone. |
| `Date2DMY` | `(Date, Integer)` | ✅ covered | day=index 1, month=index 2, year=index 3 |
| `Date2DWY` | `(Date, Integer)` | ✅ covered | day-of-week=index 1, week-no=index 2 |
| `DaTi2Variant` | `(Date, Time)` | ✅ covered | AlCompat.DaTi2Variant returns MockVariant(NavDateTime); tested in 177-system-enc-date |
| `Decrypt` | `(Text)` | ✅ covered | stub returns plaintext unchanged (no key in runner); tested in 177-system-enc-date |
| `DeleteEncryptionKey` | `()` | ✅ covered | no-op stub; tested in 177-system-enc-date |
| `DMY2Date` | `(Integer, Integer, Integer)` | ✅ covered | AlCompat.DMY2Date wraps ALSystemDate.ALDMY2Date; tested in 177-system-enc-date |
| `DT2Date` | `(DateTime)` | ✅ covered |  |
| `DT2Time` | `(DateTime)` | ✅ covered | tested in 61-datetime-decomposition and 62-time-decomposition |
| `DWY2Date` | `(Integer, Integer, Integer)` | ✅ covered | AlCompat.DWY2Date wraps ALSystemDate.ALDWY2Date; tested in 177-system-enc-date |
| `Encrypt` | `(Text)` | ✅ covered | stub returns plaintext unchanged; tested in 177-system-enc-date |
| `EncryptionEnabled` | `()` | ✅ covered | always false in standalone runner; tested in 177-system-enc-date |
| `EncryptionKeyExists` | `()` | ✅ covered | always false in standalone runner; tested in 177-system-enc-date |
| `Evaluate` | `(Joker, Text, Integer)` | ✅ covered | supports Integer, Boolean, Decimal, Text, BigInteger, Date |
| `ExportEncryptionKey` | `(Text)` | ✅ covered | no-op stub; tested in 177-system-enc-date |
| `ExportObjects` | `(Text, Table, Integer)` | ✅ covered | ALExportObjects → stripped (no-op) via StripEntireCallMethods in RoslynRewriter; object export requires BC runtime |
| `Format` | `(Joker, Integer, Integer)` | ✅ covered | 1-arg AlCompat.Format; 2-arg Format(value, formatNumber); 3-arg Format(value, length, formatString) with AL mask tokens; tested with decimal + mask |
| `Format` | `(Joker, Integer, Text)` | 🔲 gap |  |
| `GetCollectedErrors` | `(Boolean)` | ✅ covered | zero-arg AlScope.GetCollectedErrors(); tested in 176-system-error-utils |
| `GetDocumentUrl` | `(Guid)` | ✅ covered | NavMedia.ALGetDocumentUrl → AlCompat.GetDocumentUrl; returns empty string stub in standalone mode |
| `GetDotNetType` | `(Joker)` | ❌ not-possible | requires DotNet type parameter — unavailable in standalone mode; no DotNet type resolution without BC service tier |
| `GetLastErrorCallStack` | `()` | ✅ covered | returns AlScope.LastErrorCallStack (always "" at the AL API surface — no runtime stack capture in-runner). Rewriter redirects ALSystemErrorHandling.ALGetLastErrorCallStack to AlScope.LastErrorCallStack. Note that the test runner's failure output renders AL-level frames (object+procedure+line) via FormatStackFrames/FormatSingleFrame — see AlRunner/Program.cs and AlRunner.Tests/AlScopeTrackingTests.cs; this is a test-output enhancement, not an AL-surface change. |
| `GetLastErrorCode` | `()` | ✅ covered | returns AlScope.LastErrorCode (always "" in runner); tested in 176-system-error-utils |
| `GetLastErrorObject` | `()` | ✅ covered | ALSystemErrorHandling.ALGetLastErrorObject → AlScope.GetLastErrorObject(); returns MockVariant(errorMessage) after asserterror, empty MockVariant after ClearLastError |
| `GetLastErrorText` | `()` | ✅ covered | GetUrl(ClientType, Company, ObjectType, ObjectId, Record, UseFilters) — full 6-arg overload with optional Record and UseFilters |
| `GetLastErrorText` | `(Boolean)` | 🔲 gap |  |
| `GetUrl` | `(ClientType, Text, ObjectType, Integer, RecordRef, Boolean, Text)` | 🔲 gap |  |
| `GetUrl` | `(ClientType, Text, ObjectType, Integer, RecordRef, Boolean)` | 🔲 gap |  |
| `GetUrl` | `(ClientType, Text, ObjectType, Integer, Table, Boolean, Text)` | 🔲 gap |  |
| `GetUrl` | `(ClientType, Text, ObjectType, Integer, Table, Boolean)` | 🔲 gap |  |
| `GlobalLanguage` | `(Integer)` | ✅ covered | (get/set); MockLanguage.ALGlobalLanguage in-memory store; default 1033 (ENU); reset between tests |
| `GuiAllowed` | `()` | ✅ covered | MockSystemOperatingSystem.ALGuiAllowed always returns false standalone |
| `HasCollectedErrors` | `()` | ✅ covered | AlScope.HasCollectedErrors; tested in 176-system-error-utils |
| `Hyperlink` | `(Text)` | ✅ covered | MockSystemOperatingSystem.ALHyperlink is a no-op in standalone mode |
| `ImportEncryptionKey` | `(Text, Text)` | ✅ covered | no-op stub; tested in 177-system-enc-date |
| `ImportObjects` | `(Text, Integer)` | ✅ covered | ALImportObjects → stripped (no-op) via StripEntireCallMethods in RoslynRewriter; object import requires BC runtime |
| `ImportStreamWithUrlAccess` | `(InStream, Text, Integer)` | ✅ covered | NavMedia.ALImportWithUrlAccess → AlCompat.ImportStreamWithUrlAccess; returns Guid.Empty stub (BC lowers return value as Guid→Text via ALCompiler.GuidToNavText) |
| `IsCollectingErrors` | `()` | ✅ covered | AlScope.IsCollectingErrors; tested in 176-system-error-utils |
| `IsNull` | `(DotNet)` | ✅ covered | RoslynRewriter intercepts NavIndirectValueToNavValue<NavDotNet>(...).IsNull → false; no real DotNet objects in standalone mode |
| `IsNullGuid` | `(Guid)` | ✅ covered |  |
| `IsServiceTier` | `()` | ✅ covered | RoslynRewriter intercepts NavEnvironment.IsServiceTier → false; no service tier in standalone mode |
| `NormalDate` | `(Date)` | ✅ covered | AlCompat.NormalDate wraps ALSystemDate.ALNormalDate; tested in 177-system-enc-date |
| `Power` | `(Decimal, Decimal)` | ✅ covered | . Covered via ALSystemNumeric.ALPower (integer exponent, fractional/sqrt, negative base, zero exponent). |
| `Random` | `(Integer)` | ✅ covered | . ALSystemNumeric.ALRandom redirected to AlCompat.ALRandom; returns thread-local System.Random value in [1, maxNumber]. |
| `Randomize` | `(Integer)` | ✅ covered | . ALSystemNumeric.ALRandomize redirected to AlCompat.ALRandomize; seeds thread-local System.Random. |
| `Round` | `(Decimal, Decimal, Text)` | ✅ covered | (1-arg/2-arg/3-arg). 1-arg form is redirected to AlCompat.ALRound because the BC SDK's 1-arg overload defaults precision to 0 (no rounding), while AL semantics round to nearest integer. |
| `RoundDateTime` | `(DateTime, BigInteger, Text)` | ✅ covered | AlCompat.RoundDateTime via BC native ALSystemDate.ALRoundDateTime; rounds to nearest interval boundary |
| `Sleep` | `(Integer)` | ✅ covered | no-op stub in MockSession.Sleep via RoslynRewriter NavSession.Sleep→MockSession.Sleep rewrite; tests in tests/bucket-1/codeunit-runtime/153-sleep |
| `TemporaryPath` | `()` | ✅ covered | MockSystemOperatingSystem.ALTemporaryPath → Path.GetTempPath() |
| `Time` | `()` | ✅ covered |  |
| `Today` | `()` | ✅ covered |  |
| `Variant2Date` | `(Variant)` | ✅ covered | AlCompat.Variant2Date unwraps MockVariant; tested in 177-system-enc-date |
| `Variant2Time` | `(Variant)` | ✅ covered | AlCompat.Variant2Time unwraps MockVariant; tested in 177-system-enc-date |
| `WindowsLanguage` | `()` | ✅ covered | MockLanguage.ALWindowsLanguage → CultureInfo.CurrentCulture.LCID |
| `WorkDate` | `(Date)` | ✅ covered | (get/set); AlScope.GetWorkDate/SetWorkDate in-memory store; reset to NavDate.Default between tests |

## Table  (110/110)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AddLink` | `(Text, Text)` | ✅ covered | (url; url+description); returns unique integer link ID |
| `AddLoadFields` | `(Joker)` | ✅ covered | (fields, DataError+fields). Standalone no-op — all fields are always loaded in memory. |
| `AreFieldsLoaded` | `(Joker)` | ✅ covered | (fields, DataError+fields). Standalone: always returns true (every field is always loaded). |
| `Ascending` | `(Boolean)` | ✅ covered | ALAscending() getter + ALAscending(bool) setter on MockRecordHandle |
| `CalcFields` | `(Joker, Joker)` | ✅ covered | evaluates Sum/Count/Exist/Lookup FlowField formulas against in-memory tables via CalcFormulaRegistry; multiple fields in one call supported |
| `CalcSums` | `(Joker, Joker)` | ✅ covered | sums filtered records and writes result back into the record fields |
| `ChangeCompany` | `(Text)` | ✅ covered | . No-op in standalone mode (single in-memory company); returns true. |
| `ClearMarks` | `()` | ✅ covered |  |
| `Consistent` | `(Boolean)` | ✅ covered | . No-op in standalone mode (no transaction consistency). |
| `Copy` | `(Table, Boolean)` | ✅ covered | (ShareTable bool, default false) |
| `CopyFilter` | `(Joker, Joker)` | ✅ covered | copies filter from one field to a field on another record |
| `CopyFilters` | `(Table)` | ✅ covered |  |
| `CopyLinks` | `(RecordRef)` | ✅ covered | (Record; RecordRef); copies all links from source into target |
| `CopyLinks` | `(Table)` | ✅ covered | MockRecordHandle.ALCopyLinks(source) — copies all links from source record; already implemented and tested |
| `Count` | `()` | ✅ covered |  |
| `CountApprox` | `()` | ✅ covered | returns exact count (ALCountApprox = ALCount) in runner context |
| `CurrentCompany` | `()` | ✅ covered | > |
| `CurrentKey` | `()` | ✅ covered | MockRecordHandle.ALCurrentKey — returns comma-separated field names from current sort key |
| `Delete` | `(Boolean)` | ✅ covered | ALDelete(DataError, bool runTrigger) removes record from in-memory store; triggers OnDelete when runTrigger=true. |
| `DeleteAll` | `(Boolean)` | ✅ covered |  |
| `DeleteLink` | `(Integer)` | ✅ covered | removes link by ID, preserves others |
| `DeleteLinks` | `()` | ✅ covered | removes all links from the record |
| `FieldActive` | `(Joker)` | ✅ covered | MockRecordHandle.ALFieldActive(fieldNo) — always true in standalone (no field disabling) |
| `FieldCaption` | `(Joker)` | ✅ covered | MockRecordHandle.ALFieldCaption(fieldNo) — from TableFieldRegistry; falls back to "FieldNN" |
| `FieldError` | `(Joker, ErrorInfo)` | ✅ covered | MockRecordHandle.ALFieldError(fieldNo) / ALFieldError(fieldNo, msg) — throws validation error; delegated on Record classes |
| `FieldError` | `(Joker, Text)` | ✅ covered | MockRecordHandle.ALFieldError(fieldNo, message) — raises field error with custom message; tested in 180-table-metadata-stubs |
| `FieldName` | `(Joker)` | ✅ covered | MockRecordHandle.ALFieldName(fieldNo) — from TableFieldRegistry; falls back to "FieldNN" |
| `FieldNo` | `(Joker)` | ✅ covered | ALFieldNo(string) falls back to TableFieldRegistry; suite 63-record-fieldno |
| `FilterGroup` | `(Integer)` | ✅ covered | no-op stub in standalone mode (filter groups not isolated) |
| `Find` | `(Text)` | ✅ covered | > |
| `FindFirst` | `()` | ✅ covered | positions to first matching record in current key order |
| `FindLast` | `()` | ✅ covered | positions to last matching record in current key order |
| `FindSet` | `(Boolean, Boolean)` | ✅ covered | MockRecordHandle.ALFindSet(DataError, forUpdate, forceNewQuery) — delegates to ALFindSet(DataError, forUpdate); tested in 309200-table-overloads |
| `FindSet` | `(Boolean)` | ✅ covered | > |
| `FullyQualifiedName` | `()` | ✅ covered | MockRecordHandle.ALFullyQualifiedName — returns CompanyName$TableName; implemented in #1373 |
| `Get` | `(Joker)` | ✅ covered | ALGet(DataError, params NavValue[]) plus object catch-all overloads for 1–4 keys (issue #1260, NavComplexValue→object rewrite). |
| `GetAscending` | `(Joker)` | ✅ covered | . Returns true by default (ascending); reflects SetAscending calls. |
| `GetBySystemId` | `(Guid)` | ✅ covered | . Finds record by SystemId field value. |
| `GetFilter` | `(Joker)` | ✅ covered | returns filter expression for a specific field |
| `GetFilters` | `()` | ✅ covered |  |
| `GetPosition` | `(Boolean)` | ✅ covered | GetPosition() and GetPosition(UseNames: Boolean) both supported |
| `GetRangeMax` | `(Joker)` | ✅ covered | "overloads=1; field types covered: Integer, Decimal, Date, Text, Code, Boolean (ALCompiler.NavValueToNavValue<T> rewriter fix for Date/Text/Code; ToBoolean fix for Boolean)" |
| `GetRangeMin` | `(Joker)` | ✅ covered | "overloads=1; field types covered: Integer, Decimal, Date, Text, Code, Boolean (ALCompiler.NavValueToNavValue<T> rewriter fix for Date/Text/Code; ToBoolean fix for Boolean)" |
| `GetView` | `(Boolean)` | ✅ covered | serialises SORTING+WHERE into a roundtrippable view string |
| `HasFilter` | `()` | ✅ covered |  |
| `HasLinks` | `()` | ✅ covered | returns true when at least one link exists on the record |
| `Init` | `()` | ✅ covered | clears non-PK fields to defaults, preserves PK, applies InitValue |
| `Insert` | `()` | ✅ covered | ALInsert(DataError), ALInsert(DataError, runTrigger), ALInsert(DataError, runTrigger, checkMandatoryFields); CheckMandatoryFields not enforced in standalone mode. |
| `Insert` | `(Boolean, Boolean)` | ✅ covered | MockRecordHandle.ALInsert(DataError, runTrigger, checkMandatoryFields) — 3-arg overload; tested in 309200-table-overloads |
| `Insert` | `(Boolean)` | ✅ covered | MockRecordHandle.ALInsert(DataError, runTrigger) — trigger-aware insert; tested in 309200-table-overloads |
| `IsEmpty` | `()` | ✅ covered |  |
| `IsTemporary` | `()` | ✅ covered | MockRecordHandle.ALIsTemporary — reflects _isTemporary flag set at construction |
| `LoadFields` | `(Joker)` | ✅ covered | . No-op in standalone mode (all fields always loaded). |
| `LockTable` | `(Boolean, Boolean)` | ✅ covered | (no-arg; Wait:Boolean) |
| `Mark` | `(Boolean)` | ✅ covered |  |
| `MarkedOnly` | `(Boolean)` | ✅ covered |  |
| `Modify` | `(Boolean)` | ✅ covered | ALModify(DataError) and ALModify(DataError, bool runTrigger) update record in in-memory store; triggers OnModify when runTrigger=true. |
| `ModifyAll` | `(Joker, Joker, Boolean)` | ✅ covered | ALModifyAllSafe(fieldNo, NavType, value[, runTrigger]) plus DataError-prefixed overloads for each (issue #1267), plus object catch-all overloads (issue #1260). |
| `Next` | `(Integer)` | ✅ covered |  |
| `ReadConsistency` | `()` | ✅ covered | . Always false in standalone mode (no SQL isolation). |
| `ReadIsolation` | `(IsolationLevel)` | ✅ covered | . No-op property stub in standalone mode. |
| `ReadPermission` | `()` | ✅ covered | MockRecordHandle.ALReadPermission — always true (no permission system in standalone) |
| `RecordId` | `()` | ✅ covered | > |
| `RecordLevelLocking` | `()` | ✅ covered | MockRecordHandle.ALRecordLevelLocking — always false (no SQL locking in standalone) |
| `Relation` | `(Joker)` | ✅ covered | . Returns 0 (no relational metadata in standalone mode). |
| `Rename` | `(Joker, Joker)` | ✅ covered | ALRename(DataError, params NavValue[]) plus object catch-all overloads for 1–4 keys (issue #1260). |
| `Reset` | `()` | ✅ covered | ALReset() clears all filters, ranges, and current-key overrides. |
| `SecurityFiltering` | `(SecurityFilter)` | ✅ covered | . Get/set property stub; stored but not enforced in standalone mode. |
| `SetAscending` | `(Joker, Boolean)` | ✅ covered | ascending/descending per key field, composite key, Reset clears direction |
| `SetAutoCalcFields` | `(Joker)` | ✅ covered |  |
| `SetBaseLoadFields` | `()` | ✅ covered | . No-op in standalone mode (all fields always loaded). |
| `SetCurrentKey` | `(Joker, Joker)` | ✅ covered |  |
| `SetFilter` | `(Joker, Text, Joker)` | ✅ covered | ALSetFilter(fieldNo, expr, args), ALSetFilter(fieldNo, NavType, expr, args), plus DataError-prefixed overloads for each (issue #1267), plus object catch-all overloads for 1–2 args (issue #1260). |
| `SetLoadFields` | `(Joker)` | ✅ covered | . No-op in standalone mode (all fields always loaded). |
| `SetPermissionFilter` | `()` | ✅ covered | . No-op in standalone mode (no permission enforcement). |
| `SetPosition` | `(Text)` | ✅ covered |  |
| `SetRange` | `(Joker, Joker, Joker)` | ✅ covered | ALSetRange/ALSetRangeSafe (clear/single/range) plus DataError-prefixed overloads for each (issue #1267), plus object catch-all overloads (issue #1260). |
| `SetRecFilter` | `()` | ✅ covered | (single-field and composite PK) |
| `SetView` | `(Text)` | ✅ covered | parses SORTING+WHERE view string and restores filters |
| `TableCaption` | `()` | ✅ covered | MockRecordHandle.ALTableCaption — from TableFieldRegistry; falls back to ALTableName |
| `TableName` | `()` | ✅ covered | MockRecordHandle.ALTableName — from TableFieldRegistry; falls back to "TableNN" |
| `TestField` | `(Joker, BigInteger, ErrorInfo)` | ✅ covered | routes through ALTestFieldSafe(object, NavALErrorInfo) catch-all — issue #1369 |
| `TestField` | `(Joker, BigInteger)` | ✅ covered | routes through ALTestFieldSafe(object) catch-all — issue #1369 |
| `TestField` | `(Joker, Boolean, ErrorInfo)` | ✅ covered | routes through ALTestFieldSafe(object, NavALErrorInfo) catch-all — issue #1369 |
| `TestField` | `(Joker, Boolean)` | ✅ covered | routes through ALTestFieldSafe(object) catch-all — issue #1369 |
| `TestField` | `(Joker, Code, ErrorInfo)` | ✅ covered | routes through ALTestFieldSafe(object, NavALErrorInfo) catch-all — issue #1369 |
| `TestField` | `(Joker, Code)` | ✅ covered | routes through ALTestFieldSafe(object) catch-all — issue #1369 |
| `TestField` | `(Joker, Decimal, ErrorInfo)` | ✅ covered | routes through ALTestFieldSafe(object, NavALErrorInfo) catch-all — issue #1369 |
| `TestField` | `(Joker, Decimal)` | ✅ covered | routes through ALTestFieldSafe(object) catch-all — issue #1369 |
| `TestField` | `(Joker, Enum, ErrorInfo)` | ✅ covered | routes through ALTestFieldSafe(object, NavALErrorInfo) catch-all — issue #1369 |
| `TestField` | `(Joker, Enum)` | ✅ covered | routes through ALTestFieldSafe(object) catch-all — issue #1369 |
| `TestField` | `(Joker, ErrorInfo)` | ✅ covered | routes through ALTestFieldSafe(NavALErrorInfo) — non-empty check with error context — issue #1369 |
| `TestField` | `(Joker, Guid, ErrorInfo)` | ✅ covered | routes through ALTestFieldSafe(object, NavALErrorInfo) catch-all — issue #1369 |
| `TestField` | `(Joker, Guid)` | ✅ covered | routes through ALTestFieldSafe(object) catch-all — issue #1369 |
| `TestField` | `(Joker, Integer, ErrorInfo)` | ✅ covered | routes through ALTestFieldSafe(object, NavALErrorInfo) catch-all — issue #1369 |
| `TestField` | `(Joker, Integer)` | ✅ covered | routes through ALTestFieldSafe(object) catch-all — issue #1369 |
| `TestField` | `(Joker, Joker, ErrorInfo)` | ✅ covered | routes through ALTestFieldSafe(object, NavALErrorInfo) catch-all — issue #1369 |
| `TestField` | `(Joker, Joker)` | ✅ covered | routes through ALTestFieldSafe(object) catch-all — issue #1369 |
| `TestField` | `(Joker, Label, ErrorInfo)` | ✅ covered | routes through ALTestFieldSafe(object, NavALErrorInfo) catch-all — issue #1369 |
| `TestField` | `(Joker, Label)` | ✅ covered | routes through ALTestFieldSafe(object) catch-all — issue #1369 |
| `TestField` | `(Joker, Text, ErrorInfo)` | ✅ covered | routes through ALTestFieldSafe(object, NavALErrorInfo) catch-all — issue #1369 |
| `TestField` | `(Joker, Text)` | ✅ covered | routes through ALTestFieldSafe(object) catch-all — issue #1369 |
| `TestField` | `(Joker, TextConst, ErrorInfo)` | ✅ covered | routes through ALTestFieldSafe(object, NavALErrorInfo) catch-all — issue #1369 |
| `TestField` | `(Joker, TextConst)` | ✅ covered | routes through ALTestFieldSafe(object) catch-all — issue #1369 |
| `TestField` | `(Joker)` | ✅ covered | > |
| `TransferFields` | `(Table, Boolean, Boolean)` | ✅ covered | MockRecordHandle.ALTransferFields(source, initPrimaryKey, validateFields) — 3-arg overload; tested in 309200-table-overloads |
| `TransferFields` | `(Table, Boolean)` | ✅ covered | > |
| `Truncate` | `(Boolean)` | ✅ covered | . Deletes all rows without triggers (delegates to DeleteAll(false)). |
| `Validate` | `(Joker, Joker)` | ✅ covered | ALValidateSafe(fieldNo, expectedType) — re-validates current field value without setting a new one. The 2-arg overload was missing from the injected Record class delegate methods. |
| `WritePermission` | `()` | ✅ covered | MockRecordHandle.ALWritePermission — always true (no permission system in standalone) |

## TaskScheduler  (0/6)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `CancelTask` | `(Guid)` | ❌ not-possible |  |
| `CanCreateTask` | `()` | ❌ not-possible |  |
| `CreateTask` | `(Integer, Integer, Boolean, Text, DateTime, RecordId, Duration)` | ❌ not-possible |  |
| `CreateTask` | `(Integer, Integer, Boolean, Text, DateTime, RecordId)` | ❌ not-possible |  |
| `SetTaskReady` | `(Guid, DateTime)` | ❌ not-possible |  |
| `TaskExists` | `(Guid)` | ❌ not-possible |  |

## TestAction  (3/3)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Enabled` | `()` | ✅ covered |  |
| `Invoke` | `()` | ✅ covered | dispatches compiled OnAction trigger via IdSpace hash lookup; overloads=1 |
| `Visible` | `()` | ✅ covered |  |

## TestField  (24/25)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Activate` | `()` | ✅ covered | no-op in standalone mode |
| `AsBoolean` | `()` | ✅ covered | AlCompat.ObjectToBoolean(_value) |
| `AsDate` | `()` | ✅ covered | returns stored NavDate or NavDate.Default |
| `AsDateTime` | `()` | ✅ covered | (0-arg, session); BC emits session-aware form (issue #1216); throws on non-convertible values |
| `AsDecimal` | `()` | ✅ covered | AlCompat.ObjectToDecimal(_value); fixed NavInteger/NavBigInteger cast in ExtractDecimal (issue #848) |
| `AsInteger` | `()` | ✅ covered | (int)AlCompat.ObjectToDecimal(_value) |
| `AssertEquals` | `(Joker)` | ✅ covered | compares via AlCompat.Format, throws on mismatch |
| `AssistEdit` | `()` | ✅ covered | no-op in standalone mode |
| `AsTime` | `()` | ✅ covered | returns stored NavTime or NavTime.Default |
| `Caption` | `()` | ✅ covered | returns NavText.Empty |
| `Drilldown` | `()` | ✅ covered | no-op in standalone mode |
| `Editable` | `()` | ✅ covered | returns true |
| `Enabled` | `()` | ✅ covered | returns true |
| `GetOption` | `(Integer)` | ✅ covered | returns integer representation of stored value |
| `GetValidationError` | `(Integer)` | ✅ covered | returns NavText.Empty (no errors in standalone mode) |
| `HideValue` | `()` | ✅ covered | returns bool true in standalone mode |
| `Invoke` | `()` | ✅ covered | no-op in standalone mode |
| `Lookup` | `()` | ✅ covered | no-op in standalone mode |
| `Lookup` | `(RecordRef)` | 🔲 gap |  |
| `OptionCount` | `()` | ✅ covered | returns 0 (no option metadata in standalone mode) |
| `SetValue` | `(Joker)` | ✅ covered | stores value in-memory; integer SetValue then AsDecimal proved in TestRequestPage handler |
| `ShowMandatory` | `()` | ✅ covered | returns bool true in standalone mode |
| `ValidationErrorCount` | `()` | ✅ covered | returns 0 (no errors in standalone mode) |
| `Value` | `(Text)` | ✅ covered | ALValue property returns stored object? |
| `Visible` | `()` | ✅ covered | returns true |

## TestFilter  (5/5)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Ascending` | `(Boolean)` | ✅ covered | property ALAscending (bool); BC lowers Ascending() → property read, Ascending(false) → property assignment; defaults true |
| `CurrentKey` | `()` | ✅ covered | property ALCurrentKey (string); BC lowers CurrentKey() → property read; returns comma-separated field numbers |
| `GetFilter` | `(TestFilterField)` | ✅ covered | ALGetFilter(int fieldNo) returns last filter set for that field |
| `SetCurrentKey` | `(TestFilterField, TestFilterField)` | ✅ covered | (DataError + fields; fields only); BC prepends DataError to the call |
| `SetFilter` | `(TestFilterField, Text)` | ✅ covered | ALSetFilter(int fieldNo, string filterExpression) stores per-field filters |

## TestHttpRequestMessage  (4/4)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `HasSecretUri` | `()` | ✅ covered | MockTestHttpRequestMessage.ALHasSecretUri property always returns false. |
| `Path` | `()` | ✅ covered | MockTestHttpRequestMessage.ALPath property (NavText); BC emits ALPath which rewriter redirects via MockJsonHelper.Path(MockTestHttpRequestMessage) overload. |
| `QueryParameters` | `()` | ✅ covered | BC emits ALQueryParameters property returning NavDictionary<NavText,NavText>; empty stub (no URI parsing in standalone) |
| `RequestType` | `()` | ✅ covered | MockTestHttpRequestMessage.ALRequestType property (NavText). |

## TestHttpResponseMessage  (6/6)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Content` | `()` | ✅ covered |  |
| `Headers` | `()` | ✅ covered |  |
| `HttpStatusCode` | `(Integer)` | ✅ covered |  |
| `IsBlockedByEnvironment` | `(Boolean)` | ✅ covered |  |
| `IsSuccessfulRequest` | `(Boolean)` | ✅ covered |  |
| `ReasonPhrase` | `(Text)` | ✅ covered |  |

## TestPage  (30/30)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Cancel` | `()` | ✅ covered | BC emits GetBuiltInAction((FormResult)Cancel).ALInvoke() — handled by MockTestPageAction. |
| `Caption` | `()` | ✅ covered | ALCaption property on MockTestPageHandle returns "TestPage" (stub). |
| `Close` | `()` | ✅ covered | ALClose() no-op on MockTestPageHandle. |
| `Edit` | `()` | ✅ covered | ALEdit() sets _editable=true and returns MockTestPageAction; invoked via P.Edit().Invoke() pattern. |
| `Editable` | `()` | ✅ covered | ALEditable property on MockTestPageHandle; reflects state set by OpenEdit/OpenView/OpenNew/New/Edit. |
| `Expand` | `(Boolean)` | ✅ covered | ALExpand(bool) no-op on MockTestPageHandle. |
| `FindFirstField` | `(TestField, Joker)` | ✅ covered | ALFindFirstField stubs always return false (no field scanning in standalone mode). |
| `FindNextField` | `(TestField, Joker)` | ✅ covered | ALFindNextField stubs always return false (no field scanning in standalone mode). |
| `FindPreviousField` | `(TestField, Joker)` | ✅ covered | ALFindPreviousField stubs always return false (no field scanning in standalone mode). |
| `First` | `()` | ✅ covered | ALFirst() always returns true (stub — no multi-record navigation in standalone mode). |
| `GetField` | `(Integer)` | ✅ covered | GetField(hash) returns MockTestPageField keyed by field hash. |
| `GetValidationError` | `(Integer)` | ✅ covered | ALGetValidationError(int) returns empty string (no validation errors in standalone mode). |
| `GoToKey` | `(Joker)` | ✅ covered | > |
| `GoToRecord` | `(Table)` | ✅ covered | > |
| `IsExpanded` | `()` | ✅ covered | ALIsExpanded property always returns false (standalone mode has no expand state). |
| `Last` | `()` | ✅ covered | ALLast() always returns false (stub — no multi-record navigation in standalone mode). |
| `New` | `()` | ✅ covered | ALNew() sets _editable=true on MockTestPageHandle; idempotent when already editable. |
| `Next` | `()` | ✅ covered | ALNext() always returns false (stub — no multi-record navigation in standalone mode). |
| `No` | `()` | ✅ covered | BC emits GetBuiltInAction((FormResult)No).ALInvoke() — handled by MockTestPageAction. |
| `OK` | `()` | ✅ covered | BC emits GetBuiltInAction((FormResult)OK).ALInvoke() — handled by MockTestPageAction. |
| `OpenEdit` | `()` | ✅ covered | sets ALEditable=true on MockTestPageHandle. |
| `OpenNew` | `()` | ✅ covered | sets ALEditable=true on MockTestPageHandle. |
| `OpenView` | `()` | ✅ covered | sets ALEditable=false on MockTestPageHandle. |
| `Prev` | `()` | ✅ covered | Same as Previous — BC emits ALPrevious(); always returns false. |
| `Previous` | `()` | ✅ covered | ALPrevious() always returns false (stub — no multi-record navigation in standalone mode). |
| `RunPageBackgroundTask` | `(Integer, Dictionary, Boolean)` | ✅ covered | (taskId; taskId+recordId); returns empty NavDictionary<NavText,NavText> — no background task execution in standalone mode |
| `Trap` | `()` | ✅ covered | registers trap in HandlerRegistry; consumed by next MockFormHandle.RunModal on same pageId. |
| `ValidationErrorCount` | `()` | ✅ covered | ALValidationErrorCount() always returns 0 (no field validation tracking in standalone mode). |
| `View` | `()` | ✅ covered | ALView() returns empty string (stub — no filter-view serialization in standalone mode). |
| `Yes` | `()` | ✅ covered | BC emits GetBuiltInAction((FormResult)Yes).ALInvoke() — handled by MockTestPageAction. |

## TestPart  (19/20)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Caption` | `()` | ✅ covered |  |
| `Editable` | `()` | ✅ covered |  |
| `Enabled` | `()` | ✅ covered |  |
| `Expand` | `(Boolean)` | ✅ covered |  |
| `FindFirstField` | `(TestField, Joker)` | ✅ covered |  |
| `FindNextField` | `(TestField, Joker)` | ✅ covered |  |
| `FindPreviousField` | `(TestField, Joker)` | ✅ covered |  |
| `First` | `()` | ✅ covered |  |
| `GetField` | `(Integer)` | ✅ covered |  |
| `GetValidationError` | `(Integer)` | ✅ covered |  |
| `GoToKey` | `(Joker)` | ✅ covered |  |
| `GoToRecord` | `(Table)` | ✅ covered |  |
| `IsExpanded` | `()` | ✅ covered |  |
| `Last` | `()` | ✅ covered |  |
| `New` | `()` | ✅ covered |  |
| `Next` | `()` | ✅ covered |  |
| `Prev` | `()` | ❓ n/a | removed in BC runtime 13.0 (AL0666); not available in BC 26+ |
| `Previous` | `()` | ✅ covered |  |
| `ValidationErrorCount` | `()` | ✅ covered |  |
| `Visible` | `()` | ✅ covered |  |

## TestRequestPage  (25/25)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Cancel` | `()` | ✅ covered | GetBuiltInAction(FormResult.Cancel) no-op stub |
| `Caption` | `()` | ✅ covered | ALCaption property returns "TestPage" |
| `Editable` | `()` | ✅ covered | mock tracks field Editable metadata; suite 161-testrequestpage-editable |
| `Expand` | `(Boolean)` | ✅ covered | ALExpand(bool) no-op stub |
| `FindFirstField` | `(TestField, Joker)` | ✅ covered | ALFindFirstField stub returns false |
| `FindNextField` | `(TestField, Joker)` | ✅ covered | ALFindNextField stub returns false |
| `FindPreviousField` | `(TestField, Joker)` | ✅ covered | ALFindPreviousField stub returns false |
| `First` | `()` | ✅ covered | ALFirst() returns true |
| `GetValidationError` | `(Integer)` | ✅ covered | ALGetValidationError(int) returns empty NavText |
| `GoToKey` | `(Joker)` | ✅ covered | ALGoToKey(DataError, params NavValue[]) returns true; tests/bucket-2/page-report/269-testrequestpage-methods |
| `GoToRecord` | `(Table)` | ✅ covered | ALGoToRecord(MockRecordHandle) returns true; tests/bucket-2/page-report/269-testrequestpage-methods |
| `IsExpanded` | `()` | ✅ covered | ALIsExpanded property returns false; tests/bucket-2/page-report/269-testrequestpage-methods |
| `Last` | `()` | ✅ covered | ALLast() returns false (empty page) |
| `New` | `()` | ✅ covered | ALNew() no-op stub |
| `Next` | `()` | ✅ covered | ALNext() returns false |
| `OK` | `()` | ✅ covered | GetBuiltInAction(FormResult.OK) sets ModalResult |
| `Preview` | `()` | ✅ covered | ALPreview() returns MockTestPageAction (call .Invoke()) |
| `Previous` | `()` | ✅ covered | ALPrevious() returns false |
| `Print` | `()` | ✅ covered | ALPrint() returns MockTestPageAction (call .Invoke()) |
| `SaveAsExcel` | `(Text)` | ✅ covered | ALSaveAsExcel(NavText) no-op stub |
| `SaveAsPdf` | `(Text)` | ✅ covered | ALSaveAsPdf(NavText) no-op stub |
| `SaveAsWord` | `(Text)` | ✅ covered | ALSaveAsWord(NavText) no-op stub |
| `SaveAsXml` | `(Text, Text)` | ✅ covered | ALSaveAsXml(NavText, NavText) no-op stub |
| `Schedule` | `()` | ✅ covered | ALSchedule() returns MockTestPageAction (call .Invoke()) |
| `ValidationErrorCount` | `()` | ✅ covered | ALValidationErrorCount() returns 0 |

## Text  (36/36)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Contains` | `(Text)` | ✅ covered | . Covered via NavText native — positive, negative, case-sensitive, empty-needle. |
| `ConvertStr` | `(Text, Text, Text)` | ✅ covered |  |
| `CopyStr` | `(Text, Integer, Integer)` | ✅ covered | (2-param via AlCompat; 3-param via BC runtime) |
| `DelChr` | `(Text, Text, Text)` | ✅ covered | . Static Text.DelChr form covered — where='=' strips all, '<' strips leading, '>' strips trailing. |
| `DelStr` | `(Text, Integer, Integer)` | ✅ covered | (pos / pos+count). Both forms covered — 1-based AL convention. |
| `EndsWith` | `(Text)` | ✅ covered | . Covered via NavText native — positive and negative cases. |
| `IncStr` | `(Text, BigInteger)` | ✅ covered | AlCompat.IncStr(string, long) — increments trailing numeric segment by N steps. Tested: +10, +1, large-step overflow, zero-step no-op. |
| `IncStr` | `(Text)` | ✅ covered |  |
| `IndexOf` | `(Text, Integer)` | ✅ covered | . Covered via NavText native — returns 1-based index (AL convention), 0 when not found, first-occurrence semantics. |
| `IndexOfAny` | `(List, Integer)` | ✅ covered | BC native NavTextExtensions.ALIndexOfAny works standalone. Tested in bucket-1/67-text-builtins. |
| `IndexOfAny` | `(Text, Integer)` | ✅ covered | BC native NavTextExtensions.ALIndexOfAny 2-arg (Text, startIndex) overload works standalone. Tested: found, skips-before, not-found. |
| `InsStr` | `(Text, Text, Integer)` | ✅ covered | . Static Text.InsStr form covered — insertion at start and middle positions (1-based). |
| `LastIndexOf` | `(Text, Integer)` | ✅ covered | . Covered via NavText native — 1-based last occurrence, 0 when not found, differs from IndexOf for multi-match strings. |
| `LowerCase` | `(Text)` | ✅ covered | . Static Text.LowerCase form covered — includes differs-from-UpperCase trap. |
| `MaxStrLen` | `(Text)` | ✅ covered | . Static Text.MaxStrLen form covered — returns the declared Text[N] length. |
| `MaxStrLen` | `(Variant)` | ✅ covered | ALSystemString.ALMaxStrLen(NavText) works for declared Text[N] and Code[N] local variables. Tested Text[42] and Code[15]. |
| `PadLeft` | `(Integer, Char)` | ✅ covered | (with padChar / default space). Covered via NavText native — pad char, default space, no-op when source already longer. |
| `PadRight` | `(Integer, Char)` | ✅ covered | (with padChar / default space). Covered via NavText native — includes differs-from-PadLeft trap. |
| `PadStr` | `(Text, Integer, Text)` | ✅ covered | negative length = left-pad (rewriter routes ALPadStr -> AlCompat.PadStr; BC native rejects negative length). Tested in bucket-1/67-text-builtins. |
| `Remove` | `(Integer, Integer)` | ✅ covered | (1-arg from-index / 2-arg with count). Covered via NavText native — 1-based AL convention. |
| `Replace` | `(Text, Text)` | ✅ covered | (char/char, text/text). Covered via NavText native — single-char, string replace, no-match-unchanged. |
| `SelectStr` | `(Integer, Text)` | ✅ covered |  |
| `Split` | `(List)` | ✅ covered | (Char, Text, List of [Char]). Covered via NavText native — preserves empty entries, no-separator returns single-element, multi-char separator not mistaken for single-char, List-of-Char splits on any of the supplied chars. |
| `Split` | `(Text)` | ✅ covered | BC native NavText.ALSplit 1-arg (Text separator) overload works standalone. Multi-char separator tested — count, element, differs-from-single-char trap. |
| `StartsWith` | `(Text)` | ✅ covered | . Covered via NavText native — positive, negative, case-sensitive. |
| `StrCheckSum` | `(Text, Text, Integer)` | ✅ covered | BC native ALSystemString.ALStrCheckSum works standalone (default modulus 10). Tested in bucket-1/67-text-builtins. |
| `StrLen` | `(Text)` | ✅ covered | . Static Text.StrLen form covered — length of non-empty and empty strings. |
| `StrPos` | `(Text, Text)` | ✅ covered | BC native works standalone. Tested in bucket-1/67-text-builtins. |
| `StrSubstNo` | `(Text, Joker)` | ✅ covered | (variadic). Static Text.StrSubstNo form covered — no-placeholder passthrough, single %1, multiple placeholders with mixed Text/Integer args. |
| `Substring` | `(Integer, Integer)` | ✅ covered | BC native NavTextExtensions.ALSubstring works standalone (1-based). Tested in bucket-1/67-text-builtins. |
| `ToLower` | `()` | ✅ covered | BC native works standalone. Tested in bucket-1/67-text-builtins. |
| `ToUpper` | `()` | ✅ covered | BC native works standalone. Tested in bucket-1/67-text-builtins. |
| `Trim` | `()` | ✅ covered | . Covered via NavText native — both-sides strip, no-whitespace-unchanged. |
| `TrimEnd` | `(Text)` | ✅ covered | . Covered via NavText native — trailing strip only, differs-from-TrimStart trap. |
| `TrimStart` | `(Text)` | ✅ covered | . Covered via NavText native — leading strip only. |
| `UpperCase` | `(Text)` | ✅ covered | . Static Text.UpperCase form covered — includes differs-from-LowerCase trap. |

## TextBuilder  (11/13)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Append` | `(Text)` | ✅ covered |  |
| `AppendLine` | `(Text)` | ✅ covered |  |
| `Capacity` | `(Integer)` | ✅ covered | MockTextBuilder.ALCapacity => _sb.Capacity |
| `Clear` | `()` | ✅ covered | MockTextBuilder.ALClear() clears StringBuilder |
| `EnsureCapacity` | `(Integer)` | ✅ covered | MockTextBuilder.ALEnsureCapacity(capacity) sets _sb.Capacity |
| `Insert` | `(Integer, Text)` | ✅ covered | MockTextBuilder.ALInsert(DataError, index, text) delegates to _sb.Insert |
| `Length` | `(Integer)` | ✅ covered | MockTextBuilder.ALLength is both getter and setter — assigning a smaller value truncates the buffer (BC semantics). |
| `MaxCapacity` | `()` | ✅ covered | MockTextBuilder.ALMaxCapacity => _sb.MaxCapacity |
| `Remove` | `(Integer, Integer)` | ✅ covered | MockTextBuilder.ALRemove(DataError, startIndex, count) |
| `Replace` | `(Text, Text, Integer, Integer)` | 🔶 not-tested |  |
| `Replace` | `(Text, Text)` | ✅ covered | MockTextBuilder.ALReplace(DataError, oldValue, newValue) |
| `ToText` | `()` | ✅ covered |  |
| `ToText` | `(Integer, Integer)` | 🔶 not-tested |  |

## TextConst  (19/19)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Contains` | `(Text)` | ✅ covered | BC native NavText methods work on codeunit-level Label (TextConst) values after NavTextConstant→NavText rewrite. |
| `EndsWith` | `(Text)` | ✅ covered | BC native NavText methods work on codeunit-level Label (TextConst) values after NavTextConstant→NavText rewrite. |
| `IndexOf` | `(Text, Integer)` | ✅ covered | BC native NavText methods work on codeunit-level Label (TextConst) values after NavTextConstant→NavText rewrite. |
| `IndexOfAny` | `(List, Integer)` | ✅ covered | BC native NavText.ALIndexOfAny works after NavTextConstant→NavText rewrite. Both 1-arg and 2-arg (with startIndex) overloads tested — positive and negative cases. |
| `IndexOfAny` | `(Text, Integer)` | ✅ covered | BC native NavTextExtensions.ALIndexOfAny 2-arg (Text, startIndex) form works on TextConst after NavTextConstant→NavText rewrite. Positive and negative cases. |
| `LastIndexOf` | `(Text, Integer)` | ✅ covered | BC native NavText methods work on codeunit-level Label (TextConst) values after NavTextConstant→NavText rewrite. |
| `PadLeft` | `(Integer, Char)` | ✅ covered | BC native NavText methods work on codeunit-level Label (TextConst) values after NavTextConstant→NavText rewrite. |
| `PadRight` | `(Integer, Char)` | ✅ covered | BC native NavText methods work on codeunit-level Label (TextConst) values after NavTextConstant→NavText rewrite. |
| `Remove` | `(Integer, Integer)` | ✅ covered | BC native NavText methods work on codeunit-level Label (TextConst) values after NavTextConstant→NavText rewrite. |
| `Replace` | `(Text, Text)` | ✅ covered | BC native NavText methods work on codeunit-level Label (TextConst) values after NavTextConstant→NavText rewrite. |
| `Split` | `(List)` | ✅ covered | BC native NavText methods work on codeunit-level Label (TextConst) values after NavTextConstant→NavText rewrite. |
| `Split` | `(Text)` | ✅ covered | BC native NavText.ALSplit 1-arg (Text separator) form works on TextConst after NavTextConstant→NavText rewrite. Tested count and absent-separator cases. |
| `StartsWith` | `(Text)` | ✅ covered | BC native NavText methods work on codeunit-level Label (TextConst) values after NavTextConstant→NavText rewrite. |
| `Substring` | `(Integer, Integer)` | ✅ covered | BC native NavText methods work on codeunit-level Label (TextConst) values after NavTextConstant→NavText rewrite. |
| `ToLower` | `()` | ✅ covered | BC native NavText methods work on codeunit-level Label (TextConst) values after NavTextConstant→NavText rewrite. |
| `ToUpper` | `()` | ✅ covered | BC native NavText methods work on codeunit-level Label (TextConst) values after NavTextConstant→NavText rewrite. |
| `Trim` | `()` | ✅ covered | BC native NavText methods work on codeunit-level Label (TextConst) values after NavTextConstant→NavText rewrite. |
| `TrimEnd` | `(Text)` | ✅ covered | BC native NavText methods work on codeunit-level Label (TextConst) values after NavTextConstant→NavText rewrite. |
| `TrimStart` | `(Text)` | ✅ covered | BC native NavText methods work on codeunit-level Label (TextConst) values after NavTextConstant→NavText rewrite. |

## Time  (5/5)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Hour` | `()` | ✅ covered | via Format(T,0,'<Hours24,2>') picture string; Time2HMS not available in BC AL |
| `Millisecond` | `()` | ✅ covered | works natively via NavTime; tested with default (0ms) and 000000T+100 (100ms) |
| `Minute` | `()` | ✅ covered | via Format(T,0,'<Minutes,2>') picture string |
| `Second` | `()` | ✅ covered | via Format(T,0,'<Seconds,2>') picture string |
| `ToText` | `(Boolean)` | ✅ covered | via Format(T) handled by AlCompat.FormatNavTime |

## Variant  (41/67)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `IsAction` | `()` | ❓ stub | always returns false (no Action mock in standalone mode) |
| `IsAutomation` | `()` | ❓ stub | always returns false |
| `IsBigInteger` | `()` | ✅ covered |  |
| `IsBinary` | `()` | ❓ stub | always returns false (no Binary mock in standalone mode) |
| `IsBoolean` | `()` | ✅ covered |  |
| `IsByte` | `()` | ✅ covered |  |
| `IsChar` | `()` | ✅ covered |  |
| `IsClientType` | `()` | ❓ stub | always returns false |
| `IsCode` | `()` | ✅ covered |  |
| `IsCodeunit` | `()` | ✅ covered | MockVariant.ALIsCodeunit checks _value is MockCodeunitHandle; AlCompat.ALIsCodeunit also recognises MockCodeunitHandle — closes #1184 |
| `IsDataClassification` | `()` | ❓ stub | always returns false |
| `IsDataClassificationType` | `()` | ❓ stub | always returns false |
| `IsDate` | `()` | ✅ covered |  |
| `IsDateFormula` | `()` | ✅ covered |  |
| `IsDateTime` | `()` | ✅ covered |  |
| `IsDecimal` | `()` | ✅ covered |  |
| `IsDefaultLayout` | `()` | ❓ stub | always returns false |
| `IsDictionary` | `()` | ✅ covered | checks NavDictionary open generic via AlCompat.ALIsDictionary |
| `IsDotNet` | `()` | ❓ stub | always returns false (no DotNet interop in standalone mode) |
| `IsDuration` | `()` | ✅ covered |  |
| `IsExecutionMode` | `()` | ❓ stub | always returns false |
| `IsFieldRef` | `()` | ✅ covered |  |
| `IsFile` | `()` | ❓ stub | always returns false (no File mock in standalone mode) |
| `IsFilterPageBuilder` | `()` | ❓ stub | always returns false — FilterPageBuilder is not Variant-assignable in BC AL; false case proven. |
| `IsGuid` | `()` | ✅ covered |  |
| `IsInStream` | `()` | ✅ covered | checks MockInStream via AlCompat.ALIsInStream |
| `IsInteger` | `()` | ✅ covered |  |
| `IsJsonArray` | `()` | ✅ covered | checks NavJsonToken subtype name == "NavJsonArray" |
| `IsJsonObject` | `()` | ✅ covered | checks NavJsonToken subtype name == "NavJsonObject" |
| `IsJsonToken` | `()` | ✅ covered | checks _value is NavJsonToken (base class for all JSON types) |
| `IsJsonValue` | `()` | ✅ covered | checks NavJsonToken subtype name == "NavJsonValue" |
| `IsList` | `()` | ✅ covered | checks IsGenericType with NavList open generic |
| `IsNotification` | `()` | ✅ covered | checks _value is MockNotification |
| `IsObjectType` | `()` | ❓ stub | always returns false |
| `IsOption` | `()` | ✅ covered |  |
| `IsOutStream` | `()` | ✅ covered | checks MockOutStream via AlCompat.ALIsOutStream |
| `IsPromptMode` | `()` | ❓ stub | always returns false — PromptMode enum is indistinguishable from NavOption in a Variant; false case proven. |
| `IsRecord` | `()` | ✅ covered |  |
| `IsRecordId` | `()` | ✅ covered |  |
| `IsRecordRef` | `()` | ✅ covered | ALIsRecordRef checks _value is MockRecordRef; round-trip via Variant also proven (NavIndirectValueToNavValue<MockRecordRef> rewritten to direct cast) |
| `IsReportFormat` | `()` | ❓ stub | always returns false — ReportFormat enum is indistinguishable from NavOption in a Variant; false case proven. |
| `IsSecurityFiltering` | `()` | ❓ stub | always returns false |
| `IsTableConnectionType` | `()` | ❓ stub | always returns false |
| `IsTestPermissions` | `()` | ❓ stub | always returns false |
| `IsText` | `()` | ✅ covered |  |
| `IsTextBuilder` | `()` | ✅ covered | checks _value is MockTextBuilder (rewriter maps NavTextBuilder → MockTextBuilder) |
| `IsTextConstant` | `()` | ❓ stub | always returns false |
| `IsTextEncoding` | `()` | ❓ stub | always returns false |
| `IsTime` | `()` | ✅ covered |  |
| `IsTransactionType` | `()` | ❓ stub | always returns false |
| `IsWideChar` | `()` | ❓ stub | always returns false |
| `IsXmlAttribute` | `()` | ✅ covered | checks NavXmlAttribute via MockVariant |
| `IsXmlAttributeCollection` | `()` | ❓ stub | checks NavXmlAttributeCollection — XmlAttributeCollection not Variant-assignable in BC AL; false case proven. |
| `IsXmlCData` | `()` | ✅ covered | checks NavXmlCData via MockVariant |
| `IsXmlComment` | `()` | ✅ covered | checks NavXmlComment via MockVariant |
| `IsXmlDeclaration` | `()` | ✅ covered | checks NavXmlDeclaration via MockVariant |
| `IsXmlDocument` | `()` | ✅ covered | checks NavXmlDocument via MockVariant |
| `IsXmlDocumentType` | `()` | ✅ covered | checks NavXmlDocumentType (XmlNode subtype, Variant-compatible) — true+false cases proven. |
| `IsXmlElement` | `()` | ✅ covered | checks NavXmlElement via MockVariant |
| `IsXmlNamespaceManager` | `()` | ❓ stub | checks NavXmlNamespaceManager — XmlNamespaceManager not Variant-assignable in BC AL; false case proven. |
| `IsXmlNameTable` | `()` | ❓ stub | checks MockXmlNameTable — XmlNameTable not Variant-assignable in BC AL; false case proven. |
| `IsXmlNode` | `()` | ✅ covered | checks NavXmlNode via MockVariant |
| `IsXmlNodeList` | `()` | ✅ covered | checks NavXmlNodeList via MockVariant |
| `IsXmlProcessingInstruction` | `()` | ✅ covered | checks NavXmlProcessingInstruction via MockVariant |
| `IsXmlReadOptions` | `()` | ❓ stub | checks NavXmlReadOptions — XmlReadOptions not Variant-assignable in BC AL; false case proven. |
| `IsXmlText` | `()` | ✅ covered | checks NavXmlText via MockVariant |
| `IsXmlWriteOptions` | `()` | ❓ stub | checks NavXmlWriteOptions — XmlWriteOptions not Variant-assignable in BC AL; false case proven. |

## Version  (6/7)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Build` | `()` | ✅ covered |  |
| `Create` | `(Integer, Integer, Integer, Integer)` | 🔲 gap |  |
| `Create` | `(Text)` | ✅ covered | ALCreate(major,minor,build) overload added in #1323 to fix CS1501 when BC emits NavVersion.ALCreate(major,minor,build) for Version.Create(major,minor,build) — revision defaults to 0 |
| `Major` | `()` | ✅ covered |  |
| `Minor` | `()` | ✅ covered |  |
| `Revision` | `()` | ✅ covered |  |
| `ToText` | `()` | ✅ covered |  |

## WebServiceActionContext  (7/7)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AddEntityKey` | `(Integer, Joker)` | ✅ covered | (base + DataError); MockWebServiceActionContext; stores (tableId, fieldName, fieldValue) tuples in-memory; tested via CallAddEntityKey no-throw |
| `GetObjectId` | `()` | ✅ covered | (base + DataError); MockWebServiceActionContext; returns stored int; default 0; round-trip tested |
| `GetObjectType` | `()` | ✅ covered | (base + DataError); MockWebServiceActionContext; returns stored int; default 0; round-trip tested |
| `GetResultCode` | `()` | ✅ covered | (base + DataError); MockWebServiceActionContext; returns WebServiceActionResultCode enum; round-trip tested for Created and OkResponse |
| `SetObjectId` | `(Integer)` | ✅ covered | (base + DataError); MockWebServiceActionContext; stores int; round-trip tested |
| `SetObjectType` | `(ObjectType)` | ✅ covered | (base + DataError); MockWebServiceActionContext; stores int; round-trip tested |
| `SetResultCode` | `(WebServiceActionResultCode)` | ✅ covered | (base + DataError); MockWebServiceActionContext; stores WebServiceActionResultCode enum; round-trip tested for Created and OkResponse |

## XmlAttribute  (22/24)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AddAfterSelf` | `(Joker)` | ✅ covered | . Covered via NavXmlAttribute native — AddAfterSelf inserts sibling attr into parent element, attr count increases to 2. |
| `AddBeforeSelf` | `(Joker)` | ✅ covered | . Covered via NavXmlAttribute native — AddBeforeSelf inserts sibling attr into parent element, attr count increases to 2. |
| `AsXmlNode` | `()` | ✅ covered | . Covered via NavXmlAttribute native — AsXmlNode().AsXmlAttribute().LocalName round-trips. |
| `Create` | `(Text, Text, Text)` | ✅ covered | . Covered via NavXmlAttribute native — 3-arg Create(localName, namespaceUri, value) round-trips all three parameters; namespace URI with ':' works without the NCL validation issue that bit XmlAttributeCollection in #1376. Closes #1399. |
| `Create` | `(Text, Text)` | ✅ covered | . Covered via NavXmlAttribute native — 2-arg Create(name, value) exercised. |
| `CreateNamespaceDeclaration` | `(Text, Text)` | ✅ covered | . Covered via NavXmlAttribute native — CreateNamespaceDeclaration(prefix, uri) returns attr with IsNamespaceDeclaration=true and LocalName=prefix. |
| `GetDocument` | `(XmlDocument)` | ✅ covered | . Covered via NavXmlAttribute native — GetDocument returns false for detached attribute. |
| `GetParent` | `(XmlElement)` | ✅ covered | . Covered via NavXmlAttribute native — returns false for detached attr, returns true with correct parent name when attr is attached via el.Add(attr). |
| `IsNamespaceDeclaration` | `()` | ✅ covered | . Covered via NavXmlAttribute native — false for plain attr, true for CreateNamespaceDeclaration result. |
| `LocalName` | `()` | ✅ covered | . Covered via NavXmlAttribute native — equals the attribute name for non-namespaced attrs. |
| `Name` | `()` | ✅ covered | . Covered via NavXmlAttribute native — returns the Create() name (not the same slot as Value). |
| `NamespacePrefix` | `()` | ✅ covered | . Covered via NavXmlAttribute native — returns empty string for plain (non-namespaced) attribute. |
| `NamespaceUri` | `()` | ✅ covered | . Covered via NavXmlAttribute native — defaults to empty for non-namespaced attrs. |
| `Remove` | `()` | ✅ covered | . Covered via NavXmlAttribute native — Remove() detaches attr from parent; GetParent returns false after removal. |
| `ReplaceWith` | `(Joker)` | ✅ covered | . Covered via NavXmlAttribute native — ReplaceWith(newAttr) swaps old attr for new; new attr findable by name on parent element. |
| `SelectNodes` | `(Text, XmlNamespaceManager, XmlNodeList)` | 🔲 gap |  |
| `SelectNodes` | `(Text, XmlNodeList)` | ✅ covered | . Covered via NavXmlAttribute native — SelectNodes('.', nodeList) called without error. |
| `SelectSingleNode` | `(Text, XmlNamespaceManager, XmlNode)` | 🔲 gap |  |
| `SelectSingleNode` | `(Text, XmlNode)` | ✅ covered | . Covered via NavXmlAttribute native — SelectSingleNode('.', node) called without error. |
| `Value` | `(Text)` | ✅ covered | . Covered via NavXmlAttribute native — round-trips via Attributes().Get; replaceable via SetAttribute(name, new value). |
| `WriteTo` | `(OutStream)` | ✅ covered | . Covered via NavXmlAttribute native — WriteTo(var Text) produces non-empty string containing both attribute name and value. |
| `WriteTo` | `(Text)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, ByRef<NavText>) via reflection — tested in suite 220-xml-writeto-overloads. |
| `WriteTo` | `(XmlWriteOptions, OutStream)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, NavXmlWriteOptions, MockOutStream) — options ignored, delegates to text overload. |
| `WriteTo` | `(XmlWriteOptions, Text)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, NavXmlWriteOptions, ByRef<NavText>) — options ignored, delegates to plain WriteTo. |

## XmlAttributeCollection  (5/10)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Count` | `()` | ✅ covered | BC native works standalone when the XmlElement is built programmatically. |
| `Get` | `(Integer, XmlAttribute)` | ✅ covered | . Covered via BC native — returns attribute value; returns false for missing key. |
| `Get` | `(Text, Text, XmlAttribute)` | 🔲 gap |  |
| `Get` | `(Text, XmlAttribute)` | 🔲 gap |  |
| `Remove` | `(Text, Text)` | 🔲 gap |  |
| `Remove` | `(Text)` | ✅ covered | . Covered via BC native — deletes named attribute so Get returns false. |
| `Remove` | `(XmlAttribute)` | 🔲 gap |  |
| `RemoveAll` | `()` | ✅ covered | . Covered via RemoveAllAttributes() on XmlElement; also tested here via HasAttributes becoming false. |
| `Set` | `(Text, Text, Text)` | 🔲 gap |  |
| `Set` | `(Text, Text)` | ✅ covered | . Covered via BC native — Set(name, value) replaces existing or adds new attribute. |

## XmlCData  (15/17)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AddAfterSelf` | `(Joker)` | ✅ covered | tested in tests/bucket-2/data-formats/166-xmlcdata |
| `AddBeforeSelf` | `(Joker)` | ✅ covered | tested in tests/bucket-2/data-formats/166-xmlcdata |
| `AsXmlNode` | `()` | ✅ covered | tested in tests/bucket-2/data-formats/166-xmlcdata |
| `Create` | `(Text)` | ✅ covered | tested in tests/bucket-2/data-formats/166-xmlcdata |
| `GetDocument` | `(XmlDocument)` | ✅ covered | tested in tests/bucket-2/data-formats/166-xmlcdata |
| `GetParent` | `(XmlElement)` | ✅ covered | tested in tests/bucket-2/data-formats/166-xmlcdata |
| `Remove` | `()` | ✅ covered | tested in tests/bucket-2/data-formats/166-xmlcdata |
| `ReplaceWith` | `(Joker)` | ✅ covered | tested in tests/bucket-2/data-formats/166-xmlcdata |
| `SelectNodes` | `(Text, XmlNamespaceManager, XmlNodeList)` | 🔲 gap |  |
| `SelectNodes` | `(Text, XmlNodeList)` | ✅ covered | tested in tests/bucket-2/data-formats/166-xmlcdata |
| `SelectSingleNode` | `(Text, XmlNamespaceManager, XmlNode)` | 🔲 gap |  |
| `SelectSingleNode` | `(Text, XmlNode)` | ✅ covered | tested in tests/bucket-2/data-formats/166-xmlcdata |
| `Value` | `(Text)` | ✅ covered | tested in tests/bucket-2/data-formats/166-xmlcdata |
| `WriteTo` | `(OutStream)` | ✅ covered | WriteTo(Text) covered via MockJsonHelper.WriteTo(object) fallback; tested in tests/bucket-2/data-formats/166-xmlcdata |
| `WriteTo` | `(Text)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, ByRef<NavText>) via reflection — tested in suite 220-xml-writeto-overloads. |
| `WriteTo` | `(XmlWriteOptions, OutStream)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, NavXmlWriteOptions, MockOutStream) — options ignored. |
| `WriteTo` | `(XmlWriteOptions, Text)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, NavXmlWriteOptions, ByRef<NavText>) — options ignored. |

## XmlComment  (15/17)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AddAfterSelf` | `(Joker)` | ✅ covered | BC native works standalone. |
| `AddBeforeSelf` | `(Joker)` | ✅ covered | BC native works standalone. |
| `AsXmlNode` | `()` | ✅ covered | BC native works standalone. Resulting XmlNode.IsXmlComment is true. |
| `Create` | `(Text)` | ✅ covered | BC native works standalone. Comment text round-trips via .Value, comment can be attached to an XmlElement via .Add. |
| `GetDocument` | `(XmlDocument)` | ✅ covered | BC native works standalone. |
| `GetParent` | `(XmlElement)` | ✅ covered | BC native works standalone. |
| `Remove` | `()` | ✅ covered | BC native works standalone. |
| `ReplaceWith` | `(Joker)` | ✅ covered | BC native works standalone. |
| `SelectNodes` | `(Text, XmlNamespaceManager, XmlNodeList)` | 🔲 gap |  |
| `SelectNodes` | `(Text, XmlNodeList)` | ✅ covered | BC native works standalone. |
| `SelectSingleNode` | `(Text, XmlNamespaceManager, XmlNode)` | 🔲 gap |  |
| `SelectSingleNode` | `(Text, XmlNode)` | ✅ covered | BC native works standalone. |
| `Value` | `(Text)` | ✅ covered | BC native works standalone. Round-trips comment text from Create. |
| `WriteTo` | `(OutStream)` | ✅ covered | WriteTo(Text) dispatched via MockJsonHelper.WriteTo(object) fallback (PR #712); BC native works standalone. |
| `WriteTo` | `(Text)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, ByRef<NavText>) via reflection — tested in suite 220-xml-writeto-overloads. |
| `WriteTo` | `(XmlWriteOptions, OutStream)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, NavXmlWriteOptions, MockOutStream) — options ignored. |
| `WriteTo` | `(XmlWriteOptions, Text)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, NavXmlWriteOptions, ByRef<NavText>) — options ignored. |

## XmlDeclaration  (17/19)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AddAfterSelf` | `(Joker)` | ✅ covered | . No-op for detached declarations (no XmlElement parent in the node tree). |
| `AddBeforeSelf` | `(Joker)` | ✅ covered | . No-op for detached declarations (no XmlElement parent in the node tree). |
| `AsXmlNode` | `()` | ✅ covered | . NavXmlDeclaration.ALAsXmlNode works natively. |
| `Create` | `(Text, Text, Text)` | ✅ covered | . Covered via NavXmlDeclaration native — Create(version, encoding, standalone). |
| `Encoding` | `(Text)` | ✅ covered | . Covered via NavXmlDeclaration native — getter and setter round-trip. |
| `GetDocument` | `(XmlDocument)` | ✅ covered | > |
| `GetParent` | `(XmlElement)` | ✅ covered | . NavXmlDeclaration.ALGetParent works natively — always false (declarations have no XmlElement parent). |
| `Remove` | `()` | ✅ covered | . NavXmlDeclaration.ALRemove works natively via AlCompat.XmlRemove dispatch. |
| `ReplaceWith` | `(Joker)` | ✅ covered | . NavXmlDeclaration.ALReplaceWith works natively via AlCompat.XmlReplaceWith dispatch. |
| `SelectNodes` | `(Text, XmlNamespaceManager, XmlNodeList)` | 🔲 gap |  |
| `SelectNodes` | `(Text, XmlNodeList)` | ✅ covered | > |
| `SelectSingleNode` | `(Text, XmlNamespaceManager, XmlNode)` | 🔲 gap |  |
| `SelectSingleNode` | `(Text, XmlNode)` | ✅ covered | (1-arg XPath variant covered). NavXmlDeclaration.ALSelectSingleNode works natively. |
| `Standalone` | `(Text)` | ✅ covered | . Covered via NavXmlDeclaration native — getter and setter round-trip. |
| `Version` | `(Text)` | ✅ covered | . Covered via NavXmlDeclaration native — getter and setter round-trip. |
| `WriteTo` | `(OutStream)` | ✅ covered | (Text overload covered). NavXmlDeclaration.ALWriteTo works natively. |
| `WriteTo` | `(Text)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, ByRef<NavText>) via reflection — tested in suite 220-xml-writeto-overloads. |
| `WriteTo` | `(XmlWriteOptions, OutStream)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, NavXmlWriteOptions, MockOutStream) — options ignored. |
| `WriteTo` | `(XmlWriteOptions, Text)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, NavXmlWriteOptions, ByRef<NavText>) — options ignored. |

## XmlDocument  (36/38)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Add` | `(Joker)` | ✅ covered | BC native NavXmlDocument.ALAdd works standalone. |
| `AddAfterSelf` | `(Joker)` | ✅ covered | rewriter intercepts ALAddAfterSelf on XmlDocument receiver and routes to AlCompat.XmlAddAfterSelf which no-ops for document (documents cannot have siblings) |
| `AddBeforeSelf` | `(Joker)` | ✅ covered | rewriter intercepts ALAddBeforeSelf on XmlDocument receiver and routes to AlCompat.XmlAddBeforeSelf which no-ops for document (documents cannot have siblings) |
| `AddFirst` | `(Joker)` | ✅ covered | BC native works standalone |
| `AsXmlNode` | `()` | ✅ covered | BC native works standalone; returned node reports IsXmlDocument=true |
| `Create` | `()` | ✅ covered | BC native NavXmlDocument.ALCreate works standalone. |
| `Create` | `(Joker)` | ✅ covered | BC native NavXmlDocument.ALCreate(Joker) works standalone; tested with XmlElement node arg |
| `GetChildElements` | `()` | ✅ covered | BC native works standalone; name-filtered overload tested |
| `GetChildElements` | `(Text, Text)` | ✅ covered | BC native works standalone; filters by local name and namespace URI |
| `GetChildElements` | `(Text)` | ✅ covered | BC native works standalone; filters by element name; returns 0 for no match |
| `GetChildNodes` | `()` | ✅ covered | BC native works standalone; 0-arg form returns XmlNodeList. |
| `GetDeclaration` | `(XmlDeclaration)` | ✅ covered | BC native works standalone; returns false when no declaration present. |
| `GetDescendantElements` | `()` | ✅ covered | BC native works standalone |
| `GetDescendantElements` | `(Text, Text)` | ✅ covered | BC native works standalone; filters descendants by local name and namespace URI |
| `GetDescendantElements` | `(Text)` | ✅ covered | BC native works standalone; filters descendants by element name |
| `GetDescendantNodes` | `()` | ✅ covered | BC native works standalone |
| `GetDocument` | `(XmlDocument)` | ✅ covered | BC native works standalone; document returns itself |
| `GetDocumentType` | `(XmlDocumentType)` | ✅ covered | BC native works standalone; returns false with no DOCTYPE, true with DOCTYPE |
| `GetParent` | `(XmlElement)` | ✅ covered | BC native works standalone; always returns false (document has no parent) |
| `GetRoot` | `(XmlElement)` | ✅ covered | BC native works standalone. |
| `NameTable` | `()` | ✅ covered | BC native works standalone |
| `ReadFrom` | `(InStream, XmlDocument)` | ✅ covered | Text and InStream forms (with and without XmlReadOptions); rewriter redirects NavXmlDocument.ALReadFrom to AlCompat.XmlDocumentReadFrom which handles both NavText/string and MockInStream; fixes issue #1081 |
| `ReadFrom` | `(InStream, XmlReadOptions, XmlDocument)` | ✅ covered | AlCompat.XmlDocumentReadFrom handles 4-arg form (stream + XmlReadOptions); rewriter redirects NavXmlDocument.ALReadFrom |
| `ReadFrom` | `(Text, XmlDocument)` | ✅ covered | AlCompat.XmlDocumentReadFrom handles Text form (static call); parses XML string and sets document |
| `ReadFrom` | `(Text, XmlReadOptions, XmlDocument)` | ✅ covered | AlCompat.XmlDocumentReadFrom handles 4-arg Text+XmlReadOptions form; default options parse correctly |
| `Remove` | `()` | ✅ covered | rewriter intercepts ALRemove on XmlDocument receiver and routes to AlCompat.XmlRemove which no-ops for document (Remove on a document is a no-op in BC too) |
| `RemoveNodes` | `()` | ✅ covered | BC native works standalone. |
| `ReplaceNodes` | `(Joker)` | ✅ covered | BC native works standalone; replaces all child nodes |
| `ReplaceWith` | `(Joker)` | ✅ covered | rewriter intercepts ALReplaceWith on XmlDocument receiver and routes to AlCompat.XmlReplaceWith which no-ops for document (documents cannot be replaced in their parent) |
| `SelectNodes` | `(Text, XmlNamespaceManager, XmlNodeList)` | 🔲 gap |  |
| `SelectNodes` | `(Text, XmlNodeList)` | ✅ covered | BC native works standalone; 2-arg (XPath, var XmlNodeList) tested. |
| `SelectSingleNode` | `(Text, XmlNamespaceManager, XmlNode)` | 🔲 gap |  |
| `SelectSingleNode` | `(Text, XmlNode)` | ✅ covered | BC native works standalone. XmlDocument receiver requires .GetRoot first (or call on XmlElement directly). See also XmlElement.SelectSingleNode. |
| `SetDeclaration` | `(XmlDeclaration)` | ✅ covered | BC native works standalone; round-trips version and encoding |
| `WriteTo` | `(OutStream)` | ✅ covered | BC native works standalone; Text overload tested. |
| `WriteTo` | `(Text)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, ByRef<NavText>) via reflection — tested in suite 220-xml-writeto-overloads. |
| `WriteTo` | `(XmlWriteOptions, OutStream)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, NavXmlWriteOptions, MockOutStream) — options ignored; tested in suite 220-xml-writeto-overloads. |
| `WriteTo` | `(XmlWriteOptions, Text)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, NavXmlWriteOptions, ByRef<NavText>) — options ignored; tested in suite 220-xml-writeto-overloads. |

## XmlDocumentType  (25/27)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AddAfterSelf` | `(Joker)` | ✅ covered | works natively via NavXmlDocumentType; tested by inserting PI sibling after DocType in document |
| `AddBeforeSelf` | `(Joker)` | ✅ covered | works natively via NavXmlDocumentType; tested by inserting PI sibling before DocType in document |
| `AsXmlNode` | `()` | ✅ covered | works natively via NavXmlDocumentType; result satisfies IsXmlDocumentType() |
| `Create` | `(Text, Text, Text, Text)` | ✅ covered | BC native NavXmlDocumentType.ALCreate(4) works standalone; name, publicId, systemId and internalSubset all round-trip |
| `Create` | `(Text, Text, Text)` | ✅ covered | BC native NavXmlDocumentType.ALCreate(3) works standalone; name, publicId, systemId all round-trip |
| `Create` | `(Text, Text)` | ✅ covered | BC native NavXmlDocumentType.ALCreate(2) works standalone; name and publicId set; can be added to XmlDocument |
| `Create` | `(Text)` | ✅ covered | uses real BC XmlDocumentType; all 4 overloads exercised |
| `GetDocument` | `(XmlDocument)` | ✅ covered | works natively; standalone DocType returns false; DocType added to XmlDocument.Create() returns true |
| `GetInternalSubset` | `(Text)` | ✅ covered | returns value passed to Create; empty when not set |
| `GetName` | `(Text)` | ✅ covered | returns the DOCTYPE name set at creation |
| `GetParent` | `(XmlElement)` | ✅ covered | works natively; standalone DocType returns false (no parent element) |
| `GetPublicId` | `(Text)` | ✅ covered | returns value passed to Create; empty when not set |
| `GetSystemId` | `(Text)` | ✅ covered | returns value passed to Create; empty when not set |
| `Remove` | `()` | ✅ covered | works natively; DocType.Remove() detaches it from the document; subsequent GetDocumentType returns false |
| `ReplaceWith` | `(Joker)` | ✅ covered | works natively; ReplaceWith(PI) removes DocType and inserts PI; subsequent GetDocumentType returns false |
| `SelectNodes` | `(Text, XmlNamespaceManager, XmlNodeList)` | 🔲 gap |  |
| `SelectNodes` | `(Text, XmlNodeList)` | ✅ covered | works natively when return value is captured (if ... then); void call form crashes in log path; tested with if-guard pattern |
| `SelectSingleNode` | `(Text, XmlNamespaceManager, XmlNode)` | 🔲 gap |  |
| `SelectSingleNode` | `(Text, XmlNode)` | ✅ covered | works natively via NavXmlDocumentType; returns false for xpath with no match |
| `SetInternalSubset` | `(Text)` | ✅ covered | SetInternalSubset then GetInternalSubset returns new value |
| `SetName` | `(Text)` | ✅ covered | SetName then GetName returns the new name |
| `SetPublicId` | `(Text)` | ✅ covered | SetPublicId then GetPublicId returns new value |
| `SetSystemId` | `(Text)` | ✅ covered | SetSystemId then GetSystemId returns new value |
| `WriteTo` | `(OutStream)` | ✅ covered | ALWriteTo rewriter routes through MockJsonHelper fallback object overload which calls ALWriteTo natively via reflection; output contains doctype name |
| `WriteTo` | `(Text)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, ByRef<NavText>) via reflection — same fallback as OutStream overload. |
| `WriteTo` | `(XmlWriteOptions, OutStream)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, NavXmlWriteOptions, MockOutStream) — options ignored. |
| `WriteTo` | `(XmlWriteOptions, Text)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, NavXmlWriteOptions, ByRef<NavText>) — options ignored. |

## XmlElement  (46/48)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Add` | `(Joker)` | ✅ covered | . Covered via NavXmlElement native — child XmlElement add and text add. |
| `AddAfterSelf` | `(Joker)` | ✅ covered | . Covered via NavXmlElement native. |
| `AddBeforeSelf` | `(Joker)` | ✅ covered | . Covered via NavXmlElement native. |
| `AddFirst` | `(Joker)` | ✅ covered | . Covered via NavXmlElement native — adds first child node and returns the added XmlNode. |
| `AsXmlNode` | `()` | ✅ covered | . Covered via NavXmlElement native — AsXmlNode().AsXmlElement().Name round-trips. |
| `Attributes` | `()` | ✅ covered | . Covered via NavXmlElement native — Attributes().Get(name, var XmlAttribute) returns true + populates the attribute. |
| `Create` | `(Text, Joker)` | ✅ covered | BC native NavXmlElement.ALCreate(name, Joker) works standalone; child node included |
| `Create` | `(Text, Text, Joker)` | ✅ covered | BC native NavXmlElement.ALCreate(name, ns, Joker) works standalone; namespace and child node both set |
| `Create` | `(Text, Text)` | ✅ covered | BC native NavXmlElement.ALCreate(name, ns) works standalone; NamespaceUri and LocalName both correct |
| `Create` | `(Text)` | ✅ covered | . Covered via NavXmlElement native — 1-arg Create(name) tested for Name, children, attributes, SelectNodes. |
| `GetChildElements` | `()` | ✅ covered | . Covered via NavXmlElement native — reflects the number of added child elements. |
| `GetChildElements` | `(Text, Text)` | ✅ covered | BC native works standalone; filters direct children by local name and namespace URI |
| `GetChildElements` | `(Text)` | ✅ covered | BC native works standalone; filters direct children by element name; multiple matches returned |
| `GetChildNodes` | `()` | ✅ covered | . Covered via NavXmlElement native — returns XmlNodeList of direct child nodes. |
| `GetDescendantElements` | `()` | ✅ covered | . Covered via NavXmlElement native. |
| `GetDescendantElements` | `(Text, Text)` | ✅ covered | BC native works standalone; filters descendants by local name and namespace URI |
| `GetDescendantElements` | `(Text)` | ✅ covered | BC native works standalone; filters all descendants by element name |
| `GetDescendantNodes` | `()` | ✅ covered | . Covered via NavXmlElement native. |
| `GetDocument` | `(XmlDocument)` | ✅ covered | . Covered via NavXmlElement native. |
| `GetNamespaceOfPrefix` | `(Text, Text)` | ✅ covered | . Covered via NavXmlElement native. |
| `GetParent` | `(XmlElement)` | ✅ covered | . Covered via NavXmlElement native — returns parent XmlElement or null if root. |
| `GetPrefixOfNamespace` | `(Text, Text)` | ✅ covered | . Covered via NavXmlElement native. |
| `HasAttributes` | `()` | ✅ covered | . Covered via NavXmlElement native — true after SetAttribute, false initially, false after RemoveAttribute. |
| `HasElements` | `()` | ✅ covered | . Covered via NavXmlElement native — true after Add, false initially. |
| `InnerText` | `()` | ✅ covered | . Covered via NavXmlElement native — reflects text added via Add. |
| `InnerXml` | `()` | ✅ covered | . Covered via NavXmlElement native — serialises child elements + attributes. |
| `IsEmpty` | `()` | ✅ covered | BC native works standalone when the XmlElement is built programmatically. |
| `LocalName` | `()` | ✅ covered | . Covered via NavXmlElement native. |
| `Name` | `()` | ✅ covered | . Covered via NavXmlElement native — returns the Create() name. |
| `NamespaceUri` | `()` | ✅ covered | . Covered via NavXmlElement native. |
| `Remove` | `()` | ✅ covered | . Covered via NavXmlElement native — removes element from parent tree. |
| `RemoveAllAttributes` | `()` | ✅ covered | BC native works standalone. Preserves element name + children; only attributes are cleared. |
| `RemoveAttribute` | `(Text, Text)` | ✅ covered | BC native works standalone; removes attribute by local name and namespace; wrong namespace leaves attribute intact |
| `RemoveAttribute` | `(Text)` | ✅ covered | . Covered via NavXmlElement native — clears the named attribute; HasAttributes becomes false if it was the only one. |
| `RemoveAttribute` | `(XmlAttribute)` | ✅ covered | BC native works standalone; removes by XmlAttribute object reference; HasAttributes becomes false when last attribute removed |
| `RemoveNodes` | `()` | ✅ covered | . Covered via NavXmlElement native — removes all child nodes from element. |
| `ReplaceNodes` | `(Joker)` | ✅ covered | . Covered via NavXmlElement native. |
| `ReplaceWith` | `(Joker)` | ✅ covered | . Covered via NavXmlElement native. |
| `SelectNodes` | `(Text, XmlNamespaceManager, XmlNodeList)` | 🔲 gap |  |
| `SelectNodes` | `(Text, XmlNodeList)` | ✅ covered | . Covered via NavXmlElement native — XPath matches descendants on a programmatically-built element. |
| `SelectSingleNode` | `(Text, XmlNamespaceManager, XmlNode)` | 🔲 gap |  |
| `SelectSingleNode` | `(Text, XmlNode)` | ✅ covered | BC native works standalone for programmatically-built XmlElements. Relative XPath is resolved against the receiver element. |
| `SetAttribute` | `(Text, Text, Text)` | ✅ covered | BC native works standalone; sets namespace-qualified attribute; HasAttributes becomes true; round-trips with RemoveAttribute(name, ns) |
| `SetAttribute` | `(Text, Text)` | ✅ covered | . Covered via NavXmlElement native — value readable via Attributes().Get; HasAttributes becomes true. |
| `WriteTo` | `(OutStream)` | ✅ covered | . Covered via NavXmlElement native — writes element to XmlWriter with proper formatting. |
| `WriteTo` | `(Text)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, ByRef<NavText>) via reflection — tested in suite 220-xml-writeto-overloads. |
| `WriteTo` | `(XmlWriteOptions, OutStream)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, NavXmlWriteOptions, MockOutStream) — options ignored; tested in suite 220-xml-writeto-overloads. |
| `WriteTo` | `(XmlWriteOptions, Text)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, NavXmlWriteOptions, ByRef<NavText>) — options ignored; tested in suite 220-xml-writeto-overloads. |

## XmlNameTable  (2/2)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Add` | `(Text)` | ✅ covered | BC native NavXmlNameTable.ALAdd works standalone via MockXmlNameTable wrapper. |
| `Get` | `(Text, Text)` | ✅ covered | MockXmlNameTable.ALGet returns false/empty instead of throwing NavNCLKeyNotFoundException when key absent. |

## XmlNamespaceManager  (8/8)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AddNamespace` | `(Text, Text)` | ✅ covered | BC native works standalone. |
| `HasNamespace` | `(Text)` | ✅ covered | BC native works standalone. True after AddNamespace, false for unknown prefix. |
| `LookupNamespace` | `(Text, Text)` | ✅ covered | BC native works standalone. Round-trips URI added via AddNamespace. |
| `LookupPrefix` | `(Text, Text)` | ✅ covered | BC native works standalone. Round-trips prefix added via AddNamespace. |
| `NameTable` | `(XmlNameTable)` | ✅ covered | BC native works standalone. Returns XmlNameTable without throwing. |
| `PopScope` | `()` | ✅ covered | BC native works standalone. Default-scope namespaces survive push+pop. |
| `PushScope` | `()` | ✅ covered | BC native works standalone. |
| `RemoveNamespace` | `(Text, Text)` | ✅ covered | BC native works standalone. HasNamespace returns false after removal. |

## XmlNode  (30/32)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AddAfterSelf` | `(Joker)` | ✅ covered | BC native NavXmlNode.ALAddAfterSelf works standalone |
| `AddBeforeSelf` | `(Joker)` | ✅ covered | BC native NavXmlNode.ALAddBeforeSelf works standalone |
| `AsXmlAttribute` | `()` | ✅ covered | BC native NavXmlNode.ALAsXmlAttribute works standalone; errors on type mismatch |
| `AsXmlCData` | `()` | ✅ covered | BC native NavXmlNode.ALAsXmlCData works standalone |
| `AsXmlComment` | `()` | ✅ covered | BC native NavXmlNode.ALAsXmlComment works standalone |
| `AsXmlDeclaration` | `()` | ✅ covered | BC native NavXmlNode.ALAsXmlDeclaration works standalone |
| `AsXmlDocument` | `()` | ✅ covered | BC native NavXmlNode.ALAsXmlDocument works standalone; IsXmlDocument verified |
| `AsXmlDocumentType` | `()` | ✅ covered | BC native NavXmlNode.ALAsXmlDocumentType works standalone |
| `AsXmlElement` | `()` | ✅ covered | BC native NavXmlNode.ALAsXmlElement works standalone; errors on type mismatch |
| `AsXmlProcessingInstruction` | `()` | ✅ covered | BC native NavXmlNode.ALAsXmlProcessingInstruction works standalone |
| `AsXmlText` | `()` | ✅ covered | BC native NavXmlNode.ALAsXmlText works standalone |
| `GetDocument` | `(XmlDocument)` | ✅ covered | BC native NavXmlNode.ALGetDocument works standalone; true when in doc, false for orphan |
| `GetParent` | `(XmlElement)` | ✅ covered | BC native NavXmlNode.ALGetParent works standalone; true when parented, false for orphan |
| `IsXmlAttribute` | `()` | ✅ covered | BC native NavXmlNode.ALIsXmlAttribute works standalone |
| `IsXmlCData` | `()` | ✅ covered | BC native NavXmlNode.ALIsXmlCData works standalone |
| `IsXmlComment` | `()` | ✅ covered | BC native NavXmlNode.ALIsXmlComment works standalone |
| `IsXmlDeclaration` | `()` | ✅ covered | BC native NavXmlNode.ALIsXmlDeclaration works standalone |
| `IsXmlDocument` | `()` | ✅ covered | BC native NavXmlNode.ALIsXmlDocument works standalone |
| `IsXmlDocumentType` | `()` | ✅ covered | BC native NavXmlNode.ALIsXmlDocumentType works standalone; returns false for Element and Document nodes |
| `IsXmlElement` | `()` | ✅ covered | BC native NavXmlNode.ALIsXmlElement works standalone |
| `IsXmlProcessingInstruction` | `()` | ✅ covered | BC native NavXmlNode.ALIsXmlProcessingInstruction works standalone |
| `IsXmlText` | `()` | ✅ covered | BC native NavXmlNode.ALIsXmlText works standalone |
| `Remove` | `()` | ✅ covered | BC native NavXmlNode.ALRemove works standalone; detaches from parent |
| `ReplaceWith` | `(Joker)` | ✅ covered | BC native NavXmlNode.ALReplaceWith works standalone; substitutes node in parent |
| `SelectNodes` | `(Text, XmlNamespaceManager, XmlNodeList)` | 🔲 gap |  |
| `SelectNodes` | `(Text, XmlNodeList)` | ✅ covered | BC native NavXmlNode.ALSelectNodes works standalone via XPath on programmatic trees |
| `SelectSingleNode` | `(Text, XmlNamespaceManager, XmlNode)` | 🔲 gap |  |
| `SelectSingleNode` | `(Text, XmlNode)` | ✅ covered | BC native NavXmlNode.ALSelectSingleNode works standalone via XPath on programmatic trees |
| `WriteTo` | `(OutStream)` | ✅ covered | BC native NavXmlNode.ALWriteTo works standalone; elements and attributes serialized correctly |
| `WriteTo` | `(Text)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, ByRef<NavText>) via reflection — tested in suite 220-xml-writeto-overloads. |
| `WriteTo` | `(XmlWriteOptions, OutStream)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, NavXmlWriteOptions, MockOutStream) — options ignored. |
| `WriteTo` | `(XmlWriteOptions, Text)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, NavXmlWriteOptions, ByRef<NavText>) — options ignored; tested in suite 220-xml-writeto-overloads. |

## XmlNodeList  (2/2)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Count` | `()` | ✅ covered | BC native works standalone when the XmlElement is built programmatically. |
| `Get` | `(Integer, XmlNode)` | ✅ covered | works natively via NavXmlNodeList; 1-based index; tested by selecting child element and calling Get(1) |

## XmlProcessingInstruction  (18/20)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AddAfterSelf` | `(Joker)` | ✅ covered | BC native NavXmlProcessingInstruction works standalone |
| `AddBeforeSelf` | `(Joker)` | ✅ covered | BC native NavXmlProcessingInstruction works standalone |
| `AsXmlNode` | `()` | ✅ covered | BC native NavXmlProcessingInstruction works standalone |
| `Create` | `(Text, Text)` | ✅ covered | . Covered via NavXmlProcessingInstruction native — Create(target, data). |
| `GetData` | `(Text)` | ✅ covered | . Covered via NavXmlProcessingInstruction native — returns the data set at create time or via SetData. |
| `GetDocument` | `(XmlDocument)` | ✅ covered | BC native NavXmlProcessingInstruction works standalone |
| `GetParent` | `(XmlElement)` | ✅ covered | BC native NavXmlProcessingInstruction works standalone |
| `GetTarget` | `(Text)` | ✅ covered | . Covered via NavXmlProcessingInstruction native — returns the target set at create time or via SetTarget. |
| `Remove` | `()` | ✅ covered | BC native NavXmlProcessingInstruction works standalone |
| `ReplaceWith` | `(Joker)` | ✅ covered | BC native NavXmlProcessingInstruction works standalone |
| `SelectNodes` | `(Text, XmlNamespaceManager, XmlNodeList)` | 🔲 gap |  |
| `SelectNodes` | `(Text, XmlNodeList)` | ✅ covered | BC native NavXmlProcessingInstruction works standalone |
| `SelectSingleNode` | `(Text, XmlNamespaceManager, XmlNode)` | 🔲 gap |  |
| `SelectSingleNode` | `(Text, XmlNode)` | ✅ covered | BC native NavXmlProcessingInstruction works standalone |
| `SetData` | `(Text)` | ✅ covered | . Covered via NavXmlProcessingInstruction native — setter round-trips through GetData. |
| `SetTarget` | `(Text)` | ✅ covered | . Covered via NavXmlProcessingInstruction native — setter round-trips through GetTarget. |
| `WriteTo` | `(OutStream)` | ✅ covered | BC native NavXmlProcessingInstruction works standalone |
| `WriteTo` | `(Text)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, ByRef<NavText>) via reflection — tested in suite 220-xml-writeto-overloads. |
| `WriteTo` | `(XmlWriteOptions, OutStream)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, NavXmlWriteOptions, MockOutStream) — options ignored. |
| `WriteTo` | `(XmlWriteOptions, Text)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, NavXmlWriteOptions, ByRef<NavText>) — options ignored; tested in suite 220-xml-writeto-overloads. |

## XmlReadOptions  (1/1)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `PreserveWhitespace` | `(Boolean)` | ✅ covered | works natively via NavXmlReadOptions; get and set tested (default false, set to true returns true) |

## XmlText  (15/17)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `AddAfterSelf` | `(Joker)` | ✅ covered | BC native — no mock needed |
| `AddBeforeSelf` | `(Joker)` | ✅ covered | BC native — no mock needed |
| `AsXmlNode` | `()` | ✅ covered | BC native — no mock needed |
| `Create` | `(Text)` | ✅ covered | BC native — no mock needed |
| `GetDocument` | `(XmlDocument)` | ✅ covered | BC native — no mock needed |
| `GetParent` | `(XmlElement)` | ✅ covered | BC native — no mock needed |
| `Remove` | `()` | ✅ covered | BC native — no mock needed |
| `ReplaceWith` | `(Joker)` | ✅ covered | BC native — no mock needed |
| `SelectNodes` | `(Text, XmlNamespaceManager, XmlNodeList)` | 🔲 gap |  |
| `SelectNodes` | `(Text, XmlNodeList)` | ✅ covered | BC native — no mock needed |
| `SelectSingleNode` | `(Text, XmlNamespaceManager, XmlNode)` | 🔲 gap |  |
| `SelectSingleNode` | `(Text, XmlNode)` | ✅ covered | BC native — no mock needed |
| `Value` | `(Text)` | ✅ covered | BC native — no mock needed |
| `WriteTo` | `(OutStream)` | ✅ covered |  |
| `WriteTo` | `(Text)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, ByRef<NavText>) via reflection — tested in suite 220-xml-writeto-overloads. |
| `WriteTo` | `(XmlWriteOptions, OutStream)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, NavXmlWriteOptions, MockOutStream) — options ignored. |
| `WriteTo` | `(XmlWriteOptions, Text)` | ✅ covered | MockJsonHelper.WriteTo(object, DataError, NavXmlWriteOptions, ByRef<NavText>) — options ignored; tested in suite 220-xml-writeto-overloads. |

## XmlWriteOptions  (1/1)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `PreserveWhitespace` | `(Boolean)` | ✅ covered |  |

## Xmlport  (3/3)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Export` | `(Integer, OutStream, Table)` | ✅ covered | rewriter routes NavXmlPort.Export -> MockXmlPortHandle.StaticExport which is a no-op. |
| `Import` | `(Integer, InStream, Table)` | ✅ covered | rewriter routes NavXmlPort.Import -> MockXmlPortHandle.StaticImport which is a no-op. |
| `Run` | `(Integer, Boolean, Boolean, Table)` | ✅ covered | rewriter routes NavXmlPort.Run -> MockXmlPortHandle.StaticRun which is a no-op (no file I/O or interactive UI standalone). Accepts all arg shapes via params object?[]. |

## XmlportInstance  (18/18)

| Method | Signature | Status | Notes |
|--------|-----------|--------|-------|
| `Break` | `()` | ✅ covered | no-op on MockXmlPortHandle; AL0161 — protected, only callable within XmlPort trigger code; not testable from external codeunits |
| `BreakUnbound` | `()` | ✅ covered | no-op on MockXmlPortHandle; AL0161 — protected, only callable within XmlPort trigger code; not testable from external codeunits |
| `CurrentPath` | `()` | ✅ covered | returns empty string on MockXmlPortHandle |
| `Export` | `()` | ✅ covered | no-op on MockXmlPortHandle |
| `FieldDelimiter` | `(Text)` | ✅ covered | property on MockXmlPortHandle, default empty string |
| `FieldSeparator` | `(Text)` | ✅ covered | property on MockXmlPortHandle, default empty string |
| `Filename` | `(Text)` | ✅ covered | property on MockXmlPortHandle, default empty string |
| `Import` | `()` | ✅ covered | no-op on MockXmlPortHandle |
| `ImportFile` | `(Boolean)` | ✅ covered | no-op stub via ALImportFile in StripEntireCallMethods; suite 158-xmlport-import-file |
| `Quit` | `()` | ✅ covered | no-op on MockXmlPortHandle; AL0161 — protected, only callable within XmlPort trigger code; not testable from external codeunits |
| `RecordSeparator` | `(Text)` | ✅ covered | property on MockXmlPortHandle, default empty string |
| `Run` | `()` | ✅ covered | no-op on MockXmlPortHandle |
| `SetDestination` | `(OutStream)` | ✅ covered | BC emits xP.Target.Destination = outStr; after .Target stripping lands on MockXmlPortHandle.Destination property |
| `SetSource` | `(InStream)` | ✅ covered | BC emits xP.Target.Source = inStr; after .Target stripping lands on MockXmlPortHandle.Source property |
| `SetTableView` | `(Table)` | ✅ covered | no-op on MockXmlPortHandle |
| `Skip` | `()` | ✅ covered | no-op on MockXmlPortHandle; AL0161 — protected, only callable within XmlPort trigger code; not testable from external codeunits |
| `TableSeparator` | `(Text)` | ✅ covered | property on MockXmlPortHandle, default empty string |
| `TextEncoding` | `(TextEncoding)` | ✅ covered | property on MockXmlPortHandle |
