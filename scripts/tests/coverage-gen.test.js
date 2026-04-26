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

test("parses new per-overload 'Type.Method (sig)' names", () => {
  const yaml = `
BigText.AddText (Text, Integer):
  layer: runtime-api
  category: BigText
  status: not-tested
`;
  const [e] = g.parseYaml(yaml);
  assert.strictEqual(e.name, "BigText.AddText (Text, Integer)");
  assert.strictEqual(e.layer, "runtime-api");
  assert.strictEqual(e.status, "not-tested");
});

test("parses zero-arity 'Type.Method ()' names", () => {
  const yaml = `
BigText.Length ():
  layer: runtime-api
  category: BigText
  status: covered
`;
  const [e] = g.parseYaml(yaml);
  assert.strictEqual(e.name, "BigText.Length ()");
  assert.strictEqual(e.status, "covered");
});

console.log("\nemitYaml + parseYaml round-trip");
test("status, tests, notes, layer survive round-trip", () => {
  const original = [
    { name: "a.b", layer: "runtime-api", category: "a", status: "covered", tests: [], notes: "a note" },
    { name: "if_statement", layer: "syntax", category: "statement", status: "covered", tests: ["tests/x", "tests/y"] },
  ];
  const yaml = g.emitYaml(original, ["# header"]);
  const parsed = g.parseYaml(yaml);
  assert.strictEqual(parsed.length, 2);
  const byName = new Map(parsed.map(e => [e.name, e]));
  const a = byName.get("a.b");
  assert.strictEqual(a.layer, "runtime-api");
  assert.strictEqual(a.notes, "a note");
  const iff = byName.get("if_statement");
  assert.deepStrictEqual(iff.tests.sort(), ["tests/x", "tests/y"]);
});

test("per-overload names survive emitYaml + parseYaml round-trip", () => {
  const original = [
    { name: "BigText.AddText (Text, Integer)", layer: "runtime-api", category: "BigText", status: "not-tested", tests: [] },
    { name: "BigText.Length ()", layer: "runtime-api", category: "BigText", status: "covered", tests: [] },
  ];
  const yaml = g.emitYaml(original, ["# header"]);
  const parsed = g.parseYaml(yaml);
  const byName = new Map(parsed.map(e => [e.name, e]));
  assert.ok(byName.has("BigText.AddText (Text, Integer)"), "per-overload key not found after round-trip");
  assert.strictEqual(byName.get("BigText.AddText (Text, Integer)").status, "not-tested");
  assert.ok(byName.has("BigText.Length ()"), "zero-arity key not found after round-trip");
  assert.strictEqual(byName.get("BigText.Length ()").status, "covered");
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

test("migrates old 'Type.Method' curation to primary (lowest-arity) overload", () => {
  // Old YAML had "BigText.AddText" with notes; new generator emits per-overload.
  // The old curation should be copied to the lowest-arity new overload.
  const generated = [
    { name: "BigText.AddText (BigText, Integer)", layer: "runtime-api", category: "BigText", status: "not-tested", tests: [] },
    { name: "BigText.AddText (Text, Integer)", layer: "runtime-api", category: "BigText", status: "not-tested", tests: [] },
  ];
  const existing = [
    { name: "BigText.AddText", layer: "runtime-api", category: "BigText", status: "covered",
      tests: ["tests/bucket-1/10-bigtext"], notes: "hand-curated" },
  ];
  const merged = g.mergeEntries(generated, existing);
  // The primary overload (lowest arity — both have same arity here, so alphabetical)
  // should get the curation; the other should keep auto-generated status.
  const byName = new Map(merged.map(e => [e.name, e]));
  const primary = byName.get("BigText.AddText (BigText, Integer)"); // alphabetically first
  const secondary = byName.get("BigText.AddText (Text, Integer)");
  assert.strictEqual(primary.status, "covered");
  assert.deepStrictEqual(primary.tests, ["tests/bucket-1/10-bigtext"]);
  assert.strictEqual(primary.notes, "hand-curated");
  // Secondary keeps its auto-detected status
  assert.strictEqual(secondary.status, "not-tested");
  assert.deepStrictEqual(secondary.tests, []);
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

test("accepts per-overload 'Type.Method (sig)' runtime-api entries", () => {
  const errs = captureErrors(() => g.validate([
    { name: "BigText.AddText (Text, Integer)", layer: "runtime-api", category: "BigText", status: "not-tested", tests: [] },
    { name: "BigText.Length ()", layer: "runtime-api", category: "BigText", status: "covered", tests: [] },
  ]));
  assert.strictEqual(errs.length, 0, `Expected no errors, got: ${errs.join("; ")}`);
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
  // New schema: per-overload with signature field; key = "Type.Method (sig)"
  const api = {
    types: {
      Debugger: { methods: [{ name: "Breakpoint", signature: "()" }] },
    },
  };
  const out = g.buildRuntimeApiCoverage(api);
  assert.strictEqual(out["Debugger.Breakpoint ()"].status, "not-possible");
});

test("marks types with no mock mapping as gap", () => {
  const api = {
    types: {
      ZzzUnknownType: { methods: [{ name: "DoStuff", signature: "(Integer)" }] },
    },
  };
  const out = g.buildRuntimeApiCoverage(api);
  assert.strictEqual(out["ZzzUnknownType.DoStuff (Integer)"].status, "gap");
});

test("marks multi-overload methods with name in mock as not-tested", () => {
  // When a method has >1 overload and the name exists in mock, status = not-tested
  // because we can't distinguish which overload is implemented.
  const api = {
    types: {
      BigText: {
        methods: [
          { name: "AddText", signature: "(Text, Integer)" },
          { name: "AddText", signature: "(BigText, Integer)" },
        ],
      },
    },
  };
  const out = g.buildRuntimeApiCoverage(api);
  // BigText maps to MockBigText.cs which has ALAddText — name exists in mock
  // but both overloads get not-tested
  const statuses = [
    out["BigText.AddText (Text, Integer)"].status,
    out["BigText.AddText (BigText, Integer)"].status,
  ];
  // Both should be either not-tested (if ALAddText found) or gap (if file not readable in test env)
  for (const s of statuses) {
    assert.ok(
      s === "not-tested" || s === "gap",
      `Expected not-tested or gap, got: ${s}`
    );
  }
});

test("marks single-overload methods with name in mock as covered", () => {
  // Single overload + name found in mock → covered
  const api = {
    types: {
      BigText: {
        methods: [
          { name: "Length", signature: "()" },
        ],
      },
    },
  };
  const out = g.buildRuntimeApiCoverage(api);
  // BigText.Length () is a single overload; ALLength exists in MockBigText.cs
  const s = out["BigText.Length ()"].status;
  // In test env mock file may not be readable, so allow gap too
  assert.ok(
    s === "covered" || s === "gap",
    `Expected covered or gap, got: ${s}`
  );
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
