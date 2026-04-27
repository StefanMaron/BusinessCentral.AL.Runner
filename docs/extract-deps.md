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
| `Codeunit::"Sales-Post"` | `Codeunit "Sales-Post"` definition |
| `field(1; Status; Enum "Sales Line Type")` | `Enum "Sales Line Type"` definition |
| `enumextension … extends "Sales Line Type"` | All BA enumextensions of every enum in the slice |
| `[EventSubscriber(ObjectType::Table, Database::"Sales Header", 'OnAfterInsert', …)]` | The `Sales Header` table (event must fire for the subscriber to be reached) |
| BA subscribers to events published by objects in the slice | Pulled in automatically — event subscribers are found by scanning all artifact source for `[EventSubscriber]` attributes targeting objects already in the slice |

The extractor iterates to a **fixpoint**: new objects pulled in may themselves
reference further objects, and newly added objects may have their own event
subscribers. The loop terminates when no new objects are found in a full scan.

---

## CLI usage

```sh
# Step 1 — extract the slice and write reviewable AL source
al-runner extract-deps \
  --input ./src \
  --out ./extracted-deps \
  Microsoft_Base Application_25.0.app \
  Microsoft_System Application_25.0.app \
  /path/to/isv-source-dir

# Step 2 — compile to a pinned DLL
al-runner compile-dep ./extracted-deps ./deps/bc-25.0-slice.dll

# Step 3 — run tests against the pinned slice
al-runner --dep-dlls ./deps ./src ./test
```

Dependency sources can be:
- **`.app` artifact files** — the runner extracts AL source from the ZIP; used for
  Microsoft packages downloaded from the artifact feed.
- **Directories containing AL source** — read directly; used for ISV apps where
  you have the source tree.

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
1.  al-runner extract-deps …        → writes AL files to ./extracted-deps/
2.  Review the AL (optional)        → diff is meaningful: only objects your code touches
3.  al-runner compile-dep …         → compiles to a pinned DLL; fails loudly on errors
4.  Commit deps/*.dll (or the AL)   → pinned, reproducible CI dependency
5.  On BC version upgrade:
      re-run extract-deps against new artifacts
      diff the AL → review what changed in the objects you touch
      recompile
```

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

- **Tables, tableextensions, codeunits, enums, enumextensions** are extracted.
  Pages, reports, and xmlports are not currently collected as references (they can
  be added; file an issue if you need them).
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
