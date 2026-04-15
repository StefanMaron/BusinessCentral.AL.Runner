# AL Language Coverage Map

Auto-generated from `docs/coverage.yaml`. Do not edit directly.

## Summary

| Status | Count |
|--------|-------|
| ✅ Covered | 98 |
| 🔲 Gap | 47 |
| ❌ Not possible | 3 |
| ⬜ Out of scope | 6 |
| **Total** | **154** |

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
| `type_declaration` | 🔲 gap | — |  |
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
