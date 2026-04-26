# Diagnostic Suppression Audit — #1365

This document records the audit of all three AL0275/AL0197 suppression branches in
`AlRunner/Program.cs` and the associated `DetectSameExtensionDuplicates` pre-scan in
`AlRunner/Pipeline.cs`. Conducted as part of issue #1365.

---

## Principle

**Forward every BC compilation error to the user unless the diagnostic is provably a runner
artifact.** Suppress only when:

1. The runner caused the duplicate (not the user's source), OR
2. The user explicitly opted into the override (e.g. `--stubs`).

Add supplementary pre/post-compile checks for errors that `alc` would emit but BC's API
misses in single-pass mode (e.g. same-extension extension-object name collisions). See
`DetectSameExtensionDuplicates` for the reference pattern.

---

## Case 1 — Self-duplicate package (`Program.cs` lines 1261-1301)

### Trigger
AL0275 or AL0197 fires AND `IsSelfDuplicateAmbiguity(message)` returns `true` — both
sides of the ambiguity reference the **same** publisher+name+version identity string.
Additionally gated on `hasExplicitPackages && allPackagePaths.Count > 0`.

### What it suppresses
When the packages directory contains the same `.app` file under two different GUIDs (or
the same identity appears twice in `depSpecs`), the BC compiler emits AL0275 naming the
same extension on both sides. The runner rebuilds the compilation with a
`PackageScanner`-deduplicated spec list.

### Classification
**Runner artifact.** The user did not ask for the same package twice. `PackageScanner`
already deduplicates proactively (by identity), but packages loaded via explicit
`SymbolReferenceSpecification` (depSpecs from app.json dependencies) bypass that scan.
The identity-tuple match is the correct trigger: the BC error message itself provides both
extension identities, and matching them string-for-string is precise.

### Narrowing applied
**None required.** The trigger already requires an exact string match of the full
publisher+name+version identity on both sides of the diagnostic. A user could not
legitimately produce this pattern without publishing two packages with identical identities
(which would be a BC publishing constraint violation anyway).

### Test coverage
Not testable in unit fixtures without real `.app` files. The mechanism is tested
indirectly by the fact that all bucket tests pass when run against repositories that have
duplicate `.alpackages` entries.

---

## Case 2 — Stubs-vs-package conflict (`Program.cs` lines 1303-1357)

### Trigger
AL0275 or AL0197 fires AND `Log.HasStubs` is `true` (user passed `--stubs`). Extracts
conflicting app names from the error messages and drops those packages so the stub wins.

### What it suppresses
When a `--stubs` file redefines an object that is also in a loaded package, the BC
compiler emits AL0275 because both the stub and the package define the same object. The
stub is the intentional override.

### Classification
**User-opted-in behavior.** Gated strictly on `--stubs` being passed by the user. Without
`--stubs`, this path never executes.

### Narrowing applied
**None required.** The `Log.HasStubs` gate is sufficient: the user explicitly requested
stub overrides. Any AL0275 in this context is a runner-artifact conflict between the stub
and its package counterpart, which is the expected and documented behavior of `--stubs`.

### Test coverage
Covered by the existing stubs test suite (`tests/stubs/39-stubs/`), which is run
separately with `--stubs`. See `tests/stubs/39-stubs/` for fixture.

---

## Case 3 — Cross-extension extension-object collision (`Program.cs` lines 1360-1427)

### Trigger
AL0197 fires for an extension-type object (`PageExtension`, `TableExtension`,
`ReportExtension`, `EnumExtension`, `PermissionSetExtension`, `ProfileExtension`,
`PageCustomization`) AND the object name appears with **2+ different** extension identity
strings in the AL0197 messages. Matching AL0275 errors for those names are also
suppressed.

### What it suppresses
When two different `.app` packages loaded as symbol references both define an extension
object with the same name, the BC compiler emits AL0197/AL0275 with two different
extension identities. In production BC these packages compile independently and can
legitimately share extension-object names.

### Classification
**Runner artifact (package scenario only).** In the single-pass compilation, all packages
are visible simultaneously, causing false extension-object name collisions. The 2+ unique
extension identity check correctly distinguishes this from the same-extension case.

### Important distinction: source-only multi-app scenario
When the user passes two source directories (each with their own `app.json`) and both
define an extension object with the same name, **the BC compiler assigns ONE extension
identity to all compiled objects** (the first app.json's identity). This means the AL0197
messages reference the same extension ID on both sides, and Case 3 does NOT fire (count
= 1, not 2+).

In this source-only scenario, line 1482 (`IsCrossExtensionDuplicateDeclaration` filter)
suppresses the error by excluding extension-type AL0197 from `genuineDuplicates`. This
is the correct behavior: two separate app sources compiled together — each defining the
same extension-object name — is a runner artifact of single-pass compilation. In real BC,
they compile separately and succeed.

The `DetectSameExtensionDuplicates` pre-scan correctly distinguishes the two sub-cases by
examining app.json file boundaries (same app.json = same extension = genuine error).

### Narrowing applied
**None required.** The 2+ extension identity check and the pre-scan together provide
sufficient precision:
- Same-extension dups → caught by pre-scan (exit 3)
- Cross-extension source dups → extension-type filter at line 1482 suppresses (runner artifact)
- Cross-extension package dups → Case 3 suppresses (runner artifact)
- Non-extension-type dups (Codeunit, Table, Page, etc.) → always forwarded as genuine errors

### Test coverage
- `tests/bucket-1/codeunit-runtime/130-cross-ext-al0275/` — two apps with same pageextension name compile
  successfully (exit 0)
- `tests/excluded/135-same-ext-pageext-duplicate/` — same-extension pageextension dup
  exits 3
- `tests/excluded/137-same-ext-profileext-duplicate/` — same-extension profileextension
  dup exits 3 (new, added in #1365)
- `tests/excluded/138-cross-ext-pageext-suppressed/` — two apps with same pageextension
  name in source-only mode exits 0 (new, added in #1365)
- `tests/excluded/131-genuine-codeunit-collision/` — same-name Codeunit across two apps
  exits 3 (non-extension type: not suppressed)

---

## Backfill inventory — `DetectSameExtensionDuplicates`

The pre-scan (`Pipeline.cs:DetectSameExtensionDuplicates`) catches same-extension
extension-object name duplicates before the BC compiler runs, because the BC compiler in
single-pass mode cannot distinguish same-extension from cross-extension duplicates.

### Gap found: `profileextension` not covered (fixed in #1365)

**Before this fix:** `ExtensionObjectDeclPattern` only matched the numbered extension types
(`pageextension`, `tableextension`, `reportextension`, `enumextension`,
`permissionsetextension`, `pagecustomization`). The `profileextension` keyword uses
different AL syntax (no numeric ID, unquoted identifier name):

```al
profileextension MyExt extends SomePage { ... }    // ← no number, unquoted name
```

The BC compiler correctly emits AL0197 for same-extension `profileextension` duplicates,
but the `IsCrossExtensionDuplicateDeclaration` filter at line 1482 classified
`ProfileExtension` as a suppress-eligible type, causing the error to be silently eaten.

**Fix applied:** Added `ProfileExtensionDeclPattern` (a separate regex for the distinct
`profileextension` syntax) to `Pipeline.cs` and registered it in `ExtensionTypeDisplayNames`.
`DetectSameExtensionDuplicates` now checks both the numbered pattern and the profile pattern.

### Other extension types — all covered

| Type | In `ExtensionObjectDeclPattern` | In `DiagnosticClassifier.ExtensionObjectTypes` | Status |
|---|---|---|---|
| `pageextension` | ✓ | ✓ | Covered |
| `tableextension` | ✓ | ✓ | Covered |
| `reportextension` | ✓ | ✓ | Covered |
| `enumextension` | ✓ | ✓ | Covered |
| `permissionsetextension` | ✓ | ✓ | Covered |
| `pagecustomization` | ✓ | ✓ | Covered |
| `profileextension` | ✗ (missing — **fixed**) | ✓ | Fixed in #1365 |

### Other `alc`-vs-BC-API gaps investigated

- **Table/Enum/Codeunit/Page name collisions across extensions**: BC's API correctly emits
  AL0197 with non-extension types (Codeunit, Table, etc.), and line 1482 forwards them as
  genuine errors. No gap.
- **Object name clashes between app source and a non-extension stub**: stubs loaded without
  `--stubs` are included in the compilation; BC emits AL0197 if they clash with source
  objects. The stub replacement logic in `Pipeline.cs:Transpile` removes source objects
  before adding stubs, preventing the clash. No gap.
- **Enum extension value dups**: BC API emits the appropriate diagnostic. No same-extension
  pre-scan needed because enum extension values are keyed on the value name within one
  extension, which is already enforced by the BC compiler. No gap.
- **Profile dups (profile, not profileextension)**: `profile` objects are not extension
  types. Same-name `profile` objects across extensions would produce AL0197 for the
  `Profile` type (non-extension) and would be forwarded as genuine errors. No gap.

---

## Summary of changes

| Area | Change |
|---|---|
| `AlRunner/Pipeline.cs` | Added `ProfileExtensionDeclPattern` regex for `profileextension` syntax |
| `AlRunner/Pipeline.cs` | Added `"profileextension" → "ProfileExtension"` to `ExtensionTypeDisplayNames` |
| `AlRunner/Pipeline.cs` | `DetectSameExtensionDuplicates` now checks both patterns |
| `AlRunner/Program.cs` | Improved comment on line 1482 `genuineDuplicates` filter to document the architecture |
| `tests/excluded/137-same-ext-profileext-duplicate/` | New fixture: same-ext profileextension dup exits 3 |
| `tests/excluded/138-cross-ext-pageext-suppressed/` | New fixture: cross-ext pageextension source dup exits 0 (runner artifact) |
| `docs/coverage.yaml` | Added diagnostic suppression entries |

Cases 1 and 2 required no code changes — they were already correctly narrow.
