#!/usr/bin/env node
// Plain-Node unit tests for scripts/coverage-gen.js. No test framework — each
// check uses assert and prints PASS/FAIL. Run: `node scripts/tests/coverage-gen.test.js`

const assert = require("assert");
const fs = require("fs");
const path = require("path");
const os = require("os");
const g = require("../coverage-gen.js");

let passed = 0, failed = 0;
function test(name, fn) {
  try { fn(); console.log(`  PASS  ${name}`); passed++; }
  catch (e) { console.error(`  FAIL  ${name}\n        ${e.message}`); failed++; }
}

console.log("parseYaml");
test("parses layer field", () => {
  const yaml = `
foo_statement:
  layer: syntax
  category: statement
  status: covered
  tests:
    - tests/bucket-1/01-x
  notes: some note
`;
  const [e] = g.parseYaml(yaml);
  assert.strictEqual(e.name, "foo_statement");
  assert.strictEqual(e.layer, "syntax");
  assert.strictEqual(e.category, "statement");
  assert.strictEqual(e.status, "covered");
  assert.deepStrictEqual(e.tests, ["tests/bucket-1/01-x"]);
  assert.strictEqual(e.notes, "some note");
});

test("parses runtime-api 'Type.Method' names", () => {
  const yaml = `
RecordRef.FindSet:
  layer: runtime-api
  category: RecordRef
  status: covered
  notes: overloads=2
`;
  const [e] = g.parseYaml(yaml);
  assert.strictEqual(e.name, "RecordRef.FindSet");
  assert.strictEqual(e.layer, "runtime-api");
});

test("tolerates legacy entries without layer", () => {
  const yaml = `
legacy_entry:
  category: object
  status: gap
`;
  const [e] = g.parseYaml(yaml);
  assert.strictEqual(e.name, "legacy_entry");
  assert.strictEqual(e.layer, undefined); // caller defaults to "syntax"
});

console.log("\nemitYaml + parseYaml round-trip");
test("status, tests, notes, layer survive round-trip", () => {
  const original = [
    { name: "a.b", layer: "runtime-api", category: "a", status: "covered", tests: [], notes: "overloads=3" },
    { name: "if_statement", layer: "syntax", category: "statement", status: "covered", tests: ["tests/x", "tests/y"] },
  ];
  const yaml = g.emitYaml(original, ["# header"]);
  const parsed = g.parseYaml(yaml);
  assert.strictEqual(parsed.length, 2);
  const byName = new Map(parsed.map(e => [e.name, e]));
  const a = byName.get("a.b");
  assert.strictEqual(a.layer, "runtime-api");
  assert.strictEqual(a.notes, "overloads=3");
  const iff = byName.get("if_statement");
  assert.deepStrictEqual(iff.tests.sort(), ["tests/x", "tests/y"]);
});

console.log("\nmergeEntries");
test("preserves curated status when generator auto-detects 'gap'", () => {
  const generated = [{ name: "x", layer: "syntax", category: "o", status: "gap", tests: [] }];
  const existing  = [{ name: "x", layer: "syntax", category: "o", status: "out-of-scope",
                       tests: [], notes: "architectural" }];
  const [merged] = g.mergeEntries(generated, existing);
  assert.strictEqual(merged.status, "out-of-scope");
  assert.strictEqual(merged.notes, "architectural");
});

test("preserves curated test list when generator finds a superset", () => {
  const generated = [{ name: "x", layer: "syntax", category: "o", status: "covered",
                       tests: ["a", "b", "c", "d", "e"] }];
  const existing  = [{ name: "x", layer: "syntax", category: "o", status: "covered",
                       tests: ["a", "c"] }];
  const [merged] = g.mergeEntries(generated, existing);
  assert.deepStrictEqual(merged.tests, ["a", "c"]);
});

test("falls back to generator values when entry has no prior record", () => {
  const generated = [{ name: "new", layer: "syntax", category: "o", status: "gap", tests: [] }];
  const [merged] = g.mergeEntries(generated, []);
  assert.strictEqual(merged.status, "gap");
});

console.log("\nvalidate");
test("rejects entry missing layer", () => {
  const errs = captureErrors(() => g.validate([{ name: "x", category: "o", status: "gap", tests: [] }]));
  assert.ok(errs.some(e => /missing 'layer'/.test(e)), `got: ${errs.join("; ")}`);
});

test("rejects syntax 'covered' with no tests", () => {
  const errs = captureErrors(() => g.validate([
    { name: "x", layer: "syntax", category: "o", status: "covered", tests: [] },
  ]));
  assert.ok(errs.some(e => /covered.*no test suites/.test(e)));
});

test("rejects runtime-api entry without 'Type.Method' format", () => {
  const errs = captureErrors(() => g.validate([
    { name: "notqualified", layer: "runtime-api", category: "X", status: "gap", tests: [] },
  ]));
  assert.ok(errs.some(e => /must be 'Type.Method'/.test(e)));
});

test("accepts well-formed entries", () => {
  const errs = captureErrors(() => g.validate([
    { name: "RecordRef.FindSet", layer: "runtime-api", category: "RecordRef", status: "covered", tests: [] },
  ]));
  assert.strictEqual(errs.length, 0);
});

console.log("\nextractAlMethodsFromMock");
test("finds AL-prefixed declarations in a sample file", () => {
  const tmp = path.join(os.tmpdir(), `alr-${Date.now()}.cs`);
  fs.writeFileSync(tmp, `
public class MockFoo {
  public void ALOpen(int x) { }
  public int ALLength => 0;
  public static void ALRead(ref int x);
  public static MockFoo Default = new();   // not AL-prefixed
  private void InternalHelper() { }         // not AL-prefixed
  public void ALAddText(string s, int pos = 1) { }
}`);
  try {
    const syms = g.extractAlMethodsFromMock(tmp);
    assert.ok(syms.has("ALOpen"));
    assert.ok(syms.has("ALLength"));
    assert.ok(syms.has("ALRead"));
    assert.ok(syms.has("ALAddText"));
    assert.ok(!syms.has("Default"));
    assert.ok(!syms.has("InternalHelper"));
  } finally { fs.unlinkSync(tmp); }
});

test("returns empty set for non-existent file", () => {
  const syms = g.extractAlMethodsFromMock("/nonexistent/path.cs");
  assert.strictEqual(syms.size, 0);
});

console.log("\nbuildRuntimeApiCoverage");
test("marks not-possible methods regardless of mock presence", () => {
  // Shim: we can't easily inject mock files, but we can verify
  // not-possible types produce not-possible entries.
  const api = {
    types: {
      Debugger: { methods: [{ name: "Breakpoint", overloads: 1 }] },
    },
  };
  const out = g.buildRuntimeApiCoverage(api);
  assert.strictEqual(out["Debugger.Breakpoint"].status, "not-possible");
});

test("marks types with no mock mapping as gap", () => {
  const api = {
    types: {
      ZzzUnknownType: { methods: [{ name: "DoStuff", overloads: 1 }] },
    },
  };
  const out = g.buildRuntimeApiCoverage(api);
  assert.strictEqual(out["ZzzUnknownType.DoStuff"].status, "gap");
});

console.log(`\n${passed} passed, ${failed} failed`);
process.exit(failed > 0 ? 1 : 0);

function captureErrors(fn) {
  const errs = [];
  const orig = console.error;
  console.error = (...args) => errs.push(args.join(" "));
  try { fn(); } finally { console.error = orig; }
  return errs;
}
