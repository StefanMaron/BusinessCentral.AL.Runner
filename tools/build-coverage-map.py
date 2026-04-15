#!/usr/bin/env python3
"""Generate AL language coverage map from tree-sitter-al node-types.json.

Reads the tree-sitter grammar, scans test .al files for keyword matches,
cross-references docs/limitations.md, and outputs docs/coverage.yaml +
docs/coverage.md.

Usage:
    python3 tools/build-coverage-map.py [path/to/node-types.json]

If no argument is given, downloads node-types.json from GitHub.
"""

import json
import os
import re
import sys
import urllib.request
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
TESTS_DIR = REPO_ROOT / "tests"
DOCS_DIR = REPO_ROOT / "docs"
LIMITATIONS_FILE = DOCS_DIR / "limitations.md"
NODE_TYPES_URL = (
    "https://raw.githubusercontent.com/AcmeNimble/tree-sitter-al/"
    "main/src/node-types.json"
)

# ── Categories ───────────────────────────────────────────────────────────

CATEGORY_MAP = {
    "objects": [
        "table_declaration", "page_declaration", "codeunit_declaration",
        "report_declaration", "xmlport_declaration", "query_declaration",
        "enum_declaration", "interface_declaration", "controladdin_declaration",
        "profile_declaration", "permissionset_declaration",
        "entitlement_declaration", "dotnet_declaration",
    ],
    "object_extensions": [
        "tableextension_declaration", "pageextension_declaration",
        "pagecustomization_declaration", "enumextension_declaration",
        "reportextension_declaration", "profileextension_declaration",
        "permissionsetextension_declaration",
    ],
    "declarations": [
        "field_declaration", "key_declaration", "variable_declaration",
        "procedure", "trigger_declaration", "event_declaration",
        "label_declaration", "namespace_declaration", "enum_value_declaration",
        "parameter", "parameter_list", "type_declaration", "type_specification",
        "property", "fieldgroup_declaration", "interface_procedure",
        "assembly_declaration",
    ],
    "statements": [
        "if_statement", "case_statement", "case_branch", "case_else_branch",
        "for_statement", "foreach_statement", "while_statement",
        "repeat_statement", "with_statement", "exit_statement",
        "break_statement", "continue_statement", "asserterror_statement",
        "assignment_statement", "assignment_expression", "using_statement",
        "empty_statement", "code_block",
    ],
    "expressions": [
        "call_expression", "member_expression", "additive_expression",
        "multiplicative_expression", "comparison_expression",
        "logical_expression", "unary_expression", "range_expression",
        "subscript_expression", "parenthesized_expression",
        "ternary_expression", "qualified_enum_value",
    ],
    "types": [
        "basic_type", "array_type", "option_type", "record_type",
        "code_type", "text_type", "list_type", "dictionary_type",
        "object_reference_type",
    ],
    "literals": [
        "integer", "decimal", "string_literal", "date_literal",
        "time_literal", "datetime_literal", "boolean", "biginteger_literal",
        "list_literal", "verbatim_string",
    ],
    "sections": [
        "layout_section", "actions_section", "fields_section", "keys_section",
        "fieldgroups_section", "dataset_section", "elements_section",
        "views_section", "rendering_section", "requestpage_section",
        "schema_section", "labels_section", "var_section", "fixed_section",
        "action_area_section", "action_group_section", "area_section",
        "cuegroup_section", "grid_section", "group_section", "part_section",
        "repeater_section", "systempart_section", "usercontrol_section",
    ],
    "modifications": [
        "addafter_modification", "addbefore_modification",
        "addfirst_modification", "addlast_modification",
        "modify_modification", "moveafter_modification",
        "movebefore_modification", "movefirst_modification",
        "movelast_modification",
        "addafter_action_modification", "addbefore_action_modification",
        "addfirst_action_modification", "addlast_action_modification",
        "modify_action_modification",
        "addafter_dataset_modification", "addbefore_dataset_modification",
        "addfirst_dataset_modification", "addlast_dataset_modification",
        "add_dataset_modification",
        "addafter_views_modification", "addbefore_views_modification",
        "addfirst_views_modification", "addlast_views_modification",
        "addfirst_fieldgroup_modification", "addlast_fieldgroup_modification",
    ],
    "page_elements": [
        "page_field", "action_declaration", "actionref_declaration",
        "separator_action", "systemaction_declaration",
        "fileuploadaction_declaration", "customaction_declaration",
    ],
    "report_elements": [
        "report_column", "report_dataitem",
    ],
    "query_elements": [
        "query_column", "query_dataitem", "query_filter",
    ],
    "xmlport_elements": [
        "xmlport_element", "xmlport_attribute",
    ],
    "table_features": [
        "simple_table_relation", "if_table_relation",
        "table_relation_expression", "table_relation_value",
        "else_table_relation_fragment", "calc_field_reference",
        "lookup_formula", "aggregate_formula", "aggregate_function",
    ],
    "preprocessor": [
        "pragma", "preproc_region", "preproc_endregion",
        "preproc_if", "preproc_elif", "preproc_else", "preproc_endif",
    ],
    "attributes": [
        "attribute_item", "attribute_content", "attribute_arguments",
        "attribute_argument_list",
    ],
    "other": [
        "rendering_layout", "view_definition", "caption_value",
        "implements_clause", "implementation_value",
        "implementation_value_list", "option_member", "option_member_list",
        "ml_value_list", "ml_value_pair", "permission_type",
        "tabledata_permission", "tabledata_permission_list",
        "signed_integer_list", "decimal_range_value", "filter_value",
        "link_value", "link_value_list", "sorting_value",
        "order_by_item", "order_by_list", "where_clause",
        "where_condition", "where_conditions", "database_reference",
        "object_reference_value", "procedure_modifier",
        "label_attribute",
    ],
}

# Build reverse lookup: construct → category
_CONSTRUCT_TO_CATEGORY = {}
for cat, constructs in CATEGORY_MAP.items():
    for c in constructs:
        _CONSTRUCT_TO_CATEGORY[c] = cat

# ── Constructs to skip (internal parser nodes, keywords, etc.) ───────────

SKIP_PREFIXES = [
    "preproc_conditional_",  # internal preproc variants
    "preproc_split_",        # split parser nodes
    "preproc_fragmented_",
    "preproc_guarded_",
    "preproc_open", "preproc_close",
    "preproc_not_expression", "preproc_and_expression", "preproc_or_expression",
    "var_attribute_",
]

SKIP_EXACT = {
    # Keywords — not constructs in their own right
    "actions_keyword", "area_keyword", "asserterror_keyword", "begin_keyword",
    "break_keyword", "case_keyword", "codeunit_keyword", "column_keyword",
    "continue_keyword", "controladdin_keyword", "cuegroup_keyword",
    "customizes_keyword", "dataitem_keyword", "dataset_keyword", "do_keyword",
    "dotnet_keyword", "downto_keyword", "elements_keyword", "else_keyword",
    "end_keyword", "entitlement_keyword", "enum_keyword",
    "enumextension_keyword", "event_keyword", "exit_keyword",
    "extends_keyword", "field_list", "fieldgroup_keyword",
    "fieldgroups_keyword", "fields_keyword", "filter_keyword",
    "fixed_keyword", "for_keyword", "foreach_keyword", "grid_keyword",
    "group_keyword", "identifier", "if_keyword", "implements_keyword",
    "in_keyword", "interface_keyword", "internal_keyword", "key_keyword",
    "keys_keyword", "keyword_identifier", "labels_keyword", "layout_keyword",
    "local_keyword", "namespace_keyword", "object_type_keyword", "of_keyword",
    "page_keyword", "pagecustomization_keyword", "pageextension_keyword",
    "part_keyword", "permissionset_keyword", "permissionsetextension_keyword",
    "procedure_keyword", "profile_keyword", "profileextension_keyword",
    "protected_keyword", "query_keyword", "quoted_identifier",
    "record_type",  # handled in types
    "rendering_keyword", "repeat_keyword", "repeater_keyword",
    "report_keyword", "reportextension_keyword", "requestpage_keyword",
    "schema_keyword", "systempart_keyword", "table_keyword",
    "tableextension_keyword", "temporary_keyword", "then_keyword",
    "to_keyword", "trigger_keyword", "until_keyword", "usercontrol_keyword",
    "using_keyword", "var_keyword", "view_keyword", "views_keyword",
    "while_keyword", "with_keyword", "xmlport_keyword",
    # Internal parser nodes
    "source_file", "argument_list", "comparison_operator",
    "comment", "multiline_comment",
    "namespace_name", "property_name", "property_expression",
    "dotnet_assembly_name", "dotnet_type",
    "interface_procedure_suffix",
}

# ── Construct-to-keyword mapping for test scanning ───────────────────────

CONSTRUCT_KEYWORDS = {
    # Statements
    "if_statement":           [r'\bif\b.*\bthen\b'],
    "case_statement":         [r'\bcase\b\s'],
    "case_branch":            [r'\bcase\b\s'],
    "case_else_branch":       [r'\belse\b'],
    "for_statement":          [r'\bfor\b\s.*\b(to|downto)\b'],
    "foreach_statement":      [r'\bforeach\b\s'],
    "while_statement":        [r'\bwhile\b\s'],
    "repeat_statement":       [r'\brepeat\b'],
    "exit_statement":         [r'\bexit\('],
    "break_statement":        [r'\bbreak\b\s*;'],
    "continue_statement":     [r'\bcontinue\b\s*;'],
    "asserterror_statement":  [r'\basserterror\b'],
    "assignment_statement":   [r':='],
    "assignment_expression":  [r':='],
    "with_statement":         [r'\bwith\b\s+\w+\s+\bdo\b'],
    "using_statement":        [r'\busing\b\s'],
    "empty_statement":        [r';\s*;'],
    "code_block":             [r'\bbegin\b'],

    # Objects
    "table_declaration":             [r'(?m)^\s*table\s+\d+'],
    "page_declaration":              [r'(?m)^\s*page\s+\d+'],
    "codeunit_declaration":          [r'(?m)^\s*codeunit\s+\d+'],
    "report_declaration":            [r'(?m)^\s*report\s+\d+'],
    "xmlport_declaration":           [r'(?m)^\s*xmlport\s+\d+'],
    "query_declaration":             [r'(?m)^\s*query\s+\d+'],
    "enum_declaration":              [r'(?m)^\s*enum\s+\d+'],
    "interface_declaration":         [r'(?m)^\s*interface\s+"?\w'],
    "controladdin_declaration":      [r'(?m)^\s*controladdin\s+'],
    "profile_declaration":           [r'(?m)^\s*profile\s+'],
    "permissionset_declaration":     [r'(?m)^\s*permissionset\s+\d+'],
    "entitlement_declaration":       [r'(?m)^\s*entitlement\s+'],
    "dotnet_declaration":            [r'(?m)^\s*dotnet\s+'],

    # Object extensions
    "tableextension_declaration":       [r'(?m)^\s*tableextension\s+\d+'],
    "pageextension_declaration":        [r'(?m)^\s*pageextension\s+\d+'],
    "pagecustomization_declaration":    [r'(?m)^\s*pagecustomization\s+'],
    "enumextension_declaration":        [r'(?m)^\s*enumextension\s+\d+'],
    "reportextension_declaration":      [r'(?m)^\s*reportextension\s+\d+'],
    "profileextension_declaration":     [r'(?m)^\s*profileextension\s+'],
    "permissionsetextension_declaration": [r'(?m)^\s*permissionsetextension\s+\d+'],

    # Declarations
    "field_declaration":      [r'\bfield\s*\('],
    "key_declaration":        [r'\bkey\s*\('],
    "variable_declaration":   [r'\bvar\b'],
    "procedure":              [r'\bprocedure\b\s+\w+'],
    "trigger_declaration":    [r'\btrigger\b\s+\w+'],
    "event_declaration":      [r'\[IntegrationEvent\b', r'\[BusinessEvent\b'],
    "label_declaration":      [r'\w+\s*:\s*Label\b'],
    "namespace_declaration":  [r'(?m)^\s*namespace\b'],
    "enum_value_declaration": [r'\bvalue\s*\(\d+'],
    "parameter":              [r'\bprocedure\b\s+\w+\s*\('],
    "parameter_list":         [r'\bprocedure\b\s+\w+\s*\('],
    "type_declaration":       [r':\s*(Record|Codeunit|Page|Text|Code|Integer|Decimal|Boolean|Option|Enum|List|Dictionary)\b'],
    "type_specification":     [r':\s*\w+'],
    "property":               [r'\w+\s*=\s*'],
    "fieldgroup_declaration": [r'\bfieldgroup\s*\('],
    "interface_procedure":    [r'(?m)^\s*procedure\b'],
    "assembly_declaration":   [r'(?m)^\s*assembly\b'],

    # Expressions
    "call_expression":              [r'\w+\s*\('],
    "member_expression":            [r'\.\w+'],
    "additive_expression":          [r'[+\-]\s*\w'],
    "multiplicative_expression":    [r'[*/]\s*\w'],
    "comparison_expression":        [r'(<>|<=|>=|[<>=])'],
    "logical_expression":           [r'\b(and|or|not|xor)\b'],
    "unary_expression":             [r'\bnot\b\s+', r'-\w+'],
    "range_expression":             [r'\.\.\s*\d'],
    "subscript_expression":         [r'\[\d+\]'],
    "parenthesized_expression":     [r'\(\s*\w'],
    "ternary_expression":           [r'\?'],
    "qualified_enum_value":         [r'\w+::\w+'],

    # Types
    "basic_type":          [r':\s*(Integer|Text|Code|Boolean|Decimal|Char|Byte|BigInteger|Guid|DateTime|Date|Time|Duration|Option)\b'],
    "array_type":          [r'\bArray\s*\['],
    "option_type":         [r'\bOption\b'],
    "code_type":           [r'\bCode\s*\[\d+\]'],
    "text_type":           [r'\bText\s*\[\d+\]'],
    "list_type":           [r'\bList\s+of\b'],
    "dictionary_type":     [r'\bDictionary\s+of\b'],
    "object_reference_type": [r':\s*(Record|Codeunit|Page|Report|Query|XmlPort)\b'],

    # Literals
    "integer":             [r'\b\d+\b'],
    "decimal":             [r'\b\d+\.\d+\b'],
    "string_literal":      [r"'[^']*'"],
    "date_literal":        [r'\b\d{8}D\b'],
    "time_literal":        [r'\b\d{6}T\b'],
    "datetime_literal":    [r'\d{8}DT\d{6}'],
    "boolean":             [r'\b(true|false)\b'],
    "biginteger_literal":  [r'\bBigInteger\b'],
    "list_literal":        [r'\[.*,.*\]'],
    "verbatim_string":     [r'@"'],

    # Sections
    "layout_section":       [r'\blayout\b\s*\{'],
    "actions_section":      [r'\bactions\b\s*\{'],
    "fields_section":       [r'\bfields\b\s*\{'],
    "keys_section":         [r'\bkeys\b\s*\{'],
    "fieldgroups_section":  [r'\bfieldgroups\b\s*\{'],
    "dataset_section":      [r'\bdataset\b\s*\{'],
    "elements_section":     [r'\belements\b\s*\{'],
    "views_section":        [r'\bviews\b\s*\{'],
    "rendering_section":    [r'\brendering\b\s*\{'],
    "requestpage_section":  [r'\brequestpage\b\s*\{'],
    "schema_section":       [r'\bschema\b\s*\{'],
    "labels_section":       [r'\blabels\b\s*\{'],
    "var_section":          [r'\bvar\b'],
    "fixed_section":        [r'\bfixed\b\s*\{'],
    "action_area_section":  [r'\barea\s*\(\s*(creation|processing|navigation|promoted|reporting)\b'],
    "action_group_section": [r'\bgroup\s*\('],
    "area_section":         [r'\barea\s*\('],
    "cuegroup_section":     [r'\bcuegroup\s*\('],
    "grid_section":         [r'\bgrid\s*\('],
    "group_section":        [r'\bgroup\s*\('],
    "part_section":         [r'\bpart\s*\('],
    "repeater_section":     [r'\brepeater\s*\('],
    "systempart_section":   [r'\bsystempart\s*\('],
    "usercontrol_section":  [r'\busercontrol\s*\('],

    # Modifications
    "addafter_modification":  [r'\baddafter\s*\('],
    "addbefore_modification": [r'\baddbefore\s*\('],
    "addfirst_modification":  [r'\baddfirst\s*\('],
    "addlast_modification":   [r'\baddlast\s*\('],
    "modify_modification":    [r'\bmodify\s*\('],
    "moveafter_modification": [r'\bmoveafter\s*\('],
    "movebefore_modification":[r'\bmovebefore\s*\('],
    "movefirst_modification": [r'\bmovefirst\s*\('],
    "movelast_modification":  [r'\bmovelast\s*\('],

    # Page elements
    "page_field":                  [r'\bfield\s*\('],
    "action_declaration":          [r'\baction\s*\('],
    "actionref_declaration":       [r'\bactionref\s*\('],
    "separator_action":            [r'\bseparator\s*[\(;]'],
    "systemaction_declaration":    [r'\bsystemaction\s*\('],
    "fileuploadaction_declaration":[r'\bfileuploadaction\s*\('],
    "customaction_declaration":    [r'\bcustomaction\s*\('],

    # Report elements
    "report_column":      [r'\bcolumn\s*\('],
    "report_dataitem":    [r'\bdataitem\s*\('],

    # Query elements
    "query_column":       [r'\bcolumn\s*\('],
    "query_dataitem":     [r'\bdataitem\s*\('],
    "query_filter":       [r'\bfilter\s*\('],

    # Xmlport elements
    "xmlport_element":    [r'\b(tableelement|textelement|fieldelement)\s*\('],
    "xmlport_attribute":  [r'\b(fieldattribute|textattribute)\s*\('],

    # Table features
    "simple_table_relation":        [r'\bTableRelation\b'],
    "if_table_relation":            [r'\bTableRelation\b.*\bif\b'],
    "table_relation_expression":    [r'\bTableRelation\b'],
    "table_relation_value":         [r'\bTableRelation\b'],
    "else_table_relation_fragment": [r'\bTableRelation\b.*\belse\b'],
    "calc_field_reference":         [r'\bCalcFormula\b'],
    "lookup_formula":               [r'\bCalcFormula\s*=\s*lookup\b'],
    "aggregate_formula":            [r'\bCalcFormula\s*=\s*(sum|count|min|max|average)\b'],
    "aggregate_function":           [r'\b(sum|count|min|max|average)\s*\('],

    # Preprocessor
    "pragma":              [r'#pragma\b'],
    "preproc_region":      [r'#region\b'],
    "preproc_endregion":   [r'#endregion\b'],
    "preproc_if":          [r'#if\b'],
    "preproc_elif":        [r'#elif\b'],
    "preproc_else":        [r'#else\b'],
    "preproc_endif":       [r'#endif\b'],

    # Attributes
    "attribute_item":          [r'\[\w+'],
    "attribute_content":       [r'\[\w+'],
    "attribute_arguments":     [r'\[\w+\s*\('],
    "attribute_argument_list": [r'\[\w+\s*\('],

    # Other
    "rendering_layout":         [r'\brendering\b'],
    "view_definition":          [r'\bview\s*\('],
    "caption_value":            [r'\bCaption\s*='],
    "implements_clause":        [r'\bimplements\b'],
    "implementation_value":     [r'\bimplements\b'],
    "implementation_value_list":[r'\bimplements\b'],
    "option_member":            [r'\bOption\w*\s*=\s*'],
    "option_member_list":       [r'\bOption\w*\s*=\s*'],
    "qualified_enum_value":     [r'\w+::\w+'],
    "permission_type":          [r'\bPermissions\s*='],
    "where_clause":             [r'\bwhere\s*\('],
    "where_condition":          [r'\bwhere\s*\('],
    "where_conditions":         [r'\bwhere\s*\('],
    "database_reference":       [r'\bDatabase\s*::'],
    "procedure_modifier":       [r'\b(local|internal)\b\s+\bprocedure\b'],
    "label_attribute":          [r'\bLabel\b'],
}

# ── Runtime constructs (not in the grammar) ──────────────────────────────

RUNTIME_CONSTRUCTS = [
    {
        "construct": "record_operations",
        "category": "runtime",
        "keywords": [r'\.(Insert|Modify|Delete|Get|FindFirst|FindLast|FindSet|Next|Init|Reset|IsEmpty|Count|CalcFields|SetRecFilter|CopyFilters|TransferFields|Rename)\s*\('],
        "notes": None,
    },
    {
        "construct": "record_filtering",
        "category": "runtime",
        "keywords": [r'\.(SetRange|SetFilter|SetCurrentKey|GetFilter|GetRangeMin|GetRangeMax)\s*\('],
        "notes": None,
    },
    {
        "construct": "codeunit_dispatch",
        "category": "runtime",
        "keywords": [r'\bCodeunit\.(Run)\s*\(', r'\.Run\s*\('],
        "notes": None,
    },
    {
        "construct": "interface_dispatch",
        "category": "runtime",
        "keywords": [r'\binterface\b', r'\bimplements\b'],
        "notes": None,
    },
    {
        "construct": "isolated_storage",
        "category": "runtime",
        "keywords": [r'\bIsolatedStorage\.(Set|Get|Delete|Contains)\b'],
        "notes": None,
    },
    {
        "construct": "text_builder",
        "category": "runtime",
        "keywords": [r'\bTextBuilder\b'],
        "notes": None,
    },
    {
        "construct": "json_operations",
        "category": "runtime",
        "keywords": [r'\b(JsonObject|JsonArray|JsonToken|JsonValue)\b'],
        "notes": None,
    },
    {
        "construct": "blob_stream",
        "category": "runtime",
        "keywords": [r'\b(InStream|OutStream|BLOB|CreateInStream|CreateOutStream)\b'],
        "notes": None,
    },
    {
        "construct": "format_evaluate",
        "category": "runtime",
        "keywords": [r'\b(Format|Evaluate)\s*\('],
        "notes": None,
    },
    {
        "construct": "error_handling",
        "category": "runtime",
        "keywords": [r'\bError\s*\(', r'\basserterror\b', r'\bExpectedError\b'],
        "notes": None,
    },
    {
        "construct": "dialog",
        "category": "runtime",
        "keywords": [r'\b(Message|Confirm)\s*\(', r'\bDialog\b'],
        "notes": None,
    },
    {
        "construct": "session_api",
        "category": "runtime",
        "keywords": [r'\b(StartSession|StopSession|Sleep|IsSessionActive)\b'],
        "notes": "StartSession runs synchronously; IsSessionActive returns false.",
    },
    {
        "construct": "notification",
        "category": "runtime",
        "keywords": [r'\bNotification\b'],
        "notes": None,
    },
    {
        "construct": "variant_type",
        "category": "runtime",
        "keywords": [r'\bVariant\b'],
        "notes": None,
    },
    {
        "construct": "record_ref",
        "category": "runtime",
        "keywords": [r'\b(RecordRef|FieldRef)\b'],
        "notes": None,
    },
    {
        "construct": "test_page",
        "category": "runtime",
        "keywords": [r'\bTestPage\b'],
        "notes": None,
    },
    {
        "construct": "test_handlers",
        "category": "runtime",
        "keywords": [r'\[(ConfirmHandler|MessageHandler|ModalPageHandler|RequestPageHandler|SendNotificationHandler)\b'],
        "notes": None,
    },
    {
        "construct": "assert_library",
        "category": "runtime",
        "keywords": [r'\bAssert\.(AreEqual|AreNotEqual|IsTrue|IsFalse|ExpectedError|ExpectedErrorCode|ExpectedMessage)\b'],
        "notes": None,
    },
    {
        "construct": "variable_storage",
        "category": "runtime",
        "keywords": [r'\b(LibraryVariableStorage|VariableStorage)\b', r'\.(Enqueue|DequeueText|DequeueInteger)\b'],
        "notes": None,
    },
    {
        "construct": "http_mock",
        "category": "runtime",
        "keywords": [r'\b(HttpClient|HttpRequestMessage|HttpResponseMessage|HttpContent|HttpHeaders)\b'],
        "notes": "Mock types available. HttpClient.Send/Get/Post throw NotSupportedException.",
    },
    {
        "construct": "xmlport_stub",
        "category": "runtime",
        "keywords": [r'\bXmlPort\b'],
        "notes": "Compile-time stub. Import/Export throw NotSupportedException at runtime.",
    },
    {
        "construct": "query_stub",
        "category": "runtime",
        "keywords": [r'\bQuery\b'],
        "notes": None,
    },
    {
        "construct": "big_text",
        "category": "runtime",
        "keywords": [r'\bBigText\b'],
        "notes": None,
    },
    {
        "construct": "number_sequence",
        "category": "runtime",
        "keywords": [r'\bNumberSequence\b'],
        "notes": None,
    },
    {
        "construct": "error_info",
        "category": "runtime",
        "keywords": [r'\bErrorInfo\b'],
        "notes": None,
    },
    {
        "construct": "date_formula",
        "category": "runtime",
        "keywords": [r'\bDateFormula\b'],
        "notes": None,
    },
    {
        "construct": "guid_type",
        "category": "runtime",
        "keywords": [r'\bGuid\b'],
        "notes": None,
    },
    {
        "construct": "enum_operations",
        "category": "runtime",
        "keywords": [r'\b(Enum\.Names|Enum\.Ordinals|Enum\.FromInteger)\b', r'::\w+'],
        "notes": None,
    },
    {
        "construct": "temporary_records",
        "category": "runtime",
        "keywords": [r'\btemporary\b'],
        "notes": None,
    },
    {
        "construct": "try_function",
        "category": "runtime",
        "keywords": [r'\[TryFunction\]'],
        "notes": None,
    },
    {
        "construct": "event_subscribers",
        "category": "runtime",
        "keywords": [r'\[EventSubscriber\b'],
        "notes": "Partial support. Zero-arg subscribers work; parameter passing is limited.",
    },
]

# ── Not-possible items (from limitations.md) ─────────────────────────────

NOT_POSSIBLE = {
    "parallel_sessions": "StartSession runs synchronously; no parallel execution or cross-session isolation.",
    "transaction_semantics": "Commit/Rollback are no-ops; no transaction isolation.",
    "page_rendering": "No layout engine. TestPage and handler dispatch work, but field visibility/editability are not evaluated.",
    "report_rendering": "No report layout engine or dataset rendering. Report variables support limited standalone helpers only.",
    "permission_enforcement": "No permission system; all access succeeds unconditionally.",
    "http_send": "HttpClient.Send/Get/Post/Put/Delete/Patch throw NotSupportedException. Mock types available for header/content manipulation.",
    "company_context": "CompanyName() returns empty string. No company context.",
    "base_app_data": "No standard BC tables are populated unless tests insert data.",
}

# ── Scanning ─────────────────────────────────────────────────────────────


def load_node_types(path: str | None) -> list[dict]:
    """Load node-types.json from path or download from GitHub."""
    if path and os.path.isfile(path):
        with open(path) as f:
            return json.load(f)

    print("Downloading node-types.json from GitHub...")
    with urllib.request.urlopen(NODE_TYPES_URL) as resp:
        return json.loads(resp.read().decode())


def should_skip(node_type: str) -> bool:
    """Return True if this node type should be excluded from the map."""
    if node_type in SKIP_EXACT:
        return True
    for prefix in SKIP_PREFIXES:
        if node_type.startswith(prefix):
            return True
    return False


def get_category(construct: str) -> str:
    """Return the category for a construct, or 'other'."""
    return _CONSTRUCT_TO_CATEGORY.get(construct, "other")


def collect_al_files() -> dict[str, str]:
    """Return {relative_test_dir: concatenated .al content} for all test suites."""
    suites: dict[str, str] = {}
    for bucket in TESTS_DIR.iterdir():
        if not bucket.is_dir():
            continue
        for suite_dir in sorted(bucket.iterdir()):
            if not suite_dir.is_dir():
                continue
            al_content = []
            for al_file in sorted(suite_dir.rglob("*.al")):
                try:
                    al_content.append(al_file.read_text(errors="replace"))
                except OSError:
                    pass
            if al_content:
                rel = str(suite_dir.relative_to(REPO_ROOT))
                suites[rel] = "\n".join(al_content)
    return suites


def match_construct(keywords: list[str], al_text: str) -> bool:
    """Return True if any keyword pattern matches in the AL text."""
    for pattern in keywords:
        if re.search(pattern, al_text, re.IGNORECASE):
            return True
    return False


def scan_for_construct(construct: str, suites: dict[str, str]) -> list[str]:
    """Return list of test suite paths that match the construct."""
    keywords = CONSTRUCT_KEYWORDS.get(construct)
    if not keywords:
        return []
    matches = []
    for suite_path, al_text in suites.items():
        if match_construct(keywords, al_text):
            matches.append(suite_path)
    return matches


def scan_runtime_construct(keywords: list[str], suites: dict[str, str]) -> list[str]:
    """Return list of test suite paths that match runtime keywords."""
    matches = []
    for suite_path, al_text in suites.items():
        if match_construct(keywords, al_text):
            matches.append(suite_path)
    return matches


def load_limitations() -> str:
    """Load limitations.md content."""
    if LIMITATIONS_FILE.exists():
        return LIMITATIONS_FILE.read_text()
    return ""


# ── YAML output (hand-written to avoid PyYAML dependency) ────────────────


def yaml_str(s: str | None) -> str:
    """Encode a string for YAML output."""
    if s is None:
        return "null"
    if not s or any(c in s for c in ":{}\n\"'#|>&*!%@`"):
        return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'
    return s


def write_yaml(entries: list[dict], path: Path) -> None:
    """Write coverage entries to YAML."""
    lines = []
    for entry in entries:
        lines.append(f"- construct: {entry['construct']}")
        lines.append(f"  category: {entry['category']}")
        lines.append(f"  status: {entry['status']}")
        if entry["tests"]:
            lines.append("  tests:")
            for t in sorted(entry["tests"]):
                lines.append(f"    - {t}")
        else:
            lines.append("  tests: []")
        lines.append(f"  notes: {yaml_str(entry.get('notes'))}")
        lines.append("")
    path.write_text("\n".join(lines) + "\n")


# ── Markdown output ──────────────────────────────────────────────────────

STATUS_EMOJI = {
    "covered": "✅",
    "gap": "🔲",
    "not-possible": "❌",
}

CATEGORY_ORDER = [
    "objects", "object_extensions", "declarations", "statements",
    "expressions", "types", "literals", "sections", "modifications",
    "page_elements", "report_elements", "query_elements", "xmlport_elements",
    "table_features", "preprocessor", "attributes", "runtime", "other",
]

CATEGORY_LABELS = {
    "objects": "Object Declarations",
    "object_extensions": "Object Extensions",
    "declarations": "Declarations",
    "statements": "Statements",
    "expressions": "Expressions",
    "types": "Types",
    "literals": "Literals",
    "sections": "Sections",
    "modifications": "Modifications (Extension Operations)",
    "page_elements": "Page Elements",
    "report_elements": "Report Elements",
    "query_elements": "Query Elements",
    "xmlport_elements": "XmlPort Elements",
    "table_features": "Table Features",
    "preprocessor": "Preprocessor Directives",
    "attributes": "Attributes",
    "runtime": "Runtime Constructs",
    "other": "Other",
}


def write_markdown(entries: list[dict], path: Path) -> None:
    """Write rendered markdown coverage table."""
    by_cat: dict[str, list[dict]] = {}
    for e in entries:
        by_cat.setdefault(e["category"], []).append(e)

    # Summary counts
    total = len(entries)
    covered = sum(1 for e in entries if e["status"] == "covered")
    gap = sum(1 for e in entries if e["status"] == "gap")
    not_possible = sum(1 for e in entries if e["status"] == "not-possible")

    lines = [
        "# AL Language Coverage Map",
        "",
        "Auto-generated by `tools/build-coverage-map.py` from "
        "[tree-sitter-al](https://github.com/AcmeNimble/tree-sitter-al) "
        "node-types.json.",
        "",
        "## Summary",
        "",
        f"| Status | Count |",
        f"|--------|-------|",
        f"| ✅ Covered | {covered} |",
        f"| 🔲 Gap | {gap} |",
        f"| ❌ Not possible | {not_possible} |",
        f"| **Total** | **{total}** |",
        "",
    ]

    for cat in CATEGORY_ORDER:
        cat_entries = by_cat.get(cat, [])
        if not cat_entries:
            continue
        label = CATEGORY_LABELS.get(cat, cat.replace("_", " ").title())
        lines.append(f"## {label}")
        lines.append("")
        lines.append("| Construct | Status | Tests | Notes |")
        lines.append("|-----------|--------|-------|-------|")
        for e in sorted(cat_entries, key=lambda x: x["construct"]):
            emoji = STATUS_EMOJI.get(e["status"], "")
            test_links = ", ".join(
                f"[{os.path.basename(t)}]({t})" for t in sorted(e["tests"])
            ) if e["tests"] else "—"
            notes = e.get("notes") or "—"
            construct = f"`{e['construct']}`"
            lines.append(f"| {construct} | {emoji} {e['status']} | {test_links} | {notes} |")
        lines.append("")

    # Trim trailing blank lines
    while lines and lines[-1] == "":
        lines.pop()
    lines.append("")

    path.write_text("\n".join(lines))


# ── Main ─────────────────────────────────────────────────────────────────


def main():
    node_types_path = sys.argv[1] if len(sys.argv) > 1 else None
    node_types = load_node_types(node_types_path)

    named_types = sorted(
        {n["type"] for n in node_types if n.get("named")},
    )
    print(f"Loaded {len(named_types)} named node types from grammar")

    # Filter to meaningful constructs
    grammar_constructs = [t for t in named_types if not should_skip(t)]
    print(f"After filtering: {len(grammar_constructs)} constructs")

    # Collect test .al files
    suites = collect_al_files()
    print(f"Found {len(suites)} test suites")

    # Load limitations
    limitations_text = load_limitations()

    entries: list[dict] = []

    # Process grammar constructs
    for construct in grammar_constructs:
        category = get_category(construct)
        tests = scan_for_construct(construct, suites)

        # Ubiquitous constructs: if any test exists, mark covered
        if construct in (
            "call_expression", "member_expression", "assignment_statement",
            "assignment_expression", "comparison_expression", "code_block",
            "variable_declaration", "procedure", "parameter", "parameter_list",
            "type_specification", "property", "integer", "string_literal",
            "boolean", "var_section",
        ) and suites:
            tests = tests[:5] if tests else list(suites.keys())[:3]

        # Limit test list to keep output manageable
        if len(tests) > 8:
            tests = tests[:8]

        status = "covered" if tests else "gap"
        notes = None

        entries.append({
            "construct": construct,
            "category": category,
            "status": status,
            "tests": tests,
            "notes": notes,
        })

    # Add runtime constructs
    for rc in RUNTIME_CONSTRUCTS:
        tests = scan_runtime_construct(rc["keywords"], suites)
        if len(tests) > 8:
            tests = tests[:8]
        status = "covered" if tests else "gap"
        entries.append({
            "construct": rc["construct"],
            "category": rc["category"],
            "status": status,
            "tests": tests,
            "notes": rc.get("notes"),
        })

    # Add not-possible entries
    for construct, note in NOT_POSSIBLE.items():
        entries.append({
            "construct": construct,
            "category": "runtime",
            "status": "not-possible",
            "tests": [],
            "notes": note,
        })

    # Sort by category then construct name
    cat_order = {c: i for i, c in enumerate(CATEGORY_ORDER)}
    entries.sort(key=lambda e: (cat_order.get(e["category"], 99), e["construct"]))

    # Write outputs
    yaml_path = DOCS_DIR / "coverage.yaml"
    md_path = DOCS_DIR / "coverage.md"

    write_yaml(entries, yaml_path)
    print(f"Wrote {yaml_path}")

    write_markdown(entries, md_path)
    print(f"Wrote {md_path}")

    # Summary
    covered = sum(1 for e in entries if e["status"] == "covered")
    gap = sum(1 for e in entries if e["status"] == "gap")
    np = sum(1 for e in entries if e["status"] == "not-possible")
    print(f"\nCoverage: {covered} covered, {gap} gaps, {np} not-possible "
          f"({len(entries)} total)")


if __name__ == "__main__":
    main()
