# App-package compile pipeline

A shipped `.app` is compiled from its own `src/` with BC's own compiler, on every BC build the
test matrix runs, and the result is judged on three counts. This is point 3 of #3530, tracked
in #4495.

| piece | what it does |
|---|---|
| `tools/metadata-ground-truth/fixtures/<name>/` | a package fixture: `NavxManifest.xml`, `src/`, resources, and `expected.json` |
| `tools/pack-app-fixture.py` | packs a fixture into a NAVX `.app`, entry names **verbatim** |
| `tools/metadata-ground-truth` | the compile: BC's compiler over the package, configured from the package's manifest |
| `tools/check-app-compile-bundle.py` | the verdict: emit errors, object count, table-metadata count |
| `tools/app-package-pipeline.sh` | runs the three above for every fixture on one BC build |
| `bc-tests.yml`, step "App-package compile pipeline (#4495)" | runs the script on every leg |

## What a pass means

All three of these, per fixture, per BC build:

1. **Emit reported zero errors.**
2. **The table-metadata count equals `expected.json`'s `tables`.**
3. **The object count equals `expected.json`'s `objects`.**

### Why the error count is not implied by the other two

#3530 argues that emit is atomic per module, so a broken setup yields zero objects. That is
true of the runner's own compile path, which retries and then refuses a partial module, and it
is **not** true of this one. `tools/metadata-ground-truth` compiles with
`continueBuildOnError: true` and captures every object the emitter hands over, so a package that
fails still produces its objects. Measured on BC `28.4.53241.53989` with the fixture below:

| what was broken | emit | objects | tables |
|---|---|---|---|
| nothing | 0 errors | 6 | 3 |
| the percent-encoded layout entry not decoded | 1 × `AL1081` | 6 | 3 |
| `ContextSensitiveHelpUrl` removed from the manifest | 1 × `AL0543` | 6 | 3 |
| the add-in script's case changed on disk | 1 × `AL0327` | 6 | 3 |
| `<PreprocessorSymbols>` removed from the manifest | 0 errors | 5 | 2 |

So a check of "objects > 0 and table metadata present" passes three of the four broken rows.
The error count catches those three; the exact table count catches the fourth, which has no
error at all.

## The fixture: third-party-shaped, not third-party

`third-party-shaped` is written in this repository, so its licence is not a question. It is
published as `AL Runner Fixtures`, not `Microsoft`, and each quirk #3530 measured on vendor apps
is in it once:

| quirk | where | fails as |
|---|---|---|
| a manifest `<PreprocessorSymbol>` gating a table | `src/FixtureGated.Table.al` | one table fewer, no error |
| `ContextSensitiveHelpPage` needing the manifest's help URL | `src/FixtureHeaderCard.Page.al` | `AL0543` |
| a layout entry shipped percent-encoded, declared with `\` and a literal space | `layout/TP%20Fixture%20Report.rdlc`, `src/FixtureReport.Report.al` | `AL1081` |
| a script declared in lower case, shipped in mixed case | `js/TPFixture.js`, `src/FixtureAddin.ControlAddin.al` | `AL0327` |

The control add-in is declared but is not an application object the emitter hands over, which
is why `expected.json` says 6 objects for 7 declarations.

**The percent-encoded entry was a real defect in the compile path.** `LayeredFileSystem`
decoded the *requested* path but indexed the entries on disk under their encoded names, so the
fixture failed with `AL1081` until it also indexed each entry under
`AppPackageCompileSetup.NormalizeEntryName`. That file is linked into the tool as source,
the same way `EngineClosure.cs` is, so it must stay free of runner dependencies.

**Adding a fixture** is a directory under `tools/metadata-ground-truth/fixtures/` with an
`expected.json`. The script finds it; nothing else needs editing.

## Where it runs, and why not in `AlRunner.Tests`

A workflow step, on every leg. Not a C# test, for three reasons:

- **The subject is a BC build.** `tools/metadata-ground-truth` builds against one artifact
  directory's compiler (`-p:ServiceTierPath`). `AlRunner.Tests` runs only on the unit legs, the
  newest minor of each major, so a C# test would see two builds where the matrix runs up to eight.
- **It needs no Base Application.** The fixture references only the platform's `System.app`,
  so it never loads the closure `.claude/rules/no-base-app-in-csharp-tests.md` keeps out of the
  C# suite. It costs a few seconds per fixture, plus one build of the tool on legs that did not
  already build it for the ground truth.
- **The ground-truth step already hosts the same compiler there**, and this reuses its build.

The script's own logic is tested by `tools/test_check_app_compile_bundle.py`, which the
`tools/ unit tests` gate discovers by name.

## What this does not cover yet (#4495 stays open)

- **Real vendor apps.** The fixture reproduces the quirks #3530 found on vendor apps. It is not
  one. Which vendor packages a CI leg may download or vendor is a licensing decision, and
  #4495 records it as the first open question.
- **`InternalsVisibleTo` identity** (#3530, `AL0161`). Reproducing it needs a second package
  that grants access to the fixture and ships symbols the compiler can reference, which the
  packer does not build yet.
- **Base Application.** It stays out on cost. `tests/expectations/metadata-equivalence/apps.json`
  records the measurement and the sizing decision it needs (#3876).
