#!/usr/bin/env node
// coverage-gen.js — Generates docs/coverage.yaml + docs/coverage.md.
//
// The coverage map has TWO layers:
//   - syntax      → from tree-sitter-al node-types.json (construct presence in tests)
//   - runtime-api → from Microsoft.Dynamics.Nav.CodeAnalysis built-in method symbols
//                   (runtime-api.json, produced by tools/RuntimeApiEnumerator),
//                   coverage inferred by scanning AlRunner/Runtime/*.cs for AL-prefixed methods.
//
// Usage:
//   node scripts/coverage-gen.js                  → regenerate yaml + md (preserves curation)
//   node scripts/coverage-gen.js --fetch          → redownload node-types.json first
//   node scripts/coverage-gen.js --render         → yaml → md only
//   node scripts/coverage-gen.js --validate       → CI check: paths exist, covered has tests
//   node scripts/coverage-gen.js --runtime-only   → regenerate runtime-api layer only

const fs = require("fs");
const path = require("path");
const https = require("https");

const ROOT = path.resolve(__dirname, "..");
const NODE_TYPES_URL =
  "https://raw.githubusercontent.com/SShadowS/tree-sitter-al/main/src/node-types.json";
const NODE_TYPES_CACHE = path.join(__dirname, "node-types.json");
const RUNTIME_API_JSON = path.join(__dirname, "runtime-api.json");
const COVERAGE_YAML = path.join(ROOT, "docs", "coverage.yaml");
const COVERAGE_MD = path.join(ROOT, "docs", "coverage.md");
const TESTS_DIR = path.join(ROOT, "tests");
const RUNTIME_DIR = path.join(ROOT, "AlRunner", "Runtime");
const ALSCOPE_FILE = path.join(RUNTIME_DIR, "AlScope.cs");

// ── Syntax layer: construct filter & categorization ────────────────────────

const EXCLUDE_PATTERNS = [/_keyword$/, /^preproc_/, /^prec_/];

const EXCLUDE_SET = new Set([
  "source_file", "code_block", "begin_keyword", "end_keyword", "boolean",
  "integer", "decimal", "biginteger_literal", "date_literal", "datetime_literal",
  "time_literal", "string_literal", "verbatim_string", "identifier",
  "keyword_identifier", "quoted_identifier", "comment", "multiline_comment",
  "pragma", "signed_integer_list", "argument_list", "ml_value_list",
  "ml_value_pair", "caption_value", "link_value", "link_value_list",
  "implementation_value", "implementation_value_list", "sorting_value",
  "property", "property_name", "property_expression", "attribute_argument_list",
  "attribute_arguments", "attribute_content", "comparison_operator",
  "decimal_range_value", "field_list", "filter_value", "order_by_item",
  "order_by_list", "object_type_keyword", "dotnet_assembly_name", "dotnet_type",
  "permission_type", "tabledata_permission", "tabledata_permission_list",
  "option_member", "option_member_list", "where_condition", "where_conditions",
  "simple_table_relation", "else_table_relation_fragment", "if_table_relation",
  "table_relation_value",
]);

const CATEGORY_MAP = {
  codeunit_declaration: "object", table_declaration: "object",
  page_declaration: "object", report_declaration: "object",
  query_declaration: "object", xmlport_declaration: "object",
  enum_declaration: "object", interface_declaration: "object",
  profile_declaration: "object", permissionset_declaration: "object",
  entitlement_declaration: "object", controladdin_declaration: "object",
  dotnet_declaration: "object", assembly_declaration: "object",
  tableextension_declaration: "extension", pageextension_declaration: "extension",
  enumextension_declaration: "extension", reportextension_declaration: "extension",
  pagecustomization_declaration: "extension", profileextension_declaration: "extension",
  permissionsetextension_declaration: "extension",
  if_statement: "statement", case_statement: "statement",
  case_branch: "statement", case_else_branch: "statement",
  for_statement: "statement", foreach_statement: "statement",
  while_statement: "statement", repeat_statement: "statement",
  with_statement: "statement", asserterror_statement: "statement",
  exit_statement: "statement", break_statement: "statement",
  continue_statement: "statement", assignment_statement: "statement",
  empty_statement: "statement", using_statement: "statement",
  additive_expression: "expression", multiplicative_expression: "expression",
  comparison_expression: "expression", logical_expression: "expression",
  ternary_expression: "expression", unary_expression: "expression",
  assignment_expression: "expression", parenthesized_expression: "expression",
  call_expression: "expression", member_expression: "expression",
  subscript_expression: "expression", range_expression: "expression",
  database_reference: "expression", qualified_enum_value: "expression",
  basic_type: "type", array_type: "type", list_type: "type",
  dictionary_type: "type", code_type: "type", text_type: "type",
  option_type: "type", record_type: "type", object_reference_type: "type",
  type_specification: "type", type_declaration: "type",
  procedure: "procedure", procedure_modifier: "procedure",
  trigger_declaration: "procedure", event_declaration: "procedure",
  event_keyword: "procedure", interface_procedure: "procedure",
  interface_procedure_suffix: "procedure",
  var_section: "variable", var_attribute_item: "variable",
  var_attribute_open: "variable", variable_declaration: "variable",
  parameter: "variable", parameter_list: "variable",
  label_declaration: "variable", label_attribute: "variable",
  field_declaration: "table", key_declaration: "table",
  fieldgroup_declaration: "table", fields_section: "table",
  keys_section: "table", fieldgroups_section: "table",
  table_relation_expression: "table", calc_field_reference: "table",
  lookup_formula: "table", aggregate_formula: "table",
  aggregate_function: "table", fixed_section: "table",
  page_field: "page", action_declaration: "page",
  action_area_section: "page", action_group_section: "page",
  actionref_declaration: "page", actions_section: "page",
  area_section: "page", group_section: "page", part_section: "page",
  repeater_section: "page", grid_section: "page", cuegroup_section: "page",
  usercontrol_section: "page", systempart_section: "page",
  layout_section: "page", views_section: "page",
  view_definition: "page", separator_action: "page",
  customaction_declaration: "page", fileuploadaction_declaration: "page",
  systemaction_declaration: "page",
  report_column: "report", report_dataitem: "report",
  dataset_section: "report", requestpage_section: "report",
  rendering_section: "report", rendering_layout: "report",
  query_column: "query", query_dataitem: "query", query_filter: "query",
  xmlport_element: "xmlport", xmlport_attribute: "xmlport",
  elements_section: "xmlport", schema_section: "xmlport",
  enum_value_declaration: "enum", implements_clause: "enum",
  addafter_modification: "modification", addbefore_modification: "modification",
  addfirst_modification: "modification", addlast_modification: "modification",
  modify_modification: "modification", moveafter_modification: "modification",
  movebefore_modification: "modification", movefirst_modification: "modification",
  movelast_modification: "modification", modify_action_modification: "modification",
  addafter_action_modification: "modification",
  addbefore_action_modification: "modification",
  addfirst_action_modification: "modification",
  addlast_action_modification: "modification",
  add_dataset_modification: "modification",
  addafter_dataset_modification: "modification",
  addbefore_dataset_modification: "modification",
  addlast_dataset_modification: "modification",
  addfirst_dataset_modification: "modification",
  addafter_views_modification: "modification",
  addbefore_views_modification: "modification",
  addfirst_views_modification: "modification",
  addlast_views_modification: "modification",
  addfirst_fieldgroup_modification: "modification",
  addlast_fieldgroup_modification: "modification",
  label_section: "label", labels_section: "label",
  namespace_declaration: "namespace", namespace_name: "namespace",
  attribute_item: "attribute",
};

const CONSTRUCT_KEYWORDS = {
  codeunit_declaration: [/\bcodeunit\s+\d+/i],
  table_declaration: [/\btable\s+\d+/i],
  page_declaration: [/\bpage\s+\d+/i],
  report_declaration: [/\breport\s+\d+/i],
  query_declaration: [/\bquery\s+\d+/i],
  xmlport_declaration: [/\bxmlport\s+\d+/i],
  enum_declaration: [/\benum\s+\d+/i],
  interface_declaration: [/\binterface\s+"[^"]+"/i],
  tableextension_declaration: [/\btableextension\s+\d+/i],
  pageextension_declaration: [/\bpageextension\s+\d+/i],
  enumextension_declaration: [/\benumextension\s+\d+/i],
  reportextension_declaration: [/\breportextension\s+\d+/i],
  if_statement: [/\bif\b.*\bthen\b/i],
  case_statement: [/\bcase\b.*\bof\b/i],
  for_statement: [/\bfor\b.*\b(to|downto)\b.*\bdo\b/i],
  foreach_statement: [/\bforeach\b.*\bin\b.*\bdo\b/i],
  while_statement: [/\bwhile\b.*\bdo\b/i],
  repeat_statement: [/\brepeat\b/i],
  asserterror_statement: [/\basserterror\b/i],
  exit_statement: [/\bexit\s*\(/i],
  with_statement: [/\bwith\b.*\bdo\b/i],
  trigger_declaration: [/\btrigger\s+On/i],
  event_declaration: [/\[IntegrationEvent\b/i, /\[BusinessEvent\b/i],
  procedure: [/\bprocedure\b/i],
  var_section: [/\bvar\b/i],
  variable_declaration: [/\bvar\b/i],
  field_declaration: [/\bfield\s*\(\s*\d+/i],
  key_declaration: [/\bkey\s*\(/i],
  label_declaration: [/\bLabel\b.*=\s*'/i],
  array_type: [/\barray\s*\[/i],
  list_type: [/\bList\s+of\s+\[/i],
  record_type: [/:\s*Record\b/i],
  enum_value_declaration: [/\bvalue\s*\(\s*\d+/i],
  implements_clause: [/\bimplements\b/i],
};

// ── Runtime-API layer: type → mock file mapping ────────────────────────────
//
// Maps every AL built-in type (from runtime-api.json) to the .cs file(s) whose
// AL-prefixed methods implement its API. An AL call `foo.Bar(x)` becomes a call
// to `ALBar` on the corresponding mock type, so presence of `ALBar(` in the
// mock file is our coverage signal.
//
// Types not in this map default to `gap` unless they appear in
// RUNTIME_API_NOT_POSSIBLE below.

const RUNTIME_API_MOCK_FILES = {
  BigInteger: ["AlScope.cs"],
  BigText: ["MockBigText.cs"],
  Blob: ["MockBlob.cs"],
  Boolean: ["AlScope.cs"],
  Byte: ["AlScope.cs"],
  Code: ["AlScope.cs"],
  Codeunit: ["MockCodeunitHandle.cs"],
  CodeunitInstance: ["MockCodeunitHandle.cs"],
  DataTransfer: ["MockDataTransfer.cs"],
  Date: ["AlScope.cs"],
  DateTime: ["AlScope.cs"],
  Decimal: ["AlScope.cs"],
  Dialog: ["AlScope.cs"],
  Duration: ["AlScope.cs"],
  EnumType: ["AlScope.cs", "MockRecordHandle.cs"],
  ErrorInfo: ["AlScope.cs"],
  FieldRef: ["MockFieldRef.cs"],
  File: ["MockFile.cs"],
  FileUpload: ["MockFile.cs"],
  FilterPageBuilder: ["MockFormHandle.cs"],
  Guid: ["AlScope.cs"],
  HttpClient: ["MockHttpClient.cs"],
  HttpContent: ["MockHttpContent.cs"],
  HttpHeaders: ["MockHttpHeaders.cs"],
  HttpRequestMessage: ["MockHttpRequestMessage.cs"],
  HttpResponseMessage: ["MockHttpResponseMessage.cs"],
  InStream: ["MockInStream.cs", "MockStream.cs"],
  Integer: ["AlScope.cs"],
  IsolatedStorage: ["MockIsolatedStorage.cs"],
  JsonArray: ["MockJsonHelper.cs"],
  JsonObject: ["MockJsonHelper.cs"],
  JsonToken: ["MockJsonHelper.cs"],
  JsonValue: ["MockJsonHelper.cs"],
  KeyRef: ["MockKeyRef.cs"],
  Label: ["AlScope.cs"],
  Language: ["MockLanguage.cs"],
  Media: ["MockBlob.cs"],
  MediaSet: ["MockBlob.cs"],
  NavApp: ["MockNavApp.cs"],
  Notification: ["MockNotification.cs"],
  NumberSequence: ["MockNumberSequence.cs"],
  ObjectList: ["MockObjectList.cs"],
  OutStream: ["MockOutStream.cs", "MockStream.cs"],
  Page: ["MockFormHandle.cs", "MockTestPageHandle.cs"],
  Query: ["MockQueryHandle.cs"],
  RecordId: ["MockRecordRef.cs", "MockRecordHandle.cs"],
  RecordRef: ["MockRecordRef.cs", "MockRecordHandle.cs"],
  Report: ["MockReportHandle.cs"],
  ReportInstance: ["MockReportHandle.cs"],
  Session: ["MockSession.cs"],
  Text: ["AlScope.cs"],
  TextBuilder: ["MockTextBuilder.cs"],
  Time: ["AlScope.cs"],
  Variant: ["MockVariant.cs"],
  XmlPort: ["MockXmlPortHandle.cs"],
};

// AL types that tie to architectural limits (see docs/limitations.md).
// These stay "not-possible" no matter what the mock files say.
const RUNTIME_API_NOT_POSSIBLE_TYPES = new Set([
  "TaskScheduler",    // CreateTask is a sync stub, but the contract is parallel
  "Debugger",         // No debugger in standalone mode
]);

// Individual methods that are not-possible even on otherwise-covered types.
const RUNTIME_API_NOT_POSSIBLE_METHODS = new Set([
  "Session.StartSession",
  "Codeunit.StartSession",
  "HttpClient.Send",         // Real HTTP I/O
  "HttpClient.Get",          // Real HTTP I/O (mock returns canned data but doesn't hit the network)
  "HttpClient.Post",         // ditto
  "HttpClient.Put",          // ditto
  "HttpClient.Delete",       // ditto
  "HttpClient.Patch",        // ditto
]);

// ── Helpers ────────────────────────────────────────────────────────────────

function fetch(url) {
  return new Promise((resolve, reject) => {
    https.get(url, (res) => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
        return fetch(res.headers.location).then(resolve, reject);
      }
      const chunks = [];
      res.on("data", (c) => chunks.push(c));
      res.on("end", () => resolve(Buffer.concat(chunks).toString()));
      res.on("error", reject);
    }).on("error", reject);
  });
}

function walkDir(dir, ext) {
  const results = [];
  if (!fs.existsSync(dir)) return results;
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) results.push(...walkDir(full, ext));
    else if (entry.name.endsWith(ext)) results.push(full);
  }
  return results;
}

function scanTestSuites() {
  const suites = {};
  const bucketDirs = fs.readdirSync(TESTS_DIR, { withFileTypes: true })
    .filter((d) => d.isDirectory() && d.name.startsWith("bucket-"))
    .map((d) => path.join(TESTS_DIR, d.name));
  const stubsDir = path.join(TESTS_DIR, "stubs");
  if (fs.existsSync(stubsDir)) bucketDirs.push(stubsDir);
  for (const bucketDir of bucketDirs) {
    const bucketName = path.basename(bucketDir);
    for (const suite of fs.readdirSync(bucketDir, { withFileTypes: true })) {
      if (!suite.isDirectory()) continue;
      const suiteDir = path.join(bucketDir, suite.name);
      const alFiles = [
        ...walkDir(path.join(suiteDir, "src"), ".al"),
        ...walkDir(path.join(suiteDir, "test"), ".al"),
      ];
      const content = alFiles.map((f) => fs.readFileSync(f, "utf8")).join("\n");
      const relativePath = `tests/${bucketName}/${suite.name}`;
      suites[relativePath] = { content, name: suite.name };
    }
  }
  return suites;
}

function autoAssignSyntax(constructs, suites) {
  const assignments = {};
  for (const c of constructs) {
    assignments[c] = [];
    const patterns = CONSTRUCT_KEYWORDS[c];
    if (!patterns) continue;
    for (const [suitePath, { content }] of Object.entries(suites)) {
      if (patterns.some((p) => p.test(content))) assignments[c].push(suitePath);
    }
  }
  return assignments;
}

// ── Runtime-API mock scanner ───────────────────────────────────────────────

/**
 * Scan a mock file for AL-prefixed methods.
 * Returns the set of `AL<Name>` identifiers it publicly defines (methods, properties).
 * The regex is intentionally loose — it matches declarations where `AL<Name>`
 * is followed by `(`, `=>`, `{`, or `;` (fields/auto-properties).
 */
function extractAlMethodsFromMock(filePath) {
  const found = new Set();
  if (!fs.existsSync(filePath)) return found;
  const src = fs.readFileSync(filePath, "utf8");
  const rx = /\b(AL[A-Z][A-Za-z0-9_]*)\s*[(={;]/g;
  let m;
  while ((m = rx.exec(src)) !== null) found.add(m[1]);
  return found;
}

/**
 * For every overload in runtime-api.json, return a map keyed by
 * "Type.Method (sig)" → { status, mockFiles, type, method, signature }.
 * A method is covered when `AL<Method>` appears in any mapped mock file.
 *
 * Status logic:
 *   - not-possible: type or method is in the architectural exclusion lists
 *   - gap:          no mock file mapping, or AL<Method> not found in mocks
 *   - not-tested:   AL<Method> found in mocks, but method has >1 overload —
 *                   we can confirm the name is implemented but can't determine
 *                   which specific signatures are covered without deeper analysis
 *   - covered:      AL<Method> found in mocks, and method has exactly 1 overload
 */
function buildRuntimeApiCoverage(runtimeApi) {
  const entries = {};
  // Cache parsed mock-file symbols
  const mockSymbols = {};
  function load(file) {
    if (mockSymbols[file]) return mockSymbols[file];
    const full = file === "AlScope.cs" ? ALSCOPE_FILE : path.join(RUNTIME_DIR, file);
    const syms = extractAlMethodsFromMock(full);
    mockSymbols[file] = { syms, full };
    return mockSymbols[file];
  }

  for (const [typeName, typeEntry] of Object.entries(runtimeApi.types || {})) {
    const mockFiles = RUNTIME_API_MOCK_FILES[typeName] || [];
    const unionSyms = new Set();
    for (const f of mockFiles) {
      for (const s of load(f).syms) unionSyms.add(s);
    }

    // Group overloads by method name so we can tell when a name has >1 overload
    const byName = {};
    for (const method of typeEntry.methods || []) {
      (byName[method.name] ||= []).push(method);
    }

    for (const method of typeEntry.methods || []) {
      const baseKey = `${typeName}.${method.name}`;
      const sig = method.signature || "()";
      // Key includes signature so each overload is a separate entry
      const key = `${baseKey} ${sig}`;
      const alName = `AL${method.name}`;
      let status;
      if (RUNTIME_API_NOT_POSSIBLE_TYPES.has(typeName)) {
        status = "not-possible";
      } else if (RUNTIME_API_NOT_POSSIBLE_METHODS.has(baseKey)) {
        status = "not-possible";
      } else if (mockFiles.length === 0) {
        status = "gap";
      } else if (!unionSyms.has(alName)) {
        // Method name not found at all → gap
        status = "gap";
      } else if (byName[method.name].length > 1) {
        // Method name exists in mock but has multiple overloads — we know the
        // name is implemented but can't confirm which signatures are covered
        // without deeper analysis; mark as not-tested.
        status = "not-tested";
      } else {
        // Single overload, name found in mock → covered
        status = "covered";
      }
      entries[key] = {
        status,
        mockFiles: mockFiles.slice(),
        type: typeName,
        method: method.name,
        signature: sig,
      };
    }
  }
  return entries;
}

// ── YAML I/O ───────────────────────────────────────────────────────────────

/**
 * Entry shape: { name, layer, category, status, tests: [], notes?: string }.
 * Preserves order of appearance in the source file.
 *
 * Top-level keys may contain spaces and parens for per-overload entries, e.g.:
 *   BigText.AddText (Text, Integer):
 */
function parseYaml(text) {
  const entries = [];
  let current = null;
  for (const line of text.split("\n")) {
    if (line.startsWith("#") || line.trim() === "") {
      if (line.startsWith("# ── ") && current) {
        entries.push(current);
        current = null;
      }
      continue;
    }
    // Match any top-level key: alphanumeric, dots, underscores, spaces, parens, commas
    const topMatch = line.match(/^([A-Za-z0-9_.][A-Za-z0-9_. ,()]*):$/);
    if (topMatch) {
      if (current) entries.push(current);
      current = { name: topMatch[1].trimEnd(), tests: [] };
      continue;
    }
    if (!current) continue;
    const kvMatch = line.match(/^\s+(category|status|notes|layer):\s*(.+)$/);
    if (kvMatch) { current[kvMatch[1]] = kvMatch[2].trim(); continue; }
    const listMatch = line.match(/^\s+-\s+(.+)$/);
    if (listMatch && current) current.tests.push(listMatch[1].trim());
  }
  if (current) entries.push(current);
  return entries;
}

/**
 * Emit YAML from entries. Groups by layer and category. Escapes notes only when
 * needed (colons, quotes, leading/trailing whitespace).
 */
function emitYaml(entries, headerLines) {
  const byLayer = { syntax: [], "runtime-api": [] };
  for (const e of entries) {
    const layer = e.layer || "syntax";
    (byLayer[layer] ||= []).push(e);
  }

  const lines = headerLines.slice();
  for (const layer of ["syntax", "runtime-api"]) {
    const items = byLayer[layer] || [];
    if (items.length === 0) continue;
    const grouped = {};
    for (const e of items) {
      const cat = e.category || "uncategorized";
      (grouped[cat] ||= []).push(e);
    }
    lines.push("", `# ══════════════════════════════════════════════════════════════`,
                   `# Layer: ${layer}`,
                   `# ══════════════════════════════════════════════════════════════`, "");
    const catOrder = [
      // syntax-layer categories
      "object", "extension", "statement", "expression", "type", "procedure",
      "variable", "table", "page", "report", "query", "xmlport", "enum",
      "modification", "label", "namespace", "attribute",
      // runtime-api (one category per AL type, alphabetical)
      ...Object.keys(grouped).filter(c => !["object","extension","statement","expression","type","procedure","variable","table","page","report","query","xmlport","enum","modification","label","namespace","attribute","uncategorized"].includes(c)).sort(),
      "uncategorized",
    ];
    for (const cat of catOrder) {
      const bucket = grouped[cat];
      if (!bucket || bucket.length === 0) continue;
      const dashes = Math.max(0, 60 - cat.length);
      lines.push(`# ── ${cat} ${"─".repeat(dashes)}`, "");
      bucket.sort((a, b) => a.name.localeCompare(b.name));
      for (const e of bucket) {
        lines.push(`${e.name}:`);
        lines.push(`  layer: ${layer}`);
        lines.push(`  category: ${cat}`);
        lines.push(`  status: ${e.status}`);
        if (e.tests && e.tests.length > 0) {
          lines.push(`  tests:`);
          for (const t of [...e.tests].sort()) lines.push(`    - ${t}`);
        }
        if (e.notes) lines.push(`  notes: ${e.notes}`);
        lines.push("");
      }
    }
  }
  return lines.join("\n");
}

// ── Merge helpers ──────────────────────────────────────────────────────────

/**
 * Merge hand-curation (from existing YAML) into freshly-generated entries.
 * Curated status/tests/notes win when present; auto-detected values fill gaps.
 *
 * Migration: if an existing entry has an old-format key "Type.Method" and the
 * generated entries use new-format keys "Type.Method (sig)", the old entry's
 * curation is copied to the lowest-arity overload (the one with fewest params),
 * determined alphabetically when arity is equal.
 */
function mergeEntries(generated, existing) {
  const existingMap = new Map(existing.map(e => [e.name, e]));

  // Build a secondary map from old-style "Type.Method" → list of new-format entries
  // so we can migrate hand-curation to the primary (lowest-arity) overload.
  const oldStyleSiblings = new Map(); // "Type.Method" → [newEntry, ...]
  for (const gen of generated) {
    if (gen.layer !== "runtime-api") continue;
    // New key pattern: "Type.Method (sig)" — extract base "Type.Method"
    const baseMatch = gen.name.match(/^(.+?) \(.*\)$/);
    if (!baseMatch) continue;
    const base = baseMatch[1];
    if (!oldStyleSiblings.has(base)) oldStyleSiblings.set(base, []);
    oldStyleSiblings.get(base).push(gen);
  }

  // For a given base key, find which new-format entry is the "primary":
  // lowest-arity overload (fewest commas in signature), then alphabetically.
  function primaryName(base) {
    const siblings = oldStyleSiblings.get(base) || [];
    if (siblings.length === 0) return null;
    return siblings.slice().sort((a, b) => {
      const arity = (name) => {
        const m = name.match(/\(([^)]*)\)$/);
        if (!m || m[1].trim() === "") return 0;
        return m[1].split(",").length;
      };
      const diff = arity(a.name) - arity(b.name);
      return diff !== 0 ? diff : a.name.localeCompare(b.name);
    })[0].name;
  }

  return generated.map(gen => {
    // Exact match (new-format key already in existing YAML, or non-runtime-api entries)
    const prev = existingMap.get(gen.name);
    if (prev) {
      // Strip "overloads=N" prefix from notes — auto-generated by the old format
      // and now meaningless in the per-overload schema. Handle "overloads=N; rest",
      // "overloads=N rest", and bare "; rest" (from intermediate regenerations
      // where the overloads= token was already partially stripped).
      const cleanedNotes = prev.notes
        ? prev.notes.replace(/^overloads=\d+[;,]?\s*/, "").replace(/^;\s*/, "").trim() || undefined
        : undefined;
      return {
        ...gen,
        status: prev.status ?? gen.status,
        tests: (prev.tests && prev.tests.length > 0) ? prev.tests : gen.tests,
        notes: cleanedNotes ?? gen.notes,
      };
    }

    // Migration: look for old-format key "Type.Method" (no signature suffix)
    if (gen.layer === "runtime-api") {
      const baseMatch = gen.name.match(/^(.+?) \(.*\)$/);
      if (baseMatch) {
        const base = baseMatch[1];
        const oldEntry = existingMap.get(base);
        if (oldEntry && primaryName(base) === gen.name) {
          // Migrate curation from old entry to the primary overload.
          // Strip "overloads=N" notes — those were auto-generated by the old
          // format and are now meaningless in the per-overload schema.
          const migratedNotes = oldEntry.notes
            ? oldEntry.notes.replace(/^overloads=\d+[;,]?\s*/, "").replace(/^;\s*/, "").trim() || undefined
            : undefined;
          return {
            ...gen,
            status: oldEntry.status ?? gen.status,
            tests: (oldEntry.tests && oldEntry.tests.length > 0) ? oldEntry.tests : gen.tests,
            notes: migratedNotes ?? gen.notes,
          };
        }
      }
    }

    return gen;
  });
}

// ── Markdown rendering ─────────────────────────────────────────────────────

function renderMarkdown(entries) {
  const statusIcon = {
    covered: "✅", "not-possible": "❌", gap: "🔲", "out-of-scope": "⬜",
    "not-tested": "🔶",
  };
  const lines = [
    "# AL Language Coverage Map", "",
    "Auto-generated from `docs/coverage.yaml`. Do not edit directly.", "",
    "## Per-overload signature tracking", "",
    "Runtime-API coverage is tracked at **per-overload signature granularity** since " +
    "[PR #1363](https://github.com/StefanMaron/BusinessCentral.AL.Runner/pull/1363). " +
    "Each BC built-in method overload gets its own entry, keyed by `Type.Method (ParamTypes)`.", "",
    "### Status meanings for runtime-api", "",
    "| Status | Meaning |",
    "|--------|---------|",
    "| ✅ covered | Single overload and `AL<Method>` found in mock — confirmed implemented |",
    "| 🔶 not-tested | `AL<Method>` found in mock but method has multiple overloads — name is implemented but per-overload coverage is unconfirmed |",
    "| 🔲 gap | `AL<Method>` not found in any mapped mock file — not yet implemented |",
    "| ❌ not-possible | Architectural limit (parallel session, real HTTP I/O, debugger) |",
    "",
  ];

  // Summary per layer
  for (const layer of ["syntax", "runtime-api"]) {
    const scope = entries.filter(e => (e.layer || "syntax") === layer);
    if (scope.length === 0) continue;
    const counts = { covered: 0, gap: 0, "not-possible": 0, "out-of-scope": 0, "not-tested": 0 };
    for (const e of scope) counts[e.status] = (counts[e.status] || 0) + 1;
    lines.push(`## Summary — ${layer}`, "");
    lines.push(`| Status | Count |`, `|--------|-------|`);
    lines.push(`| ✅ Covered | ${counts.covered || 0} |`);
    lines.push(`| 🔶 Not tested (overload) | ${counts["not-tested"] || 0} |`);
    lines.push(`| 🔲 Gap | ${counts.gap || 0} |`);
    lines.push(`| ❌ Not possible | ${counts["not-possible"] || 0} |`);
    lines.push(`| ⬜ Out of scope | ${counts["out-of-scope"] || 0} |`);
    lines.push(`| **Total** | **${scope.length}** |`, "");
  }

  // Syntax section
  const syntax = entries.filter(e => (e.layer || "syntax") === "syntax");
  if (syntax.length > 0) {
    lines.push("# Syntax layer", "");
    const grouped = groupBy(syntax, e => e.category || "uncategorized");
    const catOrder = [
      "object","extension","statement","expression","type","procedure",
      "variable","table","page","report","query","xmlport","enum",
      "modification","label","namespace","attribute","uncategorized",
    ];
    for (const cat of catOrder) {
      const bucket = grouped[cat];
      if (!bucket || bucket.length === 0) continue;
      lines.push(`## ${capitalize(cat)}`, "");
      lines.push("| Construct | Status | Test Suites | Notes |");
      lines.push("|-----------|--------|-------------|-------|");
      for (const e of bucket.sort((a, b) => a.name.localeCompare(b.name))) {
        const icon = statusIcon[e.status] || "❓";
        const tests = (e.tests || []).length > 0
          ? e.tests.map(t => `\`${t.split("/").pop()}\``).join(", ") : "—";
        lines.push(`| \`${e.name}\` | ${icon} ${e.status} | ${tests} | ${e.notes || ""} |`);
      }
      lines.push("");
    }
  }

  // Runtime-API section (grouped by AL type, i.e. category)
  // Each row is one overload signature.
  const runtime = entries.filter(e => e.layer === "runtime-api");
  if (runtime.length > 0) {
    lines.push("# Runtime API layer", "");
    lines.push("Source: `Microsoft.Dynamics.Nav.CodeAnalysis` method symbol tables. " +
               "Coverage = AL-prefixed method present in `AlRunner/Runtime/*.cs`. " +
               "Each row is one overload signature.", "");
    const grouped = groupBy(runtime, e => e.category || "uncategorized");
    const types = Object.keys(grouped).sort();
    for (const t of types) {
      const bucket = grouped[t];
      const coveredCount = bucket.filter(e => e.status === "covered").length;
      lines.push(`## ${t}  (${coveredCount}/${bucket.length})`, "");
      lines.push("| Method | Signature | Status | Notes |");
      lines.push("|--------|-----------|--------|-------|");
      for (const e of bucket.sort((a, b) => a.name.localeCompare(b.name))) {
        const icon = statusIcon[e.status] || "❓";
        // Extract "Method (sig)" from "Type.Method (sig)"
        const dotIdx = e.name.indexOf(".");
        const methodAndSig = dotIdx >= 0 ? e.name.slice(dotIdx + 1) : e.name;
        // Split into method name and signature
        const sigMatch = methodAndSig.match(/^(.+?) (\(.*\))$/);
        const methodName = sigMatch ? sigMatch[1] : methodAndSig;
        const sig = sigMatch ? sigMatch[2] : "";
        lines.push(`| \`${methodName}\` | \`${sig}\` | ${icon} ${e.status} | ${e.notes || ""} |`);
      }
      lines.push("");
    }
  }
  return lines.join("\n");
}

function groupBy(list, keyFn) {
  const out = {};
  for (const x of list) (out[keyFn(x)] ||= []).push(x);
  return out;
}
function capitalize(s) { return s.charAt(0).toUpperCase() + s.slice(1); }

// ── CI validation ──────────────────────────────────────────────────────────

function validate(entries) {
  let errors = 0;
  for (const e of entries) {
    if (!e.layer) { console.error(`ERROR: ${e.name} is missing 'layer'`); errors++; continue; }
    if (!["syntax","runtime-api"].includes(e.layer)) {
      console.error(`ERROR: ${e.name} has invalid layer '${e.layer}'`); errors++;
    }
    if (e.layer === "syntax") {
      if (e.status === "covered" && (!e.tests || e.tests.length === 0)) {
        console.error(`ERROR: ${e.name} is 'covered' but has no test suites`); errors++;
      }
      for (const t of (e.tests || [])) {
        const full = path.join(ROOT, t);
        if (!fs.existsSync(full)) {
          console.error(`ERROR: ${e.name} references non-existent path: ${t}`);
          errors++;
        }
      }
    }
    if (e.layer === "runtime-api" && !e.name.includes(".")) {
      console.error(`ERROR: runtime-api entry ${e.name} must be 'Type.Method' or 'Type.Method (sig)'`); errors++;
    }
  }
  return errors;
}

// ── Main ───────────────────────────────────────────────────────────────────

async function main() {
  const args = process.argv.slice(2);

  if (args.includes("--render")) {
    if (!fs.existsSync(COVERAGE_YAML)) {
      console.error("docs/coverage.yaml not found."); process.exit(1);
    }
    const entries = parseYaml(fs.readFileSync(COVERAGE_YAML, "utf8"));
    fs.writeFileSync(COVERAGE_MD, renderMarkdown(entries));
    console.log(`Wrote ${COVERAGE_MD} (${entries.length} constructs)`);
    return;
  }

  if (args.includes("--validate")) {
    if (!fs.existsSync(COVERAGE_YAML)) {
      console.error("docs/coverage.yaml not found."); process.exit(1);
    }
    const entries = parseYaml(fs.readFileSync(COVERAGE_YAML, "utf8"));
    const errors = validate(entries);
    if (errors > 0) {
      console.error(`Validation failed with ${errors} error(s)`); process.exit(1);
    }
    console.log(`Validation passed (${entries.length} entries)`);
    return;
  }

  // Load existing YAML for hand-curation merging
  const existingEntries = fs.existsSync(COVERAGE_YAML)
    ? parseYaml(fs.readFileSync(COVERAGE_YAML, "utf8")) : [];

  // ── Syntax layer ───────────────────────────
  let syntaxEntries = existingEntries.filter(e => (e.layer || "syntax") === "syntax");
  if (!args.includes("--runtime-only")) {
    let raw;
    if (args.includes("--fetch") || !fs.existsSync(NODE_TYPES_CACHE)) {
      console.log("Fetching node-types.json…");
      raw = await fetch(NODE_TYPES_URL);
      fs.writeFileSync(NODE_TYPES_CACHE, raw);
    } else {
      raw = fs.readFileSync(NODE_TYPES_CACHE, "utf8");
    }
    const nodeTypes = JSON.parse(raw);
    const allNamed = [...new Set(nodeTypes.filter(n => n.named).map(n => n.type))].sort();
    const constructs = allNamed.filter(name =>
      !EXCLUDE_SET.has(name) &&
      !EXCLUDE_PATTERNS.some(p => p.test(name)) &&
      CATEGORY_MAP[name]);

    console.log(`[syntax] ${allNamed.length} named nodes → ${constructs.length} constructs`);

    const suites = scanTestSuites();
    console.log(`[syntax] ${Object.keys(suites).length} test suites scanned`);
    const assignments = autoAssignSyntax(constructs, suites);

    const generated = constructs.map(name => ({
      name,
      layer: "syntax",
      category: CATEGORY_MAP[name] || "uncategorized",
      status: assignments[name].length > 0 ? "covered" : "gap",
      tests: assignments[name],
    }));
    syntaxEntries = mergeEntries(generated, existingEntries.filter(e => (e.layer || "syntax") === "syntax"));
  }

  // ── Runtime-API layer ───────────────────────
  let runtimeEntries = [];
  if (fs.existsSync(RUNTIME_API_JSON)) {
    const runtimeApi = JSON.parse(fs.readFileSync(RUNTIME_API_JSON, "utf8"));
    const coverage = buildRuntimeApiCoverage(runtimeApi);
    console.log(`[runtime-api] ${Object.keys(coverage).length} overload entries from ${Object.keys(runtimeApi.types).length} types`);
    const generated = Object.values(coverage).map(c => ({
      // Key includes signature so each overload is a separate entry:
      // e.g. "BigText.AddText (Text, Integer)"
      name: `${c.type}.${c.method} ${c.signature}`,
      layer: "runtime-api",
      category: c.type,
      status: c.status,
      tests: [],
    }));
    runtimeEntries = mergeEntries(generated, existingEntries.filter(e => e.layer === "runtime-api"));
  } else {
    console.log(`[runtime-api] ${RUNTIME_API_JSON} not found — skipping runtime layer`);
    runtimeEntries = existingEntries.filter(e => e.layer === "runtime-api");
  }

  // ── Emit ───────────────────────────────────
  const header = [
    "# AL Language Coverage Map",
    "# Generated by scripts/coverage-gen.js — do not edit by hand.",
    `# Last updated: ${new Date().toISOString().slice(0, 10)}`,
    "#",
    "# Layers:",
    "#   syntax        — tree-sitter-al node-types.json (language constructs)",
    "#   runtime-api   — Microsoft.Dynamics.Nav.CodeAnalysis built-in method surface",
    "#",
    "# Status values:",
    "#   covered       — supported with proof (tests for syntax, mock impl for runtime-api)",
    "#   not-possible  — architectural limits prevent support",
    "#   not-tested    — method name exists in mock but has multiple overloads;",
    "#                   per-overload coverage is unconfirmed",
    "#   gap           — in-scope but not yet implemented",
    "#   out-of-scope  — not relevant to the runner",
    "#",
    "# Hand-curation is preserved on regeneration: status/tests/notes set in the",
    "# file override the auto-detected values.",
  ];

  const all = [...syntaxEntries, ...runtimeEntries];
  fs.writeFileSync(COVERAGE_YAML, emitYaml(all, header));
  console.log(`Wrote ${COVERAGE_YAML} (${all.length} entries)`);

  fs.writeFileSync(COVERAGE_MD, renderMarkdown(all));
  console.log(`Wrote ${COVERAGE_MD}`);
}

if (require.main === module) {
  main().catch(err => { console.error(err); process.exit(1); });
}

// ── Exports for testing ────────────────────────────────────────────────────
module.exports = {
  parseYaml, emitYaml, mergeEntries, validate, renderMarkdown,
  extractAlMethodsFromMock, buildRuntimeApiCoverage,
  RUNTIME_API_MOCK_FILES, RUNTIME_API_NOT_POSSIBLE_TYPES,
};
