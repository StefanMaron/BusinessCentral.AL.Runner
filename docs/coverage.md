# AL Language Coverage Map

Auto-generated from `docs/coverage.yaml`. Do not edit directly.

## Summary — syntax

| Status | Count |
|--------|-------|
| ✅ Covered | 98 |
| 🔲 Gap | 46 |
| ❌ Not possible | 3 |
| ⬜ Out of scope | 7 |
| **Total** | **154** |

## Summary — runtime-api

| Status | Count |
|--------|-------|
| ✅ Covered | 208 |
| 🔲 Gap | 1055 |
| ❌ Not possible | 31 |
| ⬜ Out of scope | 0 |
| **Total** | **1294** |

# Syntax layer

## Object

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `assembly_declaration` | ⬜ out-of-scope | — | .NET interop — requires BC runtime |
| `codeunit_declaration` | ✅ covered | `01-pure-function`, `10-cross-codeunit`, `112-codeunit-onrun-record`, `15-codeunit-assign`, `128-codeunit-not-found`, `75-codeunit-run-bool`, `76-navscope-dispatch` |  |
| `controladdin_declaration` | ⬜ out-of-scope | — | UI rendering — requires BC client |
| `dotnet_declaration` | ⬜ out-of-scope | — | .NET interop — requires BC runtime |
| `entitlement_declaration` | ❌ not-possible | — | Permission system — requires BC service tier |
| `enum_declaration` | ✅ covered | `20-option-fields`, `50-enum-ordinals`, `53-enum-interface`, `61-enum-names`, `123-fieldref-enum` |  |
| `interface_declaration` | ✅ covered | `03-interface-injection`, `31-interface-return`, `32-interface-param`, `42-list-of-interface`, `53-enum-interface` |  |
| `page_declaration` | ✅ covered | `71-testpage`, `40-page-run-record`, `48-page-variable`, `65-page-helper`, `73-modal-handler` |  |
| `permissionset_declaration` | ❌ not-possible | — | Permission system — requires BC service tier |
| `profile_declaration` | ⬜ out-of-scope | — | UI profile — requires BC client |
| `query_declaration` | ✅ covered | `90-query-object`, `125-xmlport-query-diagnostics` |  |
| `report_declaration` | ✅ covered | `112-report-dataset-columns`, `113-report-labels`, `119-report-skip`, `133-report-handler`, `91-report-handle` |  |
| `table_declaration` | ✅ covered | `02-record-operations`, `07-composite-pk`, `18-validate-trigger`, `19-table-procedures`, `62-pk-unique`, `63-oninsert-trigger` |  |
| `xmlport_declaration` | ✅ covered | `84-xmlport`, `125-xmlport-query-diagnostics` |  |

## Extension

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `enumextension_declaration` | 🔲 gap | — |  |
| `pagecustomization_declaration` | 🔲 gap | — |  |
| `pageextension_declaration` | ✅ covered | `36-page-ext-no-cascade`, `38-page-ext-currpage` |  |
| `permissionsetextension_declaration` | ❌ not-possible | — | Permission system — requires BC service tier |
| `profileextension_declaration` | ⬜ out-of-scope | — | UI profile — requires BC client |
| `reportextension_declaration` | ✅ covered | `129-reportext-parent` |  |
| `tableextension_declaration` | ✅ covered | `28-table-extension-fields`, `33-extension-validate`, `34-extension-parent-object`, `130-cross-ext-al0275` |  |

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
| `foreach_statement` | ✅ covered | `42-list-of-interface`, `83-list-byref`, `77-json-types` |  |
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
| `ternary_expression` | 🔲 gap | — |  |
| `unary_expression` | ✅ covered | `01-pure-function` |  |

## Type

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `array_type` | ✅ covered | `83-list-byref` |  |
| `basic_type` | ✅ covered | `01-pure-function`, `12-format-string`, `26-time-format`, `60-guid-text-get` |  |
| `code_type` | ✅ covered | `02-record-operations` |  |
| `dictionary_type` | 🔲 gap | — |  |
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
| `event_declaration` | ✅ covered | `66-event-subscribers`, `100-bind-subscription`, `97-event-params`, `37-event-scope` |  |
| `interface_procedure` | ✅ covered | `03-interface-injection`, `31-interface-return` |  |
| `interface_procedure_suffix` | ✅ covered | `03-interface-injection` |  |
| `procedure` | ✅ covered | `01-pure-function`, `10-cross-codeunit`, `19-table-procedures`, `41-try-function` |  |
| `procedure_modifier` | ✅ covered | `41-try-function` | local, internal, [TryFunction] modifiers |
| `trigger_declaration` | ✅ covered | `18-validate-trigger`, `63-oninsert-trigger`, `98-db-trigger-events`, `99-validate-events` |  |

## Variable

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `label_attribute` | ✅ covered | `113-report-labels` |  |
| `label_declaration` | ✅ covered | `113-report-labels`, `12-format-string` |  |
| `parameter` | ✅ covered | `01-pure-function`, `32-interface-param`, `83-list-byref` |  |
| `parameter_list` | ✅ covered | `01-pure-function` |  |
| `var_attribute_item` | 🔲 gap | — |  |
| `var_attribute_open` | 🔲 gap | — |  |
| `var_section` | ✅ covered | `01-pure-function` |  |
| `variable_declaration` | ✅ covered | `01-pure-function` |  |

## Table

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `aggregate_formula` | ✅ covered | `55-flowfield-exist`, `56-flowfield-multi` |  |
| `aggregate_function` | ✅ covered | `55-flowfield-exist`, `56-flowfield-multi` |  |
| `calc_field_reference` | ✅ covered | `55-flowfield-exist`, `56-flowfield-multi`, `124-temp-records-flowfields` |  |
| `field_declaration` | ✅ covered | `02-record-operations`, `07-composite-pk`, `20-option-fields`, `126-field-metadata` |  |
| `fieldgroup_declaration` | 🔲 gap | — |  |
| `fieldgroups_section` | 🔲 gap | — |  |
| `fields_section` | ✅ covered | `02-record-operations` |  |
| `fixed_section` | 🔲 gap | — |  |
| `key_declaration` | ✅ covered | `07-composite-pk`, `08-sort-ordering`, `109-currentkey` |  |
| `keys_section` | ✅ covered | `07-composite-pk` |  |
| `lookup_formula` | 🔲 gap | — |  |
| `table_relation_expression` | 🔲 gap | — | Table relations are parsed but not enforced at runtime |

## Page

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `action_area_section` | ✅ covered | `71-testpage`, `74-testpage-navigation` |  |
| `action_declaration` | ✅ covered | `71-testpage`, `74-testpage-navigation` |  |
| `action_group_section` | ✅ covered | `74-testpage-navigation` |  |
| `actionref_declaration` | 🔲 gap | — |  |
| `actions_section` | ✅ covered | `71-testpage` |  |
| `area_section` | ✅ covered | `71-testpage`, `36-page-ext-no-cascade` |  |
| `cuegroup_section` | 🔲 gap | — |  |
| `customaction_declaration` | 🔲 gap | — |  |
| `fileuploadaction_declaration` | 🔲 gap | — |  |
| `grid_section` | 🔲 gap | — |  |
| `group_section` | ✅ covered | `71-testpage`, `36-page-ext-no-cascade` |  |
| `layout_section` | ✅ covered | `71-testpage`, `36-page-ext-no-cascade` |  |
| `page_field` | ✅ covered | `71-testpage`, `132-testpage-stubs`, `90-testpage-extended` |  |
| `part_section` | 🔲 gap | — | Page parts require UI composition |
| `repeater_section` | ✅ covered | `74-testpage-navigation` |  |
| `separator_action` | 🔲 gap | — |  |
| `systemaction_declaration` | 🔲 gap | — |  |
| `systempart_section` | 🔲 gap | — | System parts require BC client |
| `usercontrol_section` | ⬜ out-of-scope | — | User controls require BC client rendering |
| `view_definition` | 🔲 gap | — |  |
| `views_section` | 🔲 gap | — |  |

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
| `schema_section` | 🔲 gap | — |  |
| `xmlport_attribute` | 🔲 gap | — |  |
| `xmlport_element` | ✅ covered | `84-xmlport` |  |

## Enum

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `enum_value_declaration` | ✅ covered | `20-option-fields`, `50-enum-ordinals`, `61-enum-names` |  |
| `implements_clause` | ✅ covered | `53-enum-interface` |  |

## Modification

| Construct | Status | Test Suites | Notes |
|-----------|--------|-------------|-------|
| `add_dataset_modification` | 🔲 gap | — |  |
| `addafter_action_modification` | 🔲 gap | — |  |
| `addafter_dataset_modification` | 🔲 gap | — |  |
| `addafter_modification` | 🔲 gap | — |  |
| `addafter_views_modification` | 🔲 gap | — |  |
| `addbefore_action_modification` | 🔲 gap | — |  |
| `addbefore_dataset_modification` | 🔲 gap | — |  |
| `addbefore_modification` | 🔲 gap | — |  |
| `addbefore_views_modification` | 🔲 gap | — |  |
| `addfirst_action_modification` | 🔲 gap | — |  |
| `addfirst_dataset_modification` | 🔲 gap | — |  |
| `addfirst_fieldgroup_modification` | 🔲 gap | — |  |
| `addfirst_modification` | ✅ covered | `36-page-ext-no-cascade` |  |
| `addfirst_views_modification` | 🔲 gap | — |  |
| `addlast_action_modification` | 🔲 gap | — |  |
| `addlast_dataset_modification` | 🔲 gap | — |  |
| `addlast_fieldgroup_modification` | 🔲 gap | — |  |
| `addlast_modification` | ✅ covered | `36-page-ext-no-cascade`, `38-page-ext-currpage` |  |
| `addlast_views_modification` | 🔲 gap | — |  |
| `modify_action_modification` | 🔲 gap | — |  |
| `modify_modification` | ✅ covered | `33-extension-validate`, `34-extension-parent-object` |  |
| `moveafter_modification` | 🔲 gap | — |  |
| `movebefore_modification` | 🔲 gap | — |  |
| `movefirst_modification` | 🔲 gap | — |  |
| `movelast_modification` | 🔲 gap | — |  |

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
| `attribute_item` | ✅ covered | `41-try-function`, `66-event-subscribers`, `100-bind-subscription` |  |

# Runtime API layer

Source: `Microsoft.Dynamics.Nav.CodeAnalysis` method symbol tables. Coverage = AL-prefixed method present in `AlRunner/Runtime/*.cs`.

## BigInteger  (0/1)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `ToText` | 🔲 gap | 1 |  |

## BigText  (6/6)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AddText` | ✅ covered | 2 |  |
| `GetSubText` | ✅ covered | 2 |  |
| `Length` | ✅ covered | 1 |  |
| `Read` | ✅ covered | 1 |  |
| `TextPos` | ✅ covered | 1 |  |
| `Write` | ✅ covered | 1 |  |

## Blob  (4/6)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `CreateInStream` | ✅ covered | 1 |  |
| `CreateOutStream` | ✅ covered | 1 |  |
| `Export` | 🔲 gap | 1 |  |
| `HasValue` | ✅ covered | 1 |  |
| `Import` | 🔲 gap | 1 |  |
| `Length` | ✅ covered | 1 |  |

## Boolean  (0/1)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `ToText` | 🔲 gap | 2 |  |

## Byte  (0/1)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `ToText` | 🔲 gap | 2 |  |

## Codeunit  (0/1)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Run` | 🔲 gap | 1 |  |

## CodeunitInstance  (0/1)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Run` | 🔲 gap | 1 |  |

## CompanyProperty  (0/3)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `DisplayName` | 🔲 gap | 1 |  |
| `ID` | 🔲 gap | 1 |  |
| `UrlName` | 🔲 gap | 1 |  |

## Cookie  (0/7)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Domain` | 🔲 gap | 1 |  |
| `Expires` | 🔲 gap | 1 |  |
| `HttpOnly` | 🔲 gap | 1 |  |
| `Name` | 🔲 gap | 1 |  |
| `Path` | 🔲 gap | 1 |  |
| `Secure` | 🔲 gap | 1 |  |
| `Value` | 🔲 gap | 1 |  |

## DataTransfer  (7/8)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AddConstantValue` | ✅ covered | 1 |  |
| `AddFieldValue` | ✅ covered | 1 |  |
| `AddJoin` | ✅ covered | 1 |  |
| `AddSourceFilter` | ✅ covered | 1 |  |
| `CopyFields` | ✅ covered | 1 |  |
| `CopyRows` | ✅ covered | 1 |  |
| `SetTables` | ✅ covered | 1 |  |
| `UpdateAuditFields` | 🔲 gap | 1 |  |

## Database  (0/29)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AlterKey` | 🔲 gap | 1 |  |
| `ChangeUserPassword` | 🔲 gap | 1 |  |
| `CheckLicenseFile` | 🔲 gap | 1 |  |
| `Commit` | 🔲 gap | 1 |  |
| `CompanyName` | 🔲 gap | 1 |  |
| `CopyCompany` | 🔲 gap | 1 |  |
| `CurrentTransactionType` | 🔲 gap | 1 |  |
| `DataFileInformation` | 🔲 gap | 1 |  |
| `ExportData` | 🔲 gap | 1 |  |
| `GetDefaultTableConnection` | 🔲 gap | 1 |  |
| `HasTableConnection` | 🔲 gap | 1 |  |
| `ImportData` | 🔲 gap | 1 |  |
| `IsInWriteTransaction` | 🔲 gap | 1 |  |
| `LastUsedRowVersion` | 🔲 gap | 1 |  |
| `LockTimeout` | 🔲 gap | 1 |  |
| `LockTimeoutDuration` | 🔲 gap | 1 |  |
| `MinimumActiveRowVersion` | 🔲 gap | 1 |  |
| `RegisterTableConnection` | 🔲 gap | 1 |  |
| `SelectLatestVersion` | 🔲 gap | 2 |  |
| `SerialNumber` | 🔲 gap | 1 |  |
| `ServiceInstanceId` | 🔲 gap | 1 |  |
| `SessionId` | 🔲 gap | 1 |  |
| `SetDefaultTableConnection` | 🔲 gap | 1 |  |
| `SetUserPassword` | 🔲 gap | 1 |  |
| `SID` | 🔲 gap | 1 |  |
| `TenantId` | 🔲 gap | 1 |  |
| `UnregisterTableConnection` | 🔲 gap | 1 |  |
| `UserId` | 🔲 gap | 1 |  |
| `UserSecurityId` | 🔲 gap | 1 |  |

## Date  (0/6)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Day` | 🔲 gap | 1 |  |
| `DayOfWeek` | 🔲 gap | 1 |  |
| `Month` | 🔲 gap | 1 |  |
| `ToText` | 🔲 gap | 1 |  |
| `WeekNo` | 🔲 gap | 1 |  |
| `Year` | 🔲 gap | 1 |  |

## DateTime  (0/3)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Date` | 🔲 gap | 1 |  |
| `Time` | 🔲 gap | 1 |  |
| `ToText` | 🔲 gap | 1 |  |

## Debugger  (0/19)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Activate` | ❌ not-possible | 1 |  |
| `Attach` | ❌ not-possible | 1 |  |
| `Break` | ❌ not-possible | 1 |  |
| `BreakOnError` | ❌ not-possible | 1 |  |
| `BreakOnRecordChanges` | ❌ not-possible | 1 |  |
| `Continue` | ❌ not-possible | 1 |  |
| `Deactivate` | ❌ not-possible | 1 |  |
| `DebuggedSessionID` | ❌ not-possible | 1 |  |
| `DebuggingSessionID` | ❌ not-possible | 1 |  |
| `EnableSqlTrace` | ❌ not-possible | 1 |  |
| `GetLastErrorText` | ❌ not-possible | 1 |  |
| `IsActive` | ❌ not-possible | 1 |  |
| `IsAttached` | ❌ not-possible | 1 |  |
| `IsBreakpointHit` | ❌ not-possible | 1 |  |
| `SkipSystemTriggers` | ❌ not-possible | 1 |  |
| `StepInto` | ❌ not-possible | 1 |  |
| `StepOut` | ❌ not-possible | 1 |  |
| `StepOver` | ❌ not-possible | 1 |  |
| `Stop` | ❌ not-possible | 1 |  |

## Decimal  (0/1)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `ToText` | 🔲 gap | 2 |  |

## Dialog  (4/9)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Close` | ✅ covered | 1 |  |
| `Confirm` | ✅ covered | 1 |  |
| `Error` | 🔲 gap | 2 |  |
| `HideSubsequentDialogs` | 🔲 gap | 1 |  |
| `LogInternalError` | 🔲 gap | 2 |  |
| `Message` | 🔲 gap | 1 |  |
| `Open` | ✅ covered | 1 |  |
| `StrMenu` | 🔲 gap | 1 |  |
| `Update` | ✅ covered | 1 |  |

## Duration  (0/1)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `ToText` | 🔲 gap | 1 |  |

## EnumType  (0/4)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AsInteger` | 🔲 gap | 1 |  |
| `FromInteger` | 🔲 gap | 1 |  |
| `Names` | 🔲 gap | 2 |  |
| `Ordinals` | 🔲 gap | 2 |  |

## ErrorInfo  (0/18)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AddAction` | 🔲 gap | 2 |  |
| `AddNavigationAction` | 🔲 gap | 2 |  |
| `Callstack` | 🔲 gap | 1 |  |
| `Collectible` | 🔲 gap | 1 |  |
| `ControlName` | 🔲 gap | 1 |  |
| `Create` | 🔲 gap | 2 |  |
| `CustomDimensions` | 🔲 gap | 1 |  |
| `DataClassification` | 🔲 gap | 1 |  |
| `DetailedMessage` | 🔲 gap | 1 |  |
| `ErrorType` | 🔲 gap | 1 |  |
| `FieldNo` | 🔲 gap | 1 |  |
| `Message` | 🔲 gap | 1 |  |
| `PageNo` | 🔲 gap | 1 |  |
| `RecordId` | 🔲 gap | 1 |  |
| `SystemId` | 🔲 gap | 1 |  |
| `TableId` | 🔲 gap | 1 |  |
| `Title` | 🔲 gap | 1 |  |
| `Verbosity` | 🔲 gap | 1 |  |

## FieldRef  (23/31)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Active` | ✅ covered | 1 |  |
| `CalcField` | ✅ covered | 1 |  |
| `CalcSum` | ✅ covered | 1 |  |
| `Caption` | ✅ covered | 1 |  |
| `Class` | ✅ covered | 1 |  |
| `EnumValueCount` | 🔲 gap | 1 |  |
| `FieldError` | ✅ covered | 2 |  |
| `GetEnumValueCaption` | 🔲 gap | 1 |  |
| `GetEnumValueCaptionFromOrdinalValue` | 🔲 gap | 1 |  |
| `GetEnumValueName` | 🔲 gap | 1 |  |
| `GetEnumValueNameFromOrdinalValue` | 🔲 gap | 1 |  |
| `GetEnumValueOrdinal` | 🔲 gap | 1 |  |
| `GetFilter` | ✅ covered | 1 |  |
| `GetRangeMax` | ✅ covered | 1 |  |
| `GetRangeMin` | ✅ covered | 1 |  |
| `IsEnum` | ✅ covered | 1 |  |
| `IsOptimizedForTextSearch` | 🔲 gap | 1 |  |
| `Length` | ✅ covered | 1 |  |
| `Name` | ✅ covered | 1 |  |
| `Number` | ✅ covered | 1 |  |
| `OptionCaption` | ✅ covered | 1 |  |
| `OptionMembers` | 🔲 gap | 1 |  |
| `OptionString` | ✅ covered | 1 |  |
| `Record` | ✅ covered | 1 |  |
| `Relation` | ✅ covered | 1 |  |
| `SetFilter` | ✅ covered | 1 |  |
| `SetRange` | ✅ covered | 1 |  |
| `TestField` | ✅ covered | 38 |  |
| `Type` | ✅ covered | 1 |  |
| `Validate` | ✅ covered | 1 |  |
| `Value` | ✅ covered | 1 |  |

## File  (2/28)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Close` | 🔲 gap | 1 |  |
| `Copy` | 🔲 gap | 1 |  |
| `Create` | 🔲 gap | 1 |  |
| `CreateInStream` | 🔲 gap | 1 |  |
| `CreateOutStream` | 🔲 gap | 1 |  |
| `CreateTempFile` | 🔲 gap | 1 |  |
| `Download` | 🔲 gap | 1 |  |
| `DownloadFromStream` | ✅ covered | 1 |  |
| `Erase` | 🔲 gap | 1 |  |
| `Exists` | 🔲 gap | 1 |  |
| `GetStamp` | 🔲 gap | 1 |  |
| `IsPathTemporary` | 🔲 gap | 1 |  |
| `Len` | 🔲 gap | 1 |  |
| `Name` | 🔲 gap | 1 |  |
| `Open` | 🔲 gap | 1 |  |
| `Pos` | 🔲 gap | 1 |  |
| `Read` | 🔲 gap | 1 |  |
| `Rename` | 🔲 gap | 1 |  |
| `Seek` | 🔲 gap | 1 |  |
| `SetStamp` | 🔲 gap | 1 |  |
| `TextMode` | 🔲 gap | 1 |  |
| `Trunc` | 🔲 gap | 1 |  |
| `Upload` | 🔲 gap | 1 |  |
| `UploadIntoStream` | ✅ covered | 2 |  |
| `View` | 🔲 gap | 1 |  |
| `ViewFromStream` | 🔲 gap | 1 |  |
| `Write` | 🔲 gap | 21 |  |
| `WriteMode` | 🔲 gap | 1 |  |

## FileUpload  (0/2)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `CreateInStream` | 🔲 gap | 2 |  |
| `FileName` | 🔲 gap | 1 |  |

## FilterPageBuilder  (0/11)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AddField` | 🔲 gap | 2 |  |
| `AddFieldNo` | 🔲 gap | 1 |  |
| `AddRecord` | 🔲 gap | 1 |  |
| `AddRecordRef` | 🔲 gap | 1 |  |
| `AddTable` | 🔲 gap | 1 |  |
| `Count` | 🔲 gap | 1 |  |
| `GetView` | 🔲 gap | 1 |  |
| `Name` | 🔲 gap | 1 |  |
| `PageCaption` | 🔲 gap | 1 |  |
| `RunModal` | 🔲 gap | 1 |  |
| `SetView` | 🔲 gap | 1 |  |

## Guid  (0/3)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `CreateGuid` | 🔲 gap | 1 |  |
| `CreateSequentialGuid` | 🔲 gap | 1 |  |
| `ToText` | 🔲 gap | 2 |  |

## HttpClient  (3/16)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AddCertificate` | 🔲 gap | 2 |  |
| `Clear` | 🔲 gap | 1 |  |
| `DefaultRequestHeaders` | ✅ covered | 1 |  |
| `Delete` | ❌ not-possible | 1 |  |
| `Get` | ❌ not-possible | 1 |  |
| `GetBaseAddress` | 🔲 gap | 1 |  |
| `Patch` | ❌ not-possible | 1 |  |
| `Post` | ❌ not-possible | 1 |  |
| `Put` | ❌ not-possible | 1 |  |
| `Send` | ❌ not-possible | 1 |  |
| `SetBaseAddress` | 🔲 gap | 1 |  |
| `Timeout` | ✅ covered | 1 |  |
| `UseDefaultNetworkWindowsAuthentication` | ✅ covered | 1 |  |
| `UseResponseCookies` | 🔲 gap | 1 |  |
| `UseServerCertificateValidation` | 🔲 gap | 1 |  |
| `UseWindowsAuthentication` | 🔲 gap | 2 |  |

## HttpContent  (2/5)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Clear` | 🔲 gap | 1 |  |
| `GetHeaders` | ✅ covered | 1 |  |
| `IsSecretContent` | 🔲 gap | 1 |  |
| `ReadAs` | ✅ covered | 3 |  |
| `WriteFrom` | 🔲 gap | 3 |  |

## HttpHeaders  (4/9)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Add` | ✅ covered | 2 |  |
| `Clear` | 🔲 gap | 1 |  |
| `Contains` | ✅ covered | 1 |  |
| `ContainsSecret` | 🔲 gap | 1 |  |
| `GetSecretValues` | 🔲 gap | 2 |  |
| `GetValues` | ✅ covered | 2 |  |
| `Keys` | 🔲 gap | 1 |  |
| `Remove` | ✅ covered | 1 |  |
| `TryAddWithoutValidation` | 🔲 gap | 2 |  |

## HttpRequestMessage  (5/11)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Content` | ✅ covered | 1 |  |
| `GetCookie` | 🔲 gap | 1 |  |
| `GetCookieNames` | 🔲 gap | 1 |  |
| `GetHeaders` | ✅ covered | 1 |  |
| `GetRequestUri` | ✅ covered | 1 |  |
| `GetSecretRequestUri` | 🔲 gap | 1 |  |
| `Method` | ✅ covered | 1 |  |
| `RemoveCookie` | 🔲 gap | 1 |  |
| `SetCookie` | 🔲 gap | 2 |  |
| `SetRequestUri` | ✅ covered | 1 |  |
| `SetSecretRequestUri` | 🔲 gap | 1 |  |

## HttpResponseMessage  (5/8)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Content` | ✅ covered | 1 |  |
| `GetCookie` | 🔲 gap | 1 |  |
| `GetCookieNames` | 🔲 gap | 1 |  |
| `Headers` | ✅ covered | 1 |  |
| `HttpStatusCode` | ✅ covered | 1 |  |
| `IsBlockedByEnvironment` | 🔲 gap | 1 |  |
| `IsSuccessStatusCode` | ✅ covered | 1 |  |
| `ReasonPhrase` | ✅ covered | 1 |  |

## InStream  (5/6)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `EOS` | 🔲 gap | 1 |  |
| `Length` | ✅ covered | 1 |  |
| `Position` | ✅ covered | 1 |  |
| `Read` | ✅ covered | 9 |  |
| `ReadText` | ✅ covered | 1 |  |
| `ResetPosition` | ✅ covered | 1 |  |

## Integer  (0/1)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `ToText` | 🔲 gap | 1 |  |

## IsolatedStorage  (4/5)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Contains` | ✅ covered | 2 |  |
| `Delete` | ✅ covered | 1 |  |
| `Get` | ✅ covered | 4 |  |
| `Set` | ✅ covered | 2 |  |
| `SetEncrypted` | 🔲 gap | 2 |  |

## JsonArray  (3/27)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Add` | 🔲 gap | 16 |  |
| `AsToken` | 🔲 gap | 1 |  |
| `Clone` | 🔲 gap | 1 |  |
| `Count` | 🔲 gap | 1 |  |
| `Get` | 🔲 gap | 1 |  |
| `GetArray` | 🔲 gap | 1 |  |
| `GetBigInteger` | 🔲 gap | 1 |  |
| `GetBoolean` | 🔲 gap | 1 |  |
| `GetByte` | 🔲 gap | 1 |  |
| `GetChar` | 🔲 gap | 1 |  |
| `GetDate` | 🔲 gap | 1 |  |
| `GetDateTime` | 🔲 gap | 1 |  |
| `GetDecimal` | 🔲 gap | 1 |  |
| `GetDuration` | 🔲 gap | 1 |  |
| `GetInteger` | 🔲 gap | 1 |  |
| `GetObject` | 🔲 gap | 1 |  |
| `GetOption` | 🔲 gap | 1 |  |
| `GetText` | 🔲 gap | 1 |  |
| `GetTime` | 🔲 gap | 1 |  |
| `IndexOf` | 🔲 gap | 16 |  |
| `Insert` | 🔲 gap | 16 |  |
| `Path` | 🔲 gap | 1 |  |
| `ReadFrom` | ✅ covered | 2 |  |
| `RemoveAt` | 🔲 gap | 1 |  |
| `SelectToken` | ✅ covered | 1 |  |
| `Set` | 🔲 gap | 16 |  |
| `WriteTo` | ✅ covered | 2 |  |

## JsonObject  (3/30)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Add` | 🔲 gap | 16 |  |
| `AsToken` | 🔲 gap | 1 |  |
| `Clone` | 🔲 gap | 1 |  |
| `Contains` | 🔲 gap | 1 |  |
| `Get` | 🔲 gap | 1 |  |
| `GetArray` | 🔲 gap | 1 |  |
| `GetBigInteger` | 🔲 gap | 1 |  |
| `GetBoolean` | 🔲 gap | 1 |  |
| `GetByte` | 🔲 gap | 1 |  |
| `GetChar` | 🔲 gap | 1 |  |
| `GetDate` | 🔲 gap | 1 |  |
| `GetDateTime` | 🔲 gap | 1 |  |
| `GetDecimal` | 🔲 gap | 1 |  |
| `GetDuration` | 🔲 gap | 1 |  |
| `GetInteger` | 🔲 gap | 1 |  |
| `GetObject` | 🔲 gap | 1 |  |
| `GetOption` | 🔲 gap | 1 |  |
| `GetText` | 🔲 gap | 1 |  |
| `GetTime` | 🔲 gap | 1 |  |
| `Keys` | 🔲 gap | 1 |  |
| `Path` | 🔲 gap | 1 |  |
| `ReadFrom` | ✅ covered | 2 |  |
| `ReadFromYaml` | 🔲 gap | 2 |  |
| `Remove` | 🔲 gap | 1 |  |
| `Replace` | 🔲 gap | 16 |  |
| `SelectToken` | ✅ covered | 1 |  |
| `Values` | 🔲 gap | 1 |  |
| `WriteTo` | ✅ covered | 2 |  |
| `WriteToYaml` | 🔲 gap | 2 |  |
| `WriteWithSecretsTo` | 🔲 gap | 2 |  |

## JsonToken  (3/11)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AsArray` | 🔲 gap | 1 |  |
| `AsObject` | 🔲 gap | 1 |  |
| `AsValue` | 🔲 gap | 1 |  |
| `Clone` | 🔲 gap | 1 |  |
| `IsArray` | 🔲 gap | 1 |  |
| `IsObject` | 🔲 gap | 1 |  |
| `IsValue` | 🔲 gap | 1 |  |
| `Path` | 🔲 gap | 1 |  |
| `ReadFrom` | ✅ covered | 2 |  |
| `SelectToken` | ✅ covered | 1 |  |
| `WriteTo` | ✅ covered | 2 |  |

## JsonValue  (3/24)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AsBigInteger` | 🔲 gap | 1 |  |
| `AsBoolean` | 🔲 gap | 1 |  |
| `AsByte` | 🔲 gap | 1 |  |
| `AsChar` | 🔲 gap | 1 |  |
| `AsCode` | 🔲 gap | 1 |  |
| `AsDate` | 🔲 gap | 1 |  |
| `AsDateTime` | 🔲 gap | 1 |  |
| `AsDecimal` | 🔲 gap | 1 |  |
| `AsDuration` | 🔲 gap | 1 |  |
| `AsInteger` | 🔲 gap | 1 |  |
| `AsOption` | 🔲 gap | 1 |  |
| `AsText` | 🔲 gap | 1 |  |
| `AsTime` | 🔲 gap | 1 |  |
| `AsToken` | 🔲 gap | 1 |  |
| `Clone` | 🔲 gap | 1 |  |
| `IsNull` | 🔲 gap | 1 |  |
| `IsUndefined` | 🔲 gap | 1 |  |
| `Path` | 🔲 gap | 1 |  |
| `ReadFrom` | ✅ covered | 2 |  |
| `SelectToken` | ✅ covered | 1 |  |
| `SetValue` | 🔲 gap | 12 |  |
| `SetValueToNull` | 🔲 gap | 1 |  |
| `SetValueToUndefined` | 🔲 gap | 1 |  |
| `WriteTo` | ✅ covered | 2 |  |

## KeyRef  (4/4)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Active` | ✅ covered | 1 |  |
| `FieldCount` | ✅ covered | 1 |  |
| `FieldIndex` | ✅ covered | 1 |  |
| `Record` | ✅ covered | 1 |  |

## Label  (0/17)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Contains` | 🔲 gap | 1 |  |
| `EndsWith` | 🔲 gap | 1 |  |
| `IndexOf` | 🔲 gap | 1 |  |
| `IndexOfAny` | 🔲 gap | 2 |  |
| `LastIndexOf` | 🔲 gap | 1 |  |
| `PadLeft` | 🔲 gap | 1 |  |
| `PadRight` | 🔲 gap | 1 |  |
| `Remove` | 🔲 gap | 1 |  |
| `Replace` | 🔲 gap | 1 |  |
| `Split` | 🔲 gap | 3 |  |
| `StartsWith` | 🔲 gap | 1 |  |
| `Substring` | 🔲 gap | 1 |  |
| `ToLower` | 🔲 gap | 1 |  |
| `ToUpper` | 🔲 gap | 1 |  |
| `Trim` | 🔲 gap | 1 |  |
| `TrimEnd` | 🔲 gap | 1 |  |
| `TrimStart` | 🔲 gap | 1 |  |

## Media  (1/7)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `ExportFile` | 🔲 gap | 1 |  |
| `ExportStream` | 🔲 gap | 1 |  |
| `FindOrphans` | 🔲 gap | 1 |  |
| `HasValue` | ✅ covered | 1 |  |
| `ImportFile` | 🔲 gap | 1 |  |
| `ImportStream` | 🔲 gap | 2 |  |
| `MediaId` | 🔲 gap | 1 |  |

## MediaSet  (0/9)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Count` | 🔲 gap | 1 |  |
| `ExportFile` | 🔲 gap | 1 |  |
| `FindOrphans` | 🔲 gap | 1 |  |
| `ImportFile` | 🔲 gap | 1 |  |
| `ImportStream` | 🔲 gap | 1 |  |
| `Insert` | 🔲 gap | 1 |  |
| `Item` | 🔲 gap | 1 |  |
| `MediaId` | 🔲 gap | 1 |  |
| `Remove` | 🔲 gap | 1 |  |

## ModuleDependencyInfo  (0/3)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Id` | 🔲 gap | 1 |  |
| `Name` | 🔲 gap | 1 |  |
| `Publisher` | 🔲 gap | 1 |  |

## ModuleInfo  (0/7)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AppVersion` | 🔲 gap | 1 |  |
| `DataVersion` | 🔲 gap | 1 |  |
| `Dependencies` | 🔲 gap | 1 |  |
| `Id` | 🔲 gap | 1 |  |
| `Name` | 🔲 gap | 1 |  |
| `PackageId` | 🔲 gap | 1 |  |
| `Publisher` | 🔲 gap | 1 |  |

## NavApp  (4/16)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `DeleteArchiveData` | 🔲 gap | 1 |  |
| `GetArchiveRecordRef` | 🔲 gap | 1 |  |
| `GetArchiveVersion` | 🔲 gap | 1 |  |
| `GetCallerCallstackModuleInfos` | ✅ covered | 1 |  |
| `GetCallerModuleInfo` | ✅ covered | 1 |  |
| `GetCurrentModuleInfo` | ✅ covered | 1 |  |
| `GetModuleInfo` | ✅ covered | 1 |  |
| `GetResource` | 🔲 gap | 1 |  |
| `GetResourceAsJson` | 🔲 gap | 1 |  |
| `GetResourceAsText` | 🔲 gap | 1 |  |
| `IsEntitled` | 🔲 gap | 1 |  |
| `IsInstalling` | 🔲 gap | 1 |  |
| `IsUnlicensed` | 🔲 gap | 1 |  |
| `ListResources` | 🔲 gap | 1 |  |
| `LoadPackageData` | 🔲 gap | 1 |  |
| `RestoreArchiveData` | 🔲 gap | 1 |  |

## Notification  (9/9)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AddAction` | ✅ covered | 2 |  |
| `GetData` | ✅ covered | 1 |  |
| `HasData` | ✅ covered | 1 |  |
| `Id` | ✅ covered | 1 |  |
| `Message` | ✅ covered | 1 |  |
| `Recall` | ✅ covered | 1 |  |
| `Scope` | ✅ covered | 1 |  |
| `Send` | ✅ covered | 1 |  |
| `SetData` | ✅ covered | 1 |  |

## NumberSequence  (6/7)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Current` | ✅ covered | 1 |  |
| `Delete` | ✅ covered | 1 |  |
| `Exists` | ✅ covered | 1 |  |
| `Insert` | ✅ covered | 1 |  |
| `Next` | ✅ covered | 1 |  |
| `Range` | 🔲 gap | 2 |  |
| `Restart` | ✅ covered | 1 |  |

## OutStream  (2/2)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Write` | ✅ covered | 23 |  |
| `WriteText` | ✅ covered | 1 |  |

## Page  (4/19)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Activate` | 🔲 gap | 1 |  |
| `CancelBackgroundTask` | 🔲 gap | 1 |  |
| `Caption` | ✅ covered | 1 |  |
| `Close` | ✅ covered | 1 |  |
| `Editable` | ✅ covered | 1 |  |
| `EnqueueBackgroundTask` | 🔲 gap | 1 |  |
| `GetBackgroundParameters` | 🔲 gap | 1 |  |
| `GetRecord` | ✅ covered | 1 |  |
| `LookupMode` | 🔲 gap | 1 |  |
| `ObjectId` | 🔲 gap | 1 |  |
| `PromptMode` | 🔲 gap | 1 |  |
| `Run` | 🔲 gap | 3 |  |
| `RunModal` | 🔲 gap | 4 |  |
| `SaveRecord` | 🔲 gap | 1 |  |
| `SetBackgroundTaskResult` | 🔲 gap | 1 |  |
| `SetRecord` | 🔲 gap | 1 |  |
| `SetSelectionFilter` | 🔲 gap | 1 |  |
| `SetTableView` | 🔲 gap | 1 |  |
| `Update` | 🔲 gap | 1 |  |

## ProductName  (0/3)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Full` | 🔲 gap | 1 |  |
| `Marketing` | 🔲 gap | 1 |  |
| `Short` | 🔲 gap | 1 |  |

## Query  (3/3)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `SaveAsCsv` | ✅ covered | 2 |  |
| `SaveAsJson` | ✅ covered | 1 |  |
| `SaveAsXml` | ✅ covered | 2 |  |

## QueryInstance  (0/15)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Close` | 🔲 gap | 1 |  |
| `ColumnCaption` | 🔲 gap | 1 |  |
| `ColumnName` | 🔲 gap | 1 |  |
| `ColumnNo` | 🔲 gap | 1 |  |
| `GetFilter` | 🔲 gap | 1 |  |
| `GetFilters` | 🔲 gap | 1 |  |
| `Open` | 🔲 gap | 1 |  |
| `Read` | 🔲 gap | 1 |  |
| `SaveAsCsv` | 🔲 gap | 2 |  |
| `SaveAsJson` | 🔲 gap | 1 |  |
| `SaveAsXml` | 🔲 gap | 2 |  |
| `SecurityFiltering` | 🔲 gap | 1 |  |
| `SetFilter` | 🔲 gap | 1 |  |
| `SetRange` | 🔲 gap | 1 |  |
| `TopNumberOfRows` | 🔲 gap | 1 |  |

## RecordId  (0/2)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `GetRecord` | 🔲 gap | 1 |  |
| `TableNo` | 🔲 gap | 1 |  |

## RecordRef  (65/75)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AddLink` | ✅ covered | 1 |  |
| `AddLoadFields` | 🔲 gap | 1 |  |
| `AreFieldsLoaded` | 🔲 gap | 1 |  |
| `Ascending` | ✅ covered | 1 |  |
| `Caption` | ✅ covered | 1 |  |
| `ChangeCompany` | ✅ covered | 1 |  |
| `ClearMarks` | ✅ covered | 1 |  |
| `Close` | ✅ covered | 1 |  |
| `Copy` | ✅ covered | 2 |  |
| `CopyLinks` | 🔲 gap | 3 |  |
| `Count` | ✅ covered | 1 |  |
| `CountApprox` | ✅ covered | 1 |  |
| `CurrentCompany` | ✅ covered | 1 |  |
| `CurrentKey` | ✅ covered | 1 |  |
| `CurrentKeyIndex` | ✅ covered | 1 |  |
| `Delete` | ✅ covered | 1 |  |
| `DeleteAll` | ✅ covered | 1 |  |
| `DeleteLink` | ✅ covered | 1 |  |
| `DeleteLinks` | ✅ covered | 1 |  |
| `Duplicate` | ✅ covered | 1 |  |
| `Field` | ✅ covered | 2 |  |
| `FieldCount` | ✅ covered | 1 |  |
| `FieldExist` | 🔲 gap | 2 |  |
| `FieldIndex` | ✅ covered | 1 |  |
| `FilterGroup` | ✅ covered | 1 |  |
| `Find` | ✅ covered | 1 |  |
| `FindFirst` | ✅ covered | 1 |  |
| `FindLast` | ✅ covered | 1 |  |
| `FindSet` | ✅ covered | 2 |  |
| `Get` | ✅ covered | 1 |  |
| `GetBySystemId` | ✅ covered | 1 |  |
| `GetFilters` | ✅ covered | 1 |  |
| `GetPosition` | ✅ covered | 1 |  |
| `GetTable` | ✅ covered | 1 |  |
| `GetView` | ✅ covered | 1 |  |
| `HasFilter` | ✅ covered | 1 |  |
| `HasLinks` | ✅ covered | 1 |  |
| `Init` | ✅ covered | 1 |  |
| `Insert` | ✅ covered | 3 |  |
| `IsDirty` | 🔲 gap | 1 |  |
| `IsEmpty` | ✅ covered | 1 |  |
| `IsTemporary` | ✅ covered | 1 |  |
| `KeyCount` | ✅ covered | 1 |  |
| `KeyIndex` | ✅ covered | 1 |  |
| `LoadFields` | 🔲 gap | 1 |  |
| `LockTable` | ✅ covered | 1 |  |
| `Mark` | ✅ covered | 1 |  |
| `MarkedOnly` | ✅ covered | 1 |  |
| `Modify` | ✅ covered | 1 |  |
| `Name` | ✅ covered | 1 |  |
| `Next` | ✅ covered | 1 |  |
| `Number` | ✅ covered | 1 |  |
| `Open` | ✅ covered | 1 |  |
| `ReadConsistency` | 🔲 gap | 1 |  |
| `ReadIsolation` | ✅ covered | 1 |  |
| `ReadPermission` | ✅ covered | 1 |  |
| `RecordId` | ✅ covered | 1 |  |
| `RecordLevelLocking` | 🔲 gap | 1 |  |
| `Rename` | ✅ covered | 1 |  |
| `Reset` | ✅ covered | 1 |  |
| `SecurityFiltering` | 🔲 gap | 1 |  |
| `SetAutoCalcFields` | ✅ covered | 1 |  |
| `SetLoadFields` | ✅ covered | 1 |  |
| `SetPermissionFilter` | ✅ covered | 1 |  |
| `SetPosition` | ✅ covered | 1 |  |
| `SetRecFilter` | ✅ covered | 1 |  |
| `SetTable` | ✅ covered | 2 |  |
| `SetView` | ✅ covered | 1 |  |
| `SystemCreatedAtNo` | ✅ covered | 1 |  |
| `SystemCreatedByNo` | ✅ covered | 1 |  |
| `SystemIdNo` | ✅ covered | 1 |  |
| `SystemModifiedAtNo` | ✅ covered | 1 |  |
| `SystemModifiedByNo` | ✅ covered | 1 |  |
| `Truncate` | 🔲 gap | 1 |  |
| `WritePermission` | ✅ covered | 1 |  |

## Report  (0/18)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `DefaultLayout` | 🔲 gap | 1 |  |
| `ExcelLayout` | 🔲 gap | 1 |  |
| `Execute` | 🔲 gap | 1 |  |
| `GetSubstituteReportId` | 🔲 gap | 1 |  |
| `Print` | 🔲 gap | 1 |  |
| `RdlcLayout` | 🔲 gap | 1 |  |
| `Run` | 🔲 gap | 1 |  |
| `RunModal` | 🔲 gap | 1 |  |
| `RunRequestPage` | 🔲 gap | 1 |  |
| `SaveAs` | 🔲 gap | 1 |  |
| `SaveAsExcel` | 🔲 gap | 1 |  |
| `SaveAsHtml` | 🔲 gap | 1 |  |
| `SaveAsPdf` | 🔲 gap | 1 |  |
| `SaveAsWord` | 🔲 gap | 1 |  |
| `SaveAsXml` | 🔲 gap | 1 |  |
| `ValidateAndPrepareLayout` | 🔲 gap | 1 |  |
| `WordLayout` | 🔲 gap | 1 |  |
| `WordXmlPart` | 🔲 gap | 1 |  |

## ReportInstance  (0/36)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Break` | 🔲 gap | 1 |  |
| `CreateTotals` | 🔲 gap | 2 |  |
| `DefaultLayout` | 🔲 gap | 1 |  |
| `ExcelLayout` | 🔲 gap | 1 |  |
| `Execute` | 🔲 gap | 1 |  |
| `FormatRegion` | 🔲 gap | 1 |  |
| `IsReadOnly` | 🔲 gap | 1 |  |
| `Language` | 🔲 gap | 1 |  |
| `NewPage` | 🔲 gap | 1 |  |
| `NewPagePerRecord` | 🔲 gap | 1 |  |
| `ObjectId` | 🔲 gap | 1 |  |
| `PageNo` | 🔲 gap | 1 |  |
| `PaperSource` | 🔲 gap | 1 |  |
| `Preview` | 🔲 gap | 1 |  |
| `Print` | 🔲 gap | 1 |  |
| `PrintOnlyIfDetail` | 🔲 gap | 1 |  |
| `Quit` | 🔲 gap | 1 |  |
| `RDLCLayout` | 🔲 gap | 1 |  |
| `Run` | 🔲 gap | 1 |  |
| `RunModal` | 🔲 gap | 1 |  |
| `RunRequestPage` | 🔲 gap | 1 |  |
| `SaveAs` | 🔲 gap | 1 |  |
| `SaveAsExcel` | 🔲 gap | 1 |  |
| `SaveAsHtml` | 🔲 gap | 1 |  |
| `SaveAsPdf` | 🔲 gap | 1 |  |
| `SaveAsWord` | 🔲 gap | 1 |  |
| `SaveAsXml` | 🔲 gap | 1 |  |
| `SetTableView` | 🔲 gap | 1 |  |
| `ShowOutput` | 🔲 gap | 2 |  |
| `Skip` | 🔲 gap | 1 |  |
| `TargetFormat` | 🔲 gap | 1 |  |
| `TotalsCausedBy` | 🔲 gap | 1 |  |
| `UseRequestPage` | 🔲 gap | 1 |  |
| `ValidateAndPrepareLayout` | 🔲 gap | 1 |  |
| `WordLayout` | 🔲 gap | 1 |  |
| `WordXmlPart` | 🔲 gap | 1 |  |

## RequestPage  (0/9)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Activate` | 🔲 gap | 1 |  |
| `Caption` | 🔲 gap | 1 |  |
| `Close` | 🔲 gap | 1 |  |
| `Editable` | 🔲 gap | 1 |  |
| `LookupMode` | 🔲 gap | 1 |  |
| `ObjectId` | 🔲 gap | 1 |  |
| `SaveRecord` | 🔲 gap | 1 |  |
| `SetSelectionFilter` | 🔲 gap | 1 |  |
| `Update` | 🔲 gap | 1 |  |

## SecretText  (0/3)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `IsEmpty` | 🔲 gap | 1 |  |
| `SecretStrSubstNo` | 🔲 gap | 1 |  |
| `Unwrap` | 🔲 gap | 1 |  |

## Session  (2/19)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `ApplicationArea` | 🔲 gap | 1 |  |
| `ApplicationIdentifier` | 🔲 gap | 1 |  |
| `BindSubscription` | 🔲 gap | 1 |  |
| `CurrentClientType` | 🔲 gap | 1 |  |
| `CurrentExecutionMode` | 🔲 gap | 1 |  |
| `DefaultClientType` | 🔲 gap | 1 |  |
| `EnableVerboseTelemetry` | 🔲 gap | 1 |  |
| `GetCurrentModuleExecutionContext` | 🔲 gap | 1 |  |
| `GetExecutionContext` | 🔲 gap | 1 |  |
| `GetModuleExecutionContext` | 🔲 gap | 1 |  |
| `IsSessionActive` | ✅ covered | 1 |  |
| `LogAuditMessage` | 🔲 gap | 1 |  |
| `LogMessage` | 🔲 gap | 2 |  |
| `LogSecurityAudit` | 🔲 gap | 1 |  |
| `SendTraceTag` | 🔲 gap | 1 |  |
| `SetDocumentServiceToken` | 🔲 gap | 1 |  |
| `StartSession` | ❌ not-possible | 3 |  |
| `StopSession` | ✅ covered | 1 |  |
| `UnbindSubscription` | 🔲 gap | 1 |  |

## SessionInformation  (0/4)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AITokensUsed` | 🔲 gap | 1 |  |
| `Callstack` | 🔲 gap | 1 |  |
| `SqlRowsRead` | 🔲 gap | 1 |  |
| `SqlStatementsExecuted` | 🔲 gap | 1 |  |

## SessionSettings  (0/9)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Company` | 🔲 gap | 1 |  |
| `Init` | 🔲 gap | 1 |  |
| `LanguageId` | 🔲 gap | 1 |  |
| `LocaleId` | 🔲 gap | 1 |  |
| `ProfileAppId` | 🔲 gap | 1 |  |
| `ProfileId` | 🔲 gap | 1 |  |
| `ProfileSystemScope` | 🔲 gap | 1 |  |
| `RequestSessionUpdate` | 🔲 gap | 1 |  |
| `TimeZone` | 🔲 gap | 1 |  |

## System  (0/71)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Abs` | 🔲 gap | 1 |  |
| `ApplicationPath` | 🔲 gap | 1 |  |
| `ArrayLen` | 🔲 gap | 1 |  |
| `CalcDate` | 🔲 gap | 2 |  |
| `CanLoadType` | 🔲 gap | 1 |  |
| `CaptionClassTranslate` | 🔲 gap | 1 |  |
| `Clear` | 🔲 gap | 3 |  |
| `ClearAll` | 🔲 gap | 1 |  |
| `ClearCollectedErrors` | 🔲 gap | 1 |  |
| `ClearLastError` | 🔲 gap | 1 |  |
| `ClosingDate` | 🔲 gap | 1 |  |
| `CodeCoverageInclude` | 🔲 gap | 1 |  |
| `CodeCoverageLoad` | 🔲 gap | 1 |  |
| `CodeCoverageLog` | 🔲 gap | 1 |  |
| `CodeCoverageRefresh` | 🔲 gap | 1 |  |
| `CompressArray` | 🔲 gap | 1 |  |
| `CopyArray` | 🔲 gap | 1 |  |
| `CopyStream` | 🔲 gap | 1 |  |
| `CreateDateTime` | 🔲 gap | 1 |  |
| `CreateEncryptionKey` | 🔲 gap | 1 |  |
| `CreateGuid` | 🔲 gap | 1 |  |
| `CurrentDateTime` | 🔲 gap | 1 |  |
| `Date2DMY` | 🔲 gap | 1 |  |
| `Date2DWY` | 🔲 gap | 1 |  |
| `DaTi2Variant` | 🔲 gap | 1 |  |
| `Decrypt` | 🔲 gap | 1 |  |
| `DeleteEncryptionKey` | 🔲 gap | 1 |  |
| `DMY2Date` | 🔲 gap | 1 |  |
| `DT2Date` | 🔲 gap | 1 |  |
| `DT2Time` | 🔲 gap | 1 |  |
| `DWY2Date` | 🔲 gap | 1 |  |
| `Encrypt` | 🔲 gap | 1 |  |
| `EncryptionEnabled` | 🔲 gap | 1 |  |
| `EncryptionKeyExists` | 🔲 gap | 1 |  |
| `Evaluate` | 🔲 gap | 1 |  |
| `ExportEncryptionKey` | 🔲 gap | 1 |  |
| `ExportObjects` | 🔲 gap | 1 |  |
| `Format` | 🔲 gap | 2 |  |
| `GetCollectedErrors` | 🔲 gap | 1 |  |
| `GetDocumentUrl` | 🔲 gap | 1 |  |
| `GetDotNetType` | 🔲 gap | 1 |  |
| `GetLastErrorCallStack` | 🔲 gap | 1 |  |
| `GetLastErrorCode` | 🔲 gap | 1 |  |
| `GetLastErrorObject` | 🔲 gap | 1 |  |
| `GetLastErrorText` | 🔲 gap | 2 |  |
| `GetUrl` | 🔲 gap | 4 |  |
| `GlobalLanguage` | 🔲 gap | 1 |  |
| `GuiAllowed` | 🔲 gap | 1 |  |
| `HasCollectedErrors` | 🔲 gap | 1 |  |
| `Hyperlink` | 🔲 gap | 1 |  |
| `ImportEncryptionKey` | 🔲 gap | 1 |  |
| `ImportObjects` | 🔲 gap | 1 |  |
| `ImportStreamWithUrlAccess` | 🔲 gap | 1 |  |
| `IsCollectingErrors` | 🔲 gap | 1 |  |
| `IsNull` | 🔲 gap | 1 |  |
| `IsNullGuid` | 🔲 gap | 1 |  |
| `IsServiceTier` | 🔲 gap | 1 |  |
| `NormalDate` | 🔲 gap | 1 |  |
| `Power` | 🔲 gap | 1 |  |
| `Random` | 🔲 gap | 1 |  |
| `Randomize` | 🔲 gap | 1 |  |
| `Round` | 🔲 gap | 1 |  |
| `RoundDateTime` | 🔲 gap | 1 |  |
| `Sleep` | 🔲 gap | 1 |  |
| `TemporaryPath` | 🔲 gap | 1 |  |
| `Time` | 🔲 gap | 1 |  |
| `Today` | 🔲 gap | 1 |  |
| `Variant2Date` | 🔲 gap | 1 |  |
| `Variant2Time` | 🔲 gap | 1 |  |
| `WindowsLanguage` | 🔲 gap | 1 |  |
| `WorkDate` | 🔲 gap | 1 |  |

## Table  (0/80)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AddLink` | 🔲 gap | 1 |  |
| `AddLoadFields` | 🔲 gap | 1 |  |
| `AreFieldsLoaded` | 🔲 gap | 1 |  |
| `Ascending` | 🔲 gap | 1 |  |
| `CalcFields` | 🔲 gap | 1 |  |
| `CalcSums` | 🔲 gap | 1 |  |
| `ChangeCompany` | 🔲 gap | 1 |  |
| `ClearMarks` | 🔲 gap | 1 |  |
| `Consistent` | 🔲 gap | 1 |  |
| `Copy` | 🔲 gap | 1 |  |
| `CopyFilter` | 🔲 gap | 1 |  |
| `CopyFilters` | 🔲 gap | 1 |  |
| `CopyLinks` | 🔲 gap | 2 |  |
| `Count` | 🔲 gap | 1 |  |
| `CountApprox` | 🔲 gap | 1 |  |
| `CurrentCompany` | 🔲 gap | 1 |  |
| `CurrentKey` | 🔲 gap | 1 |  |
| `Delete` | 🔲 gap | 1 |  |
| `DeleteAll` | 🔲 gap | 1 |  |
| `DeleteLink` | 🔲 gap | 1 |  |
| `DeleteLinks` | 🔲 gap | 1 |  |
| `FieldActive` | 🔲 gap | 1 |  |
| `FieldCaption` | 🔲 gap | 1 |  |
| `FieldError` | 🔲 gap | 2 |  |
| `FieldName` | 🔲 gap | 1 |  |
| `FieldNo` | 🔲 gap | 1 |  |
| `FilterGroup` | 🔲 gap | 1 |  |
| `Find` | 🔲 gap | 1 |  |
| `FindFirst` | 🔲 gap | 1 |  |
| `FindLast` | 🔲 gap | 1 |  |
| `FindSet` | 🔲 gap | 2 |  |
| `Get` | 🔲 gap | 1 |  |
| `GetAscending` | 🔲 gap | 1 |  |
| `GetBySystemId` | 🔲 gap | 1 |  |
| `GetFilter` | 🔲 gap | 1 |  |
| `GetFilters` | 🔲 gap | 1 |  |
| `GetPosition` | 🔲 gap | 1 |  |
| `GetRangeMax` | 🔲 gap | 1 |  |
| `GetRangeMin` | 🔲 gap | 1 |  |
| `GetView` | 🔲 gap | 1 |  |
| `HasFilter` | 🔲 gap | 1 |  |
| `HasLinks` | 🔲 gap | 1 |  |
| `Init` | 🔲 gap | 1 |  |
| `Insert` | 🔲 gap | 3 |  |
| `IsEmpty` | 🔲 gap | 1 |  |
| `IsTemporary` | 🔲 gap | 1 |  |
| `LoadFields` | 🔲 gap | 1 |  |
| `LockTable` | 🔲 gap | 1 |  |
| `Mark` | 🔲 gap | 1 |  |
| `MarkedOnly` | 🔲 gap | 1 |  |
| `Modify` | 🔲 gap | 1 |  |
| `ModifyAll` | 🔲 gap | 1 |  |
| `Next` | 🔲 gap | 1 |  |
| `ReadConsistency` | 🔲 gap | 1 |  |
| `ReadIsolation` | 🔲 gap | 1 |  |
| `ReadPermission` | 🔲 gap | 1 |  |
| `RecordId` | 🔲 gap | 1 |  |
| `RecordLevelLocking` | 🔲 gap | 1 |  |
| `Relation` | 🔲 gap | 1 |  |
| `Rename` | 🔲 gap | 1 |  |
| `Reset` | 🔲 gap | 1 |  |
| `SecurityFiltering` | 🔲 gap | 1 |  |
| `SetAscending` | 🔲 gap | 1 |  |
| `SetAutoCalcFields` | 🔲 gap | 1 |  |
| `SetBaseLoadFields` | 🔲 gap | 1 |  |
| `SetCurrentKey` | 🔲 gap | 1 |  |
| `SetFilter` | 🔲 gap | 1 |  |
| `SetLoadFields` | 🔲 gap | 1 |  |
| `SetPermissionFilter` | 🔲 gap | 1 |  |
| `SetPosition` | 🔲 gap | 1 |  |
| `SetRange` | 🔲 gap | 1 |  |
| `SetRecFilter` | 🔲 gap | 1 |  |
| `SetView` | 🔲 gap | 1 |  |
| `TableCaption` | 🔲 gap | 1 |  |
| `TableName` | 🔲 gap | 1 |  |
| `TestField` | 🔲 gap | 26 |  |
| `TransferFields` | 🔲 gap | 2 |  |
| `Truncate` | 🔲 gap | 1 |  |
| `Validate` | 🔲 gap | 1 |  |
| `WritePermission` | 🔲 gap | 1 |  |

## TaskScheduler  (0/5)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `CancelTask` | ❌ not-possible | 1 |  |
| `CanCreateTask` | ❌ not-possible | 1 |  |
| `CreateTask` | ❌ not-possible | 2 |  |
| `SetTaskReady` | ❌ not-possible | 1 |  |
| `TaskExists` | ❌ not-possible | 1 |  |

## TestAction  (0/3)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Enabled` | 🔲 gap | 1 |  |
| `Invoke` | 🔲 gap | 1 |  |
| `Visible` | 🔲 gap | 1 |  |

## TestField  (0/24)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Activate` | 🔲 gap | 1 |  |
| `AsBoolean` | 🔲 gap | 1 |  |
| `AsDate` | 🔲 gap | 1 |  |
| `AsDateTime` | 🔲 gap | 1 |  |
| `AsDecimal` | 🔲 gap | 1 |  |
| `AsInteger` | 🔲 gap | 1 |  |
| `AssertEquals` | 🔲 gap | 1 |  |
| `AssistEdit` | 🔲 gap | 1 |  |
| `AsTime` | 🔲 gap | 1 |  |
| `Caption` | 🔲 gap | 1 |  |
| `Drilldown` | 🔲 gap | 1 |  |
| `Editable` | 🔲 gap | 1 |  |
| `Enabled` | 🔲 gap | 1 |  |
| `GetOption` | 🔲 gap | 1 |  |
| `GetValidationError` | 🔲 gap | 1 |  |
| `HideValue` | 🔲 gap | 1 |  |
| `Invoke` | 🔲 gap | 1 |  |
| `Lookup` | 🔲 gap | 2 |  |
| `OptionCount` | 🔲 gap | 1 |  |
| `SetValue` | 🔲 gap | 1 |  |
| `ShowMandatory` | 🔲 gap | 1 |  |
| `ValidationErrorCount` | 🔲 gap | 1 |  |
| `Value` | 🔲 gap | 1 |  |
| `Visible` | 🔲 gap | 1 |  |

## TestFilter  (0/5)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Ascending` | 🔲 gap | 1 |  |
| `CurrentKey` | 🔲 gap | 1 |  |
| `GetFilter` | 🔲 gap | 1 |  |
| `SetCurrentKey` | 🔲 gap | 1 |  |
| `SetFilter` | 🔲 gap | 1 |  |

## TestHttpRequestMessage  (0/4)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `HasSecretUri` | 🔲 gap | 1 |  |
| `Path` | 🔲 gap | 1 |  |
| `QueryParameters` | 🔲 gap | 1 |  |
| `RequestType` | 🔲 gap | 1 |  |

## TestHttpResponseMessage  (0/6)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Content` | 🔲 gap | 1 |  |
| `Headers` | 🔲 gap | 1 |  |
| `HttpStatusCode` | 🔲 gap | 1 |  |
| `IsBlockedByEnvironment` | 🔲 gap | 1 |  |
| `IsSuccessfulRequest` | 🔲 gap | 1 |  |
| `ReasonPhrase` | 🔲 gap | 1 |  |

## TestPage  (0/30)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Cancel` | 🔲 gap | 1 |  |
| `Caption` | 🔲 gap | 1 |  |
| `Close` | 🔲 gap | 1 |  |
| `Edit` | 🔲 gap | 1 |  |
| `Editable` | 🔲 gap | 1 |  |
| `Expand` | 🔲 gap | 1 |  |
| `FindFirstField` | 🔲 gap | 1 |  |
| `FindNextField` | 🔲 gap | 1 |  |
| `FindPreviousField` | 🔲 gap | 1 |  |
| `First` | 🔲 gap | 1 |  |
| `GetField` | 🔲 gap | 1 |  |
| `GetValidationError` | 🔲 gap | 1 |  |
| `GoToKey` | 🔲 gap | 1 |  |
| `GoToRecord` | 🔲 gap | 1 |  |
| `IsExpanded` | 🔲 gap | 1 |  |
| `Last` | 🔲 gap | 1 |  |
| `New` | 🔲 gap | 1 |  |
| `Next` | 🔲 gap | 1 |  |
| `No` | 🔲 gap | 1 |  |
| `OK` | 🔲 gap | 1 |  |
| `OpenEdit` | 🔲 gap | 1 |  |
| `OpenNew` | 🔲 gap | 1 |  |
| `OpenView` | 🔲 gap | 1 |  |
| `Prev` | 🔲 gap | 1 |  |
| `Previous` | 🔲 gap | 1 |  |
| `RunPageBackgroundTask` | 🔲 gap | 1 |  |
| `Trap` | 🔲 gap | 1 |  |
| `ValidationErrorCount` | 🔲 gap | 1 |  |
| `View` | 🔲 gap | 1 |  |
| `Yes` | 🔲 gap | 1 |  |

## TestPart  (0/20)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Caption` | 🔲 gap | 1 |  |
| `Editable` | 🔲 gap | 1 |  |
| `Enabled` | 🔲 gap | 1 |  |
| `Expand` | 🔲 gap | 1 |  |
| `FindFirstField` | 🔲 gap | 1 |  |
| `FindNextField` | 🔲 gap | 1 |  |
| `FindPreviousField` | 🔲 gap | 1 |  |
| `First` | 🔲 gap | 1 |  |
| `GetField` | 🔲 gap | 1 |  |
| `GetValidationError` | 🔲 gap | 1 |  |
| `GoToKey` | 🔲 gap | 1 |  |
| `GoToRecord` | 🔲 gap | 1 |  |
| `IsExpanded` | 🔲 gap | 1 |  |
| `Last` | 🔲 gap | 1 |  |
| `New` | 🔲 gap | 1 |  |
| `Next` | 🔲 gap | 1 |  |
| `Prev` | 🔲 gap | 1 |  |
| `Previous` | 🔲 gap | 1 |  |
| `ValidationErrorCount` | 🔲 gap | 1 |  |
| `Visible` | 🔲 gap | 1 |  |

## TestRequestPage  (0/25)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Cancel` | 🔲 gap | 1 |  |
| `Caption` | 🔲 gap | 1 |  |
| `Editable` | 🔲 gap | 1 |  |
| `Expand` | 🔲 gap | 1 |  |
| `FindFirstField` | 🔲 gap | 1 |  |
| `FindNextField` | 🔲 gap | 1 |  |
| `FindPreviousField` | 🔲 gap | 1 |  |
| `First` | 🔲 gap | 1 |  |
| `GetValidationError` | 🔲 gap | 1 |  |
| `GoToKey` | 🔲 gap | 1 |  |
| `GoToRecord` | 🔲 gap | 1 |  |
| `IsExpanded` | 🔲 gap | 1 |  |
| `Last` | 🔲 gap | 1 |  |
| `New` | 🔲 gap | 1 |  |
| `Next` | 🔲 gap | 1 |  |
| `OK` | 🔲 gap | 1 |  |
| `Preview` | 🔲 gap | 1 |  |
| `Previous` | 🔲 gap | 1 |  |
| `Print` | 🔲 gap | 1 |  |
| `SaveAsExcel` | 🔲 gap | 1 |  |
| `SaveAsPdf` | 🔲 gap | 1 |  |
| `SaveAsWord` | 🔲 gap | 1 |  |
| `SaveAsXml` | 🔲 gap | 1 |  |
| `Schedule` | 🔲 gap | 1 |  |
| `ValidationErrorCount` | 🔲 gap | 1 |  |

## Text  (0/32)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Contains` | 🔲 gap | 1 |  |
| `ConvertStr` | 🔲 gap | 1 |  |
| `CopyStr` | 🔲 gap | 1 |  |
| `DelChr` | 🔲 gap | 1 |  |
| `DelStr` | 🔲 gap | 1 |  |
| `EndsWith` | 🔲 gap | 1 |  |
| `IncStr` | 🔲 gap | 2 |  |
| `IndexOf` | 🔲 gap | 1 |  |
| `IndexOfAny` | 🔲 gap | 2 |  |
| `InsStr` | 🔲 gap | 1 |  |
| `LastIndexOf` | 🔲 gap | 1 |  |
| `LowerCase` | 🔲 gap | 1 |  |
| `MaxStrLen` | 🔲 gap | 2 |  |
| `PadLeft` | 🔲 gap | 1 |  |
| `PadRight` | 🔲 gap | 1 |  |
| `PadStr` | 🔲 gap | 1 |  |
| `Remove` | 🔲 gap | 1 |  |
| `Replace` | 🔲 gap | 1 |  |
| `SelectStr` | 🔲 gap | 1 |  |
| `Split` | 🔲 gap | 3 |  |
| `StartsWith` | 🔲 gap | 1 |  |
| `StrCheckSum` | 🔲 gap | 1 |  |
| `StrLen` | 🔲 gap | 1 |  |
| `StrPos` | 🔲 gap | 1 |  |
| `StrSubstNo` | 🔲 gap | 1 |  |
| `Substring` | 🔲 gap | 1 |  |
| `ToLower` | 🔲 gap | 1 |  |
| `ToUpper` | 🔲 gap | 1 |  |
| `Trim` | 🔲 gap | 1 |  |
| `TrimEnd` | 🔲 gap | 1 |  |
| `TrimStart` | 🔲 gap | 1 |  |
| `UpperCase` | 🔲 gap | 1 |  |

## TextBuilder  (3/11)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Append` | ✅ covered | 1 |  |
| `AppendLine` | ✅ covered | 1 |  |
| `Capacity` | 🔲 gap | 1 |  |
| `Clear` | 🔲 gap | 1 |  |
| `EnsureCapacity` | 🔲 gap | 1 |  |
| `Insert` | 🔲 gap | 1 |  |
| `Length` | 🔲 gap | 1 |  |
| `MaxCapacity` | 🔲 gap | 1 |  |
| `Remove` | 🔲 gap | 1 |  |
| `Replace` | 🔲 gap | 2 |  |
| `ToText` | ✅ covered | 2 |  |

## TextConst  (0/17)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Contains` | 🔲 gap | 1 |  |
| `EndsWith` | 🔲 gap | 1 |  |
| `IndexOf` | 🔲 gap | 1 |  |
| `IndexOfAny` | 🔲 gap | 2 |  |
| `LastIndexOf` | 🔲 gap | 1 |  |
| `PadLeft` | 🔲 gap | 1 |  |
| `PadRight` | 🔲 gap | 1 |  |
| `Remove` | 🔲 gap | 1 |  |
| `Replace` | 🔲 gap | 1 |  |
| `Split` | 🔲 gap | 3 |  |
| `StartsWith` | 🔲 gap | 1 |  |
| `Substring` | 🔲 gap | 1 |  |
| `ToLower` | 🔲 gap | 1 |  |
| `ToUpper` | 🔲 gap | 1 |  |
| `Trim` | 🔲 gap | 1 |  |
| `TrimEnd` | 🔲 gap | 1 |  |
| `TrimStart` | 🔲 gap | 1 |  |

## Time  (0/5)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Hour` | 🔲 gap | 1 |  |
| `Millisecond` | 🔲 gap | 1 |  |
| `Minute` | 🔲 gap | 1 |  |
| `Second` | 🔲 gap | 1 |  |
| `ToText` | 🔲 gap | 1 |  |

## Variant  (19/67)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `IsAction` | 🔲 gap | 1 |  |
| `IsAutomation` | 🔲 gap | 1 |  |
| `IsBigInteger` | ✅ covered | 1 |  |
| `IsBinary` | 🔲 gap | 1 |  |
| `IsBoolean` | ✅ covered | 1 |  |
| `IsByte` | ✅ covered | 1 |  |
| `IsChar` | ✅ covered | 1 |  |
| `IsClientType` | 🔲 gap | 1 |  |
| `IsCode` | ✅ covered | 1 |  |
| `IsCodeunit` | 🔲 gap | 1 |  |
| `IsDataClassification` | 🔲 gap | 1 |  |
| `IsDataClassificationType` | 🔲 gap | 1 |  |
| `IsDate` | ✅ covered | 1 |  |
| `IsDateFormula` | ✅ covered | 1 |  |
| `IsDateTime` | ✅ covered | 1 |  |
| `IsDecimal` | ✅ covered | 1 |  |
| `IsDefaultLayout` | 🔲 gap | 1 |  |
| `IsDictionary` | 🔲 gap | 1 |  |
| `IsDotNet` | 🔲 gap | 1 |  |
| `IsDuration` | ✅ covered | 1 |  |
| `IsExecutionMode` | 🔲 gap | 1 |  |
| `IsFieldRef` | ✅ covered | 1 |  |
| `IsFile` | 🔲 gap | 1 |  |
| `IsFilterPageBuilder` | 🔲 gap | 1 |  |
| `IsGuid` | ✅ covered | 1 |  |
| `IsInStream` | 🔲 gap | 1 |  |
| `IsInteger` | ✅ covered | 1 |  |
| `IsJsonArray` | 🔲 gap | 1 |  |
| `IsJsonObject` | 🔲 gap | 1 |  |
| `IsJsonToken` | 🔲 gap | 1 |  |
| `IsJsonValue` | 🔲 gap | 1 |  |
| `IsList` | 🔲 gap | 1 |  |
| `IsNotification` | 🔲 gap | 1 |  |
| `IsObjectType` | 🔲 gap | 1 |  |
| `IsOption` | ✅ covered | 1 |  |
| `IsOutStream` | 🔲 gap | 1 |  |
| `IsPromptMode` | 🔲 gap | 1 |  |
| `IsRecord` | ✅ covered | 1 |  |
| `IsRecordId` | ✅ covered | 1 |  |
| `IsRecordRef` | ✅ covered | 1 |  |
| `IsReportFormat` | 🔲 gap | 1 |  |
| `IsSecurityFiltering` | 🔲 gap | 1 |  |
| `IsTableConnectionType` | 🔲 gap | 1 |  |
| `IsTestPermissions` | 🔲 gap | 1 |  |
| `IsText` | ✅ covered | 1 |  |
| `IsTextBuilder` | 🔲 gap | 1 |  |
| `IsTextConstant` | 🔲 gap | 1 |  |
| `IsTextEncoding` | 🔲 gap | 1 |  |
| `IsTime` | ✅ covered | 1 |  |
| `IsTransactionType` | 🔲 gap | 1 |  |
| `IsWideChar` | 🔲 gap | 1 |  |
| `IsXmlAttribute` | 🔲 gap | 1 |  |
| `IsXmlAttributeCollection` | 🔲 gap | 1 |  |
| `IsXmlCData` | 🔲 gap | 1 |  |
| `IsXmlComment` | 🔲 gap | 1 |  |
| `IsXmlDeclaration` | 🔲 gap | 1 |  |
| `IsXmlDocument` | 🔲 gap | 1 |  |
| `IsXmlDocumentType` | 🔲 gap | 1 |  |
| `IsXmlElement` | 🔲 gap | 1 |  |
| `IsXmlNamespaceManager` | 🔲 gap | 1 |  |
| `IsXmlNameTable` | 🔲 gap | 1 |  |
| `IsXmlNode` | 🔲 gap | 1 |  |
| `IsXmlNodeList` | 🔲 gap | 1 |  |
| `IsXmlProcessingInstruction` | 🔲 gap | 1 |  |
| `IsXmlReadOptions` | 🔲 gap | 1 |  |
| `IsXmlText` | 🔲 gap | 1 |  |
| `IsXmlWriteOptions` | 🔲 gap | 1 |  |

## Version  (0/6)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Build` | 🔲 gap | 1 |  |
| `Create` | 🔲 gap | 2 |  |
| `Major` | 🔲 gap | 1 |  |
| `Minor` | 🔲 gap | 1 |  |
| `Revision` | 🔲 gap | 1 |  |
| `ToText` | 🔲 gap | 1 |  |

## WebServiceActionContext  (0/7)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AddEntityKey` | 🔲 gap | 1 |  |
| `GetObjectId` | 🔲 gap | 1 |  |
| `GetObjectType` | 🔲 gap | 1 |  |
| `GetResultCode` | 🔲 gap | 1 |  |
| `SetObjectId` | 🔲 gap | 1 |  |
| `SetObjectType` | 🔲 gap | 1 |  |
| `SetResultCode` | 🔲 gap | 1 |  |

## XmlAttribute  (0/18)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AddAfterSelf` | 🔲 gap | 1 |  |
| `AddBeforeSelf` | 🔲 gap | 1 |  |
| `AsXmlNode` | 🔲 gap | 1 |  |
| `Create` | 🔲 gap | 2 |  |
| `CreateNamespaceDeclaration` | 🔲 gap | 1 |  |
| `GetDocument` | 🔲 gap | 1 |  |
| `GetParent` | 🔲 gap | 1 |  |
| `IsNamespaceDeclaration` | 🔲 gap | 1 |  |
| `LocalName` | 🔲 gap | 1 |  |
| `Name` | 🔲 gap | 1 |  |
| `NamespacePrefix` | 🔲 gap | 1 |  |
| `NamespaceUri` | 🔲 gap | 1 |  |
| `Remove` | 🔲 gap | 1 |  |
| `ReplaceWith` | 🔲 gap | 1 |  |
| `SelectNodes` | 🔲 gap | 2 |  |
| `SelectSingleNode` | 🔲 gap | 2 |  |
| `Value` | 🔲 gap | 1 |  |
| `WriteTo` | 🔲 gap | 4 |  |

## XmlAttributeCollection  (0/5)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Count` | 🔲 gap | 1 |  |
| `Get` | 🔲 gap | 3 |  |
| `Remove` | 🔲 gap | 3 |  |
| `RemoveAll` | 🔲 gap | 1 |  |
| `Set` | 🔲 gap | 2 |  |

## XmlCData  (0/12)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AddAfterSelf` | 🔲 gap | 1 |  |
| `AddBeforeSelf` | 🔲 gap | 1 |  |
| `AsXmlNode` | 🔲 gap | 1 |  |
| `Create` | 🔲 gap | 1 |  |
| `GetDocument` | 🔲 gap | 1 |  |
| `GetParent` | 🔲 gap | 1 |  |
| `Remove` | 🔲 gap | 1 |  |
| `ReplaceWith` | 🔲 gap | 1 |  |
| `SelectNodes` | 🔲 gap | 2 |  |
| `SelectSingleNode` | 🔲 gap | 2 |  |
| `Value` | 🔲 gap | 1 |  |
| `WriteTo` | 🔲 gap | 4 |  |

## XmlComment  (0/12)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AddAfterSelf` | 🔲 gap | 1 |  |
| `AddBeforeSelf` | 🔲 gap | 1 |  |
| `AsXmlNode` | 🔲 gap | 1 |  |
| `Create` | 🔲 gap | 1 |  |
| `GetDocument` | 🔲 gap | 1 |  |
| `GetParent` | 🔲 gap | 1 |  |
| `Remove` | 🔲 gap | 1 |  |
| `ReplaceWith` | 🔲 gap | 1 |  |
| `SelectNodes` | 🔲 gap | 2 |  |
| `SelectSingleNode` | 🔲 gap | 2 |  |
| `Value` | 🔲 gap | 1 |  |
| `WriteTo` | 🔲 gap | 4 |  |

## XmlDeclaration  (0/14)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AddAfterSelf` | 🔲 gap | 1 |  |
| `AddBeforeSelf` | 🔲 gap | 1 |  |
| `AsXmlNode` | 🔲 gap | 1 |  |
| `Create` | 🔲 gap | 1 |  |
| `Encoding` | 🔲 gap | 1 |  |
| `GetDocument` | 🔲 gap | 1 |  |
| `GetParent` | 🔲 gap | 1 |  |
| `Remove` | 🔲 gap | 1 |  |
| `ReplaceWith` | 🔲 gap | 1 |  |
| `SelectNodes` | 🔲 gap | 2 |  |
| `SelectSingleNode` | 🔲 gap | 2 |  |
| `Standalone` | 🔲 gap | 1 |  |
| `Version` | 🔲 gap | 1 |  |
| `WriteTo` | 🔲 gap | 4 |  |

## XmlDocument  (0/25)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Add` | 🔲 gap | 1 |  |
| `AddAfterSelf` | 🔲 gap | 1 |  |
| `AddBeforeSelf` | 🔲 gap | 1 |  |
| `AddFirst` | 🔲 gap | 1 |  |
| `AsXmlNode` | 🔲 gap | 1 |  |
| `Create` | 🔲 gap | 2 |  |
| `GetChildElements` | 🔲 gap | 3 |  |
| `GetChildNodes` | 🔲 gap | 1 |  |
| `GetDeclaration` | 🔲 gap | 1 |  |
| `GetDescendantElements` | 🔲 gap | 3 |  |
| `GetDescendantNodes` | 🔲 gap | 1 |  |
| `GetDocument` | 🔲 gap | 1 |  |
| `GetDocumentType` | 🔲 gap | 1 |  |
| `GetParent` | 🔲 gap | 1 |  |
| `GetRoot` | 🔲 gap | 1 |  |
| `NameTable` | 🔲 gap | 1 |  |
| `ReadFrom` | 🔲 gap | 4 |  |
| `Remove` | 🔲 gap | 1 |  |
| `RemoveNodes` | 🔲 gap | 1 |  |
| `ReplaceNodes` | 🔲 gap | 1 |  |
| `ReplaceWith` | 🔲 gap | 1 |  |
| `SelectNodes` | 🔲 gap | 2 |  |
| `SelectSingleNode` | 🔲 gap | 2 |  |
| `SetDeclaration` | 🔲 gap | 1 |  |
| `WriteTo` | 🔲 gap | 4 |  |

## XmlDocumentType  (0/19)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AddAfterSelf` | 🔲 gap | 1 |  |
| `AddBeforeSelf` | 🔲 gap | 1 |  |
| `AsXmlNode` | 🔲 gap | 1 |  |
| `Create` | 🔲 gap | 4 |  |
| `GetDocument` | 🔲 gap | 1 |  |
| `GetInternalSubset` | 🔲 gap | 1 |  |
| `GetName` | 🔲 gap | 1 |  |
| `GetParent` | 🔲 gap | 1 |  |
| `GetPublicId` | 🔲 gap | 1 |  |
| `GetSystemId` | 🔲 gap | 1 |  |
| `Remove` | 🔲 gap | 1 |  |
| `ReplaceWith` | 🔲 gap | 1 |  |
| `SelectNodes` | 🔲 gap | 2 |  |
| `SelectSingleNode` | 🔲 gap | 2 |  |
| `SetInternalSubset` | 🔲 gap | 1 |  |
| `SetName` | 🔲 gap | 1 |  |
| `SetPublicId` | 🔲 gap | 1 |  |
| `SetSystemId` | 🔲 gap | 1 |  |
| `WriteTo` | 🔲 gap | 4 |  |

## XmlElement  (0/33)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Add` | 🔲 gap | 1 |  |
| `AddAfterSelf` | 🔲 gap | 1 |  |
| `AddBeforeSelf` | 🔲 gap | 1 |  |
| `AddFirst` | 🔲 gap | 1 |  |
| `AsXmlNode` | 🔲 gap | 1 |  |
| `Attributes` | 🔲 gap | 1 |  |
| `Create` | 🔲 gap | 4 |  |
| `GetChildElements` | 🔲 gap | 3 |  |
| `GetChildNodes` | 🔲 gap | 1 |  |
| `GetDescendantElements` | 🔲 gap | 3 |  |
| `GetDescendantNodes` | 🔲 gap | 1 |  |
| `GetDocument` | 🔲 gap | 1 |  |
| `GetNamespaceOfPrefix` | 🔲 gap | 1 |  |
| `GetParent` | 🔲 gap | 1 |  |
| `GetPrefixOfNamespace` | 🔲 gap | 1 |  |
| `HasAttributes` | 🔲 gap | 1 |  |
| `HasElements` | 🔲 gap | 1 |  |
| `InnerText` | 🔲 gap | 1 |  |
| `InnerXml` | 🔲 gap | 1 |  |
| `IsEmpty` | 🔲 gap | 1 |  |
| `LocalName` | 🔲 gap | 1 |  |
| `Name` | 🔲 gap | 1 |  |
| `NamespaceUri` | 🔲 gap | 1 |  |
| `Remove` | 🔲 gap | 1 |  |
| `RemoveAllAttributes` | 🔲 gap | 1 |  |
| `RemoveAttribute` | 🔲 gap | 3 |  |
| `RemoveNodes` | 🔲 gap | 1 |  |
| `ReplaceNodes` | 🔲 gap | 1 |  |
| `ReplaceWith` | 🔲 gap | 1 |  |
| `SelectNodes` | 🔲 gap | 2 |  |
| `SelectSingleNode` | 🔲 gap | 2 |  |
| `SetAttribute` | 🔲 gap | 2 |  |
| `WriteTo` | 🔲 gap | 4 |  |

## XmlNameTable  (0/2)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Add` | 🔲 gap | 1 |  |
| `Get` | 🔲 gap | 1 |  |

## XmlNamespaceManager  (0/8)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AddNamespace` | 🔲 gap | 1 |  |
| `HasNamespace` | 🔲 gap | 1 |  |
| `LookupNamespace` | 🔲 gap | 1 |  |
| `LookupPrefix` | 🔲 gap | 1 |  |
| `NameTable` | 🔲 gap | 1 |  |
| `PopScope` | 🔲 gap | 1 |  |
| `PushScope` | 🔲 gap | 1 |  |
| `RemoveNamespace` | 🔲 gap | 1 |  |

## XmlNode  (0/27)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AddAfterSelf` | 🔲 gap | 1 |  |
| `AddBeforeSelf` | 🔲 gap | 1 |  |
| `AsXmlAttribute` | 🔲 gap | 1 |  |
| `AsXmlCData` | 🔲 gap | 1 |  |
| `AsXmlComment` | 🔲 gap | 1 |  |
| `AsXmlDeclaration` | 🔲 gap | 1 |  |
| `AsXmlDocument` | 🔲 gap | 1 |  |
| `AsXmlDocumentType` | 🔲 gap | 1 |  |
| `AsXmlElement` | 🔲 gap | 1 |  |
| `AsXmlProcessingInstruction` | 🔲 gap | 1 |  |
| `AsXmlText` | 🔲 gap | 1 |  |
| `GetDocument` | 🔲 gap | 1 |  |
| `GetParent` | 🔲 gap | 1 |  |
| `IsXmlAttribute` | 🔲 gap | 1 |  |
| `IsXmlCData` | 🔲 gap | 1 |  |
| `IsXmlComment` | 🔲 gap | 1 |  |
| `IsXmlDeclaration` | 🔲 gap | 1 |  |
| `IsXmlDocument` | 🔲 gap | 1 |  |
| `IsXmlDocumentType` | 🔲 gap | 1 |  |
| `IsXmlElement` | 🔲 gap | 1 |  |
| `IsXmlProcessingInstruction` | 🔲 gap | 1 |  |
| `IsXmlText` | 🔲 gap | 1 |  |
| `Remove` | 🔲 gap | 1 |  |
| `ReplaceWith` | 🔲 gap | 1 |  |
| `SelectNodes` | 🔲 gap | 2 |  |
| `SelectSingleNode` | 🔲 gap | 2 |  |
| `WriteTo` | 🔲 gap | 4 |  |

## XmlNodeList  (0/2)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Count` | 🔲 gap | 1 |  |
| `Get` | 🔲 gap | 1 |  |

## XmlProcessingInstruction  (0/15)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AddAfterSelf` | 🔲 gap | 1 |  |
| `AddBeforeSelf` | 🔲 gap | 1 |  |
| `AsXmlNode` | 🔲 gap | 1 |  |
| `Create` | 🔲 gap | 1 |  |
| `GetData` | 🔲 gap | 1 |  |
| `GetDocument` | 🔲 gap | 1 |  |
| `GetParent` | 🔲 gap | 1 |  |
| `GetTarget` | 🔲 gap | 1 |  |
| `Remove` | 🔲 gap | 1 |  |
| `ReplaceWith` | 🔲 gap | 1 |  |
| `SelectNodes` | 🔲 gap | 2 |  |
| `SelectSingleNode` | 🔲 gap | 2 |  |
| `SetData` | 🔲 gap | 1 |  |
| `SetTarget` | 🔲 gap | 1 |  |
| `WriteTo` | 🔲 gap | 4 |  |

## XmlReadOptions  (0/1)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `PreserveWhitespace` | 🔲 gap | 1 |  |

## XmlText  (0/12)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `AddAfterSelf` | 🔲 gap | 1 |  |
| `AddBeforeSelf` | 🔲 gap | 1 |  |
| `AsXmlNode` | 🔲 gap | 1 |  |
| `Create` | 🔲 gap | 1 |  |
| `GetDocument` | 🔲 gap | 1 |  |
| `GetParent` | 🔲 gap | 1 |  |
| `Remove` | 🔲 gap | 1 |  |
| `ReplaceWith` | 🔲 gap | 1 |  |
| `SelectNodes` | 🔲 gap | 2 |  |
| `SelectSingleNode` | 🔲 gap | 2 |  |
| `Value` | 🔲 gap | 1 |  |
| `WriteTo` | 🔲 gap | 4 |  |

## XmlWriteOptions  (0/1)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `PreserveWhitespace` | 🔲 gap | 1 |  |

## Xmlport  (0/3)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Export` | 🔲 gap | 1 |  |
| `Import` | 🔲 gap | 1 |  |
| `Run` | 🔲 gap | 1 |  |

## XmlportInstance  (0/18)

| Method | Status | Overloads | Notes |
|--------|--------|-----------|-------|
| `Break` | 🔲 gap | 1 |  |
| `BreakUnbound` | 🔲 gap | 1 |  |
| `CurrentPath` | 🔲 gap | 1 |  |
| `Export` | 🔲 gap | 1 |  |
| `FieldDelimiter` | 🔲 gap | 1 |  |
| `FieldSeparator` | 🔲 gap | 1 |  |
| `Filename` | 🔲 gap | 1 |  |
| `Import` | 🔲 gap | 1 |  |
| `ImportFile` | 🔲 gap | 1 |  |
| `Quit` | 🔲 gap | 1 |  |
| `RecordSeparator` | 🔲 gap | 1 |  |
| `Run` | 🔲 gap | 1 |  |
| `SetDestination` | 🔲 gap | 1 |  |
| `SetSource` | 🔲 gap | 1 |  |
| `SetTableView` | 🔲 gap | 1 |  |
| `Skip` | 🔲 gap | 1 |  |
| `TableSeparator` | 🔲 gap | 1 |  |
| `TextEncoding` | 🔲 gap | 1 |  |
