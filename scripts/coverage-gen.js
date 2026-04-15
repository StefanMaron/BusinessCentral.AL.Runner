#!/usr/bin/env node
// coverage-gen.js — Generates docs/coverage.yaml from tree-sitter-al node-types.json
// Usage: node scripts/coverage-gen.js [--fetch] [--render]
//   --fetch   Download fresh node-types.json from GitHub
//   --render  Generate docs/coverage.md from docs/coverage.yaml

const fs = require("fs");
const path = require("path");
const https = require("https");

const ROOT = path.resolve(__dirname, "..");
const NODE_TYPES_URL =
  "https://raw.githubusercontent.com/SShadowS/tree-sitter-al/main/src/node-types.json";
const NODE_TYPES_CACHE = path.join(__dirname, "node-types.json");
const COVERAGE_YAML = path.join(ROOT, "docs", "coverage.yaml");
const COVERAGE_MD = path.join(ROOT, "docs", "coverage.md");
const TESTS_DIR = path.join(ROOT, "tests");

// ── Construct filter & categorization ──────────────────────────────────────

// Nodes to exclude: keywords, preprocessor internals, punctuation-level grammar
const EXCLUDE_PATTERNS = [
  /_keyword$/,
  /^preproc_/,
  /^prec_/,
];

const EXCLUDE_SET = new Set([
  "source_file",
  "code_block",
  "begin_keyword",
  "end_keyword",
  "boolean",
  "integer",
  "decimal",
  "biginteger_literal",
  "date_literal",
  "datetime_literal",
  "time_literal",
  "string_literal",
  "verbatim_string",
  "identifier",
  "keyword_identifier",
  "quoted_identifier",
  "comment",
  "multiline_comment",
  "pragma",
  "signed_integer_list",
  "argument_list",
  "ml_value_list",
  "ml_value_pair",
  "caption_value",
  "link_value",
  "link_value_list",
  "implementation_value",
  "implementation_value_list",
  "sorting_value",
  "property",
  "property_name",
  "property_expression",
  "attribute_argument_list",
  "attribute_arguments",
  "attribute_content",
  "comparison_operator",
  "decimal_range_value",
  "field_list",
  "filter_value",
  "order_by_item",
  "order_by_list",
  "object_type_keyword",
  "dotnet_assembly_name",
  "dotnet_type",
  "permission_type",
  "tabledata_permission",
  "tabledata_permission_list",
  "option_member",
  "option_member_list",
  "where_condition",
  "where_conditions",
  "simple_table_relation",
  "else_table_relation_fragment",
  "if_table_relation",
  "table_relation_value",
]);

const CATEGORY_MAP = {
  // Object declarations
  codeunit_declaration: "object",
  table_declaration: "object",
  page_declaration: "object",
  report_declaration: "object",
  query_declaration: "object",
  xmlport_declaration: "object",
  enum_declaration: "object",
  interface_declaration: "object",
  profile_declaration: "object",
  permissionset_declaration: "object",
  entitlement_declaration: "object",
  controladdin_declaration: "object",
  dotnet_declaration: "object",
  assembly_declaration: "object",

  // Extension declarations
  tableextension_declaration: "extension",
  pageextension_declaration: "extension",
  enumextension_declaration: "extension",
  reportextension_declaration: "extension",
  pagecustomization_declaration: "extension",
  profileextension_declaration: "extension",
  permissionsetextension_declaration: "extension",

  // Statements
  if_statement: "statement",
  case_statement: "statement",
  case_branch: "statement",
  case_else_branch: "statement",
  for_statement: "statement",
  foreach_statement: "statement",
  while_statement: "statement",
  repeat_statement: "statement",
  with_statement: "statement",
  asserterror_statement: "statement",
  exit_statement: "statement",
  break_statement: "statement",
  continue_statement: "statement",
  assignment_statement: "statement",
  empty_statement: "statement",
  using_statement: "statement",

  // Expressions
  additive_expression: "expression",
  multiplicative_expression: "expression",
  comparison_expression: "expression",
  logical_expression: "expression",
  ternary_expression: "expression",
  unary_expression: "expression",
  assignment_expression: "expression",
  parenthesized_expression: "expression",
  call_expression: "expression",
  member_expression: "expression",
  subscript_expression: "expression",
  range_expression: "expression",
  database_reference: "expression",
  qualified_enum_value: "expression",

  // Types
  basic_type: "type",
  array_type: "type",
  list_type: "type",
  dictionary_type: "type",
  code_type: "type",
  text_type: "type",
  option_type: "type",
  record_type: "type",
  object_reference_type: "type",
  type_specification: "type",
  type_declaration: "type",

  // Procedures and triggers
  procedure: "procedure",
  procedure_modifier: "procedure",
  trigger_declaration: "procedure",
  event_declaration: "procedure",
  event_keyword: "procedure",
  interface_procedure: "procedure",
  interface_procedure_suffix: "procedure",

  // Variables and parameters
  var_section: "variable",
  var_attribute_item: "variable",
  var_attribute_open: "variable",
  variable_declaration: "variable",
  parameter: "variable",
  parameter_list: "variable",
  label_declaration: "variable",
  label_attribute: "variable",

  // Table features
  field_declaration: "table",
  key_declaration: "table",
  fieldgroup_declaration: "table",
  fields_section: "table",
  keys_section: "table",
  fieldgroups_section: "table",
  table_relation_expression: "table",
  calc_field_reference: "table",
  lookup_formula: "table",
  aggregate_formula: "table",
  aggregate_function: "table",
  fixed_section: "table",

  // Page features
  page_field: "page",
  action_declaration: "page",
  action_area_section: "page",
  action_group_section: "page",
  actionref_declaration: "page",
  actions_section: "page",
  area_section: "page",
  group_section: "page",
  part_section: "page",
  repeater_section: "page",
  grid_section: "page",
  cuegroup_section: "page",
  usercontrol_section: "page",
  systempart_section: "page",
  layout_section: "page",
  views_section: "page",
  view_definition: "page",
  separator_action: "page",
  customaction_declaration: "page",
  fileuploadaction_declaration: "page",
  systemaction_declaration: "page",

  // Report features
  report_column: "report",
  report_dataitem: "report",
  dataset_section: "report",
  requestpage_section: "report",
  rendering_section: "report",
  rendering_layout: "report",

  // Query features
  query_column: "query",
  query_dataitem: "query",
  query_filter: "query",

  // XmlPort features
  xmlport_element: "xmlport",
  xmlport_attribute: "xmlport",
  elements_section: "xmlport",
  schema_section: "xmlport",

  // Enum features
  enum_value_declaration: "enum",
  implements_clause: "enum",

  // Extension modifications
  addafter_modification: "modification",
  addbefore_modification: "modification",
  addfirst_modification: "modification",
  addlast_modification: "modification",
  modify_modification: "modification",
  moveafter_modification: "modification",
  movebefore_modification: "modification",
  movefirst_modification: "modification",
  movelast_modification: "modification",
  modify_action_modification: "modification",
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

  // Labels
  label_section: "label",
  labels_section: "label",

  // Namespace
  namespace_declaration: "namespace",
  namespace_name: "namespace",

  // Attributes
  attribute_item: "attribute",
};

// ── Test suite → construct mapping (keyword-based heuristics) ──────────────

// Map of construct name → array of keyword patterns to search in AL files
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
    if (entry.isDirectory()) {
      results.push(...walkDir(full, ext));
    } else if (entry.name.endsWith(ext)) {
      results.push(full);
    }
  }
  return results;
}

function scanTestSuites() {
  const suites = {};
  const bucketDirs = fs.readdirSync(TESTS_DIR, { withFileTypes: true })
    .filter((d) => d.isDirectory() && d.name.startsWith("bucket-"))
    .map((d) => path.join(TESTS_DIR, d.name));

  // Also include stubs dir
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

function autoAssign(constructs, suites) {
  const assignments = {};
  for (const c of constructs) {
    assignments[c] = [];
    const patterns = CONSTRUCT_KEYWORDS[c];
    if (!patterns) continue;
    for (const [suitePath, { content }] of Object.entries(suites)) {
      if (patterns.some((p) => p.test(content))) {
        assignments[c].push(suitePath);
      }
    }
  }
  return assignments;
}

// ── YAML generation ────────────────────────────────────────────────────────

function toYaml(constructs, assignments) {
  const grouped = {};
  for (const c of constructs) {
    const cat = CATEGORY_MAP[c] || "uncategorized";
    if (!grouped[cat]) grouped[cat] = [];
    grouped[cat].push(c);
  }

  const lines = [
    "# AL Language Coverage Map",
    "# Generated from SShadowS/tree-sitter-al node-types.json",
    `# Last updated: ${new Date().toISOString().slice(0, 10)}`,
    "#",
    "# Status values:",
    "#   covered       — supported with proof test suites",
    "#   not-possible  — architectural limits prevent support",
    "#   gap           — in-scope but not yet implemented",
    "#   out-of-scope  — grammar node not relevant to runner",
    "",
  ];

  const categoryOrder = [
    "object", "extension", "statement", "expression", "type",
    "procedure", "variable", "table", "page", "report", "query",
    "xmlport", "enum", "modification", "label", "namespace",
    "attribute", "uncategorized",
  ];

  for (const cat of categoryOrder) {
    const items = grouped[cat];
    if (!items || items.length === 0) continue;
    lines.push(`# ── ${cat} ${"─".repeat(60 - cat.length)}`);
    lines.push("");
    for (const c of items.sort()) {
      const tests = assignments[c] || [];
      const status = tests.length > 0 ? "covered" : "gap";
      lines.push(`${c}:`);
      lines.push(`  category: ${cat}`);
      lines.push(`  status: ${status}`);
      if (tests.length > 0) {
        lines.push(`  tests:`);
        for (const t of tests.sort()) {
          lines.push(`    - ${t}`);
        }
      }
      lines.push("");
    }
  }
  return lines.join("\n");
}

// ── Markdown rendering ─────────────────────────────────────────────────────

function parseYaml(text) {
  // Minimal YAML parser for our flat structure
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
    const topMatch = line.match(/^(\w+):$/);
    if (topMatch) {
      if (current) entries.push(current);
      current = { name: topMatch[1], tests: [] };
      continue;
    }
    if (!current) continue;
    const kvMatch = line.match(/^\s+(category|status|notes):\s*(.+)$/);
    if (kvMatch) {
      current[kvMatch[1]] = kvMatch[2].trim();
      continue;
    }
    const listMatch = line.match(/^\s+-\s+(.+)$/);
    if (listMatch && current) {
      current.tests.push(listMatch[1].trim());
    }
  }
  if (current) entries.push(current);
  return entries;
}

function renderMarkdown(entries) {
  const statusIcon = {
    covered: "✅",
    "not-possible": "❌",
    gap: "🔲",
    "out-of-scope": "⬜",
  };

  const grouped = {};
  for (const e of entries) {
    const cat = e.category || "uncategorized";
    if (!grouped[cat]) grouped[cat] = [];
    grouped[cat].push(e);
  }

  const lines = [
    "# AL Language Coverage Map",
    "",
    "Auto-generated from `docs/coverage.yaml`. Do not edit directly.",
    "",
  ];

  // Summary
  const total = entries.length;
  const covered = entries.filter((e) => e.status === "covered").length;
  const gap = entries.filter((e) => e.status === "gap").length;
  const notPossible = entries.filter((e) => e.status === "not-possible").length;
  const outOfScope = entries.filter((e) => e.status === "out-of-scope").length;

  lines.push("## Summary");
  lines.push("");
  lines.push(`| Status | Count |`);
  lines.push(`|--------|-------|`);
  lines.push(`| ✅ Covered | ${covered} |`);
  lines.push(`| 🔲 Gap | ${gap} |`);
  lines.push(`| ❌ Not possible | ${notPossible} |`);
  lines.push(`| ⬜ Out of scope | ${outOfScope} |`);
  lines.push(`| **Total** | **${total}** |`);
  lines.push("");

  const categoryOrder = [
    "object", "extension", "statement", "expression", "type",
    "procedure", "variable", "table", "page", "report", "query",
    "xmlport", "enum", "modification", "label", "namespace",
    "attribute", "uncategorized",
  ];

  for (const cat of categoryOrder) {
    const items = grouped[cat];
    if (!items || items.length === 0) continue;

    lines.push(`## ${cat.charAt(0).toUpperCase() + cat.slice(1)}`);
    lines.push("");
    lines.push("| Construct | Status | Test Suites | Notes |");
    lines.push("|-----------|--------|-------------|-------|");

    for (const e of items.sort((a, b) => a.name.localeCompare(b.name))) {
      const icon = statusIcon[e.status] || "❓";
      const tests = e.tests.length > 0
        ? e.tests.map((t) => `\`${t.split("/").pop()}\``).join(", ")
        : "—";
      const notes = e.notes || "";
      lines.push(`| \`${e.name}\` | ${icon} ${e.status} | ${tests} | ${notes} |`);
    }
    lines.push("");
  }

  return lines.join("\n");
}

// ── CI validation ──────────────────────────────────────────────────────────

function validate(entries) {
  let errors = 0;
  for (const e of entries) {
    if (e.status === "covered" && e.tests.length === 0) {
      console.error(`ERROR: ${e.name} is marked 'covered' but has no test suites`);
      errors++;
    }
    for (const t of e.tests) {
      const fullPath = path.join(ROOT, t);
      if (!fs.existsSync(fullPath)) {
        console.error(`ERROR: ${e.name} references non-existent path: ${t}`);
        errors++;
      }
    }
  }
  return errors;
}

// ── Main ───────────────────────────────────────────────────────────────────

async function main() {
  const args = process.argv.slice(2);

  if (args.includes("--render")) {
    // Render coverage.md from coverage.yaml
    if (!fs.existsSync(COVERAGE_YAML)) {
      console.error("docs/coverage.yaml not found. Run without --render first.");
      process.exit(1);
    }
    const yaml = fs.readFileSync(COVERAGE_YAML, "utf8");
    const entries = parseYaml(yaml);
    const md = renderMarkdown(entries);
    fs.writeFileSync(COVERAGE_MD, md);
    console.log(`Wrote ${COVERAGE_MD} (${entries.length} constructs)`);
    return;
  }

  if (args.includes("--validate")) {
    if (!fs.existsSync(COVERAGE_YAML)) {
      console.error("docs/coverage.yaml not found.");
      process.exit(1);
    }
    const yaml = fs.readFileSync(COVERAGE_YAML, "utf8");
    const entries = parseYaml(yaml);
    const errors = validate(entries);
    if (errors > 0) {
      console.error(`Validation failed with ${errors} error(s)`);
      process.exit(1);
    }
    console.log(`Validation passed (${entries.length} constructs)`);
    return;
  }

  // Phase 1: Get node types
  let raw;
  if (args.includes("--fetch") || !fs.existsSync(NODE_TYPES_CACHE)) {
    console.log("Fetching node-types.json from GitHub...");
    raw = await fetch(NODE_TYPES_URL);
    fs.writeFileSync(NODE_TYPES_CACHE, raw);
  } else {
    raw = fs.readFileSync(NODE_TYPES_CACHE, "utf8");
  }

  const nodeTypes = JSON.parse(raw);
  const allNamed = [...new Set(nodeTypes.filter((n) => n.named).map((n) => n.type))].sort();

  // Filter to meaningful constructs
  const constructs = allNamed.filter((name) => {
    if (EXCLUDE_SET.has(name)) return false;
    if (EXCLUDE_PATTERNS.some((p) => p.test(name))) return false;
    if (!CATEGORY_MAP[name]) return false; // Only include categorized constructs
    return true;
  });

  console.log(`Filtered ${allNamed.length} named nodes → ${constructs.length} AL constructs`);

  // Phase 2: Scan test suites
  const suites = scanTestSuites();
  console.log(`Found ${Object.keys(suites).length} test suites`);

  const assignments = autoAssign(constructs, suites);

  // Generate YAML
  const yaml = toYaml(constructs, assignments);
  fs.writeFileSync(COVERAGE_YAML, yaml);
  console.log(`Wrote ${COVERAGE_YAML}`);

  // Also render MD
  const entries = parseYaml(yaml);
  const md = renderMarkdown(entries);
  fs.writeFileSync(COVERAGE_MD, md);
  console.log(`Wrote ${COVERAGE_MD}`);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
