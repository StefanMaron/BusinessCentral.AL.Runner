# Dependency Slicing — `al-runner extract-deps`

`extract-deps` extracts the minimal reachable subset of objects from BA/SA/ISV
artifacts that your extension actually touches, compiles them into a pinned DLL,
and uses that DLL in the runner instead of blank shells.

---

## The problem it solves

The runner auto-generates **blank shells** for every BA/SA object your extension
references: every method exists, every procedure returns a type default (`0`, `''`,
`false`), and nothing executes. This means tests that call BA logic can pass silently
while asserting against meaningless values — a false positive that is only caught
later in a full BC pipeline run.

`extract-deps` replaces blank shells with real compiled BA logic for the exact
objects your extension touches. Tests that call into the extracted slice run the
actual BA code. Tests that call objects outside the slice fail with an explicit
error (see `--fail-on-stub`, issue #1519) rather than silently passing.

---

## What gets extracted

The extractor walks the syntax tree of your extension and collects:

| Pattern | What is extracted |
|---|---|
| `var x: Record "Sales Header"` | `Table "Sales Header"` definition |
| `tableextension … extends "Sales Header"` | All BA tableextensions of every table in the slice |
| `Codeunit::"Sales-Post"` / `var C: Codeunit "Sales-Post"` | `Codeunit "Sales-Post"` definition |
| `field(1; Status; Enum "Sales Line Type")` | `Enum "Sales Line Type"` definition |
| `enumextension … extends "Sales Line Type"` | All BA enumextensions of every enum in the slice |
| `var P: Page "Customer Card"` / `Page.Run(Page::"Customer Card")` | `Page "Customer Card"` definition |
| `pageextension … extends "Customer Card"` | All BA pageextensions of every page in the slice |
| `var R: Report "Sales Invoice"` / `Report.Run(Report::…)` | `Report "Sales Invoice"` definition |
| `reportextension … extends "Sales Invoice"` | All BA reportextensions of every report in the slice |
| `var Q: Query "My Query"` / `Query::"My Query"` | `Query "My Query"` definition |
| `var X: XmlPort "Export Data"` / `XmlPort::…` | `XmlPort "Export Data"` definition |
| `procedure Run(Handler: Interface IMyHandler)` | `Interface IMyHandler` definition |
| `[EventSubscriber(ObjectType::Table, Database::"Sales Header", 'OnAfterInsert', …)]` | `Sales Header` table (event must fire for the subscriber to be reached) |
| `[EventSubscriber(ObjectType::Page, …)]` / codeunit/report variants | The referenced page, codeunit, or report |
| BA subscribers to events on objects in the slice | Pulled in automatically by scanning all artifact source for `[EventSubscriber]` attributes targeting slice objects |

The extractor iterates to a **fixpoint**: new objects pulled in may themselves
reference further objects, and newly added objects may have their own event
subscribers. The loop terminates when no new objects are found in a full scan.

---

## CLI usage

### Full Microsoft stack + ISV

Microsoft ships four apps together every wave with interdependencies between them.
Extract and compile them all at once into a single versioned DLL:

```sh
# Step 1 — extract the minimal vertical from all four Microsoft apps
# Output lands in ./deps/src as plain AL files you can inspect and edit.
al-runner --extract-deps ./src ./deps/src \
  ".alpackages/Microsoft_System Application_25.0.app" \
  ".alpackages/Microsoft_Business Foundation_25.0.app" \
  ".alpackages/Microsoft_Base Application_25.0.app" \
  ".alpackages/Microsoft_Application_25.0.app"

# Step 2 — (optional) review deps/src/
# Typical result: ~150–200 files instead of ~8 000.
# Delete methods that reference DotNet types you don't need,
# or adjust anything that won't compile cleanly.

# Step 3 — compile to a pinned Microsoft DLL
al-runner --compile-dep ./deps/src ./deps --packages .alpackages

# deps/src.dll is now your pinned Microsoft 25.0 dependency.
# Commit deps/src/ (AL source) and deps/src.dll to your repo.
```

### Adding an ISV dependency

Each ISV app gets its own DLL. The ISV slice references the Microsoft DLL,
so pass it via `--packages` during compilation:

```sh
# Extract ISV slice — accepts a .app artifact or a source directory
al-runner --extract-deps ./src ./deps/isv-src \
  "/path/to/Continia_Document Capture_25.0.app"

# Compile — --packages gives the compiler access to the Microsoft DLL
al-runner --compile-dep ./deps/isv-src ./deps --packages .alpackages

# Run tests with all DLLs
al-runner --dep-dlls ./deps ./src ./test
```

### Dependency source types

Inputs to `--extract-deps` can be:
- **`.app` artifact files** — runner extracts AL source from the ZIP; use for
  Microsoft packages and AppSource ISV apps.
- **Directories containing AL source** — read directly; use for ISV apps where
  you have the source tree available locally.

---

## DLL grouping model

Combine all Microsoft apps into **one DLL per BC version**. Each ISV app gets its
own DLL. This mirrors the versioning boundaries: Microsoft ships their entire stack
as a coherent versioned unit; ISVs version independently.

```
deps/
  bc-25.0-slice.dll        ← System App + Base App + Business Foundation (your vertical only)
  isv-continia-25.0.dll    ← Continia slice (MetadataReference → bc-25.0-slice.dll)
  isv-other-4.2.dll        ← another ISV, pinned independently
```

**No duplicate objects across DLLs.** Every AL object belongs to exactly one source
app. An object is extracted into the DLL that owns it, never into a DLL that merely
references it. `Customer` is owned by Base Application → it goes in the Microsoft
DLL only. ISV tableextensions of `Customer` are owned by the ISV → they go in the
ISV DLL, which holds a `MetadataReference` to the Microsoft DLL.

---

## Developer workflow

```
1.  al-runner --extract-deps ./src ./deps/src <apps...>
        → writes AL files to ./deps/src/; ~150-200 files for a typical sales extension

2.  Review deps/src/ (optional)
        → diff is meaningful: only objects your code touches
        → delete DotNet-heavy methods you don't need, fix anything that won't compile

3.  al-runner --compile-dep ./deps/src ./deps --packages .alpackages
        → compiles to deps/src.dll; fails loudly on errors

4.  Commit deps/src/ and deps/src.dll
        → pinned, reproducible dependency; anyone cloning the repo can run tests immediately

5.  On BC version upgrade:
        re-run --extract-deps against the new artifacts
        diff deps/src/ → review what changed in your vertical only
        recompile → new pinned DLL
```

### Whole objects, not method slices

Objects are extracted in full. If your code calls one method on
`Codeunit "Sales-Post"`, you get the entire `Sales-Post` source file — all its
procedures, including ones you never call and any DotNet-interop methods that won't
compile in standalone mode. Partial objects would be invalid AL, so there is no
alternative.

This is why the review step exists. On first extraction, scan the output for
procedures that reference DotNet types you have no use for and delete them before
compiling. The edit is versioned: on the next BC upgrade, the diff will show if
that procedure changed, and you decide again.

If `compile-dep` reports errors after extraction, inspect them:

- **Missing type reference** — an extracted object references something outside the
  slice (e.g. a DotNet type, or a table not yet in the slice). Either add the missing
  object to the slice or remove the incompatible code from the extracted AL file.
  This is an intentional, versioned edit — not a workaround.
- **Duplicate definition** — two source files define the same object. Check the
  ownership rule: one should be in a different DLL group.

Compilation errors are signals, not bugs. The extracted AL files are first-class
reviewable artifacts; manual edits to them are deliberate decisions about what
your dependency slice contains.

### Slice completeness and the self-healing feedback loop

The BFS extraction is **transitively complete for static references**. If object A
calls B which calls C, and A is in your slice, B and C will be found and extracted
automatically.

The one residual gap is **dynamic dispatch** — `Codeunit.Run(someDynamicId)` where
the ID is computed at runtime. The extractor cannot resolve that statically, so the
target codeunit won't be in the slice.

This gap surfaces through two mechanisms, in order of speed:

1. **`--fail-on-stub`** — the dynamically dispatched codeunit hits a blank shell
   and throws `RunnerGapException`. The test fails explicitly, naming the missing
   object. Fast feedback, no silent pass.
2. **Nightly BC run** — the same test passes in the runner but fails against real
   BC, signalling a divergence in the slice.

The fix in both cases: rewrite the test or the production code to reference the
missing object statically (`Codeunit::"The Missing One"` instead of
`Codeunit.Run(dynamicId)`). The next `--extract-deps` run picks it up automatically
and it enters the slice permanently.

The loop **self-heals**. Every gap discovered makes the slice more complete. Over
time the nightly divergences shrink toward zero, and you are left with a
machine-verified description of your extension's true runtime dependency surface —
not a hand-maintained list of stubs, but a graph extracted from the actual BA source
and validated against real BC.

---

## CI pattern

The recommended CI setup uses the runner for fast PR feedback and a real BC
container for nightly ground-truth verification:

```
PR gate (seconds):
  al-runner --dep-dlls ./deps --fail-on-stub ./src ./test
  → logic errors caught immediately; objects outside slice fail loudly

Nightly scheduled job:
  Full BC sandbox container, same test suite
  → catches anything the runner cannot model (transactions, DotNet interop, etc.)
  → allowed to fail without blocking merge
```

The runner's job is to be a fast filter, not a faithful BC emulator. The nightly
BC run catches the residual gap. Together they give fast feedback on every PR and
ground-truth coverage once a day.

---

## Scope and limitations

- **All standard object types** are extracted: tables, tableextensions, codeunits,
  enums, enumextensions, pages, pageextensions, reports, reportextensions, queries,
  xmlports, and interfaces.
- **Event subscribers** to table events (platform-triggered `OnAfterInsert`,
  `OnBeforeModify`, etc.) and to integration/business events on codeunits in the
  slice are included.
- **DotNet interop** inside extracted objects will cause `compile-dep` to fail.
  Remove those methods from the extracted AL or move them behind an interface.
- **Transaction semantics** are not emulated even with the slice in place. See
  `docs/limitations.md` for the full list of architectural limits.
- **Transitive closure size** is typically much smaller than the full app stack.
  A sales-adjacent extension touching `Customer` and `Sales Header` produces
  ~150–200 objects out of ~8 000 in Base Application.
