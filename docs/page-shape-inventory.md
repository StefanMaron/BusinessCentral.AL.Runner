# What the runner knows about a page's shape, and where that knowledge comes from

Written for issue #3735, which asked why `BuiltInPageModeActionRule.RefuseUnknownPageType`
exists and whether any AL can reach it. The short answer is in the code, at the refusal site
(`AlRunner/Patches/MockTestPage.LivePage.Actions.cs`); this is the long form, and the place to
re-check when either inventory changes.

## The two inventories

A page id resolves its shape — `PageType`, `SourceTable`, `CardPageId` — through exactly two
sources, both read by `RecordPatches.TryGetAnyPageType` and by `RecordPatches.IsPageShapeKnown`:

1. **Parsed AL source.** `_parsedPages`, written only by `RecordPatches.AlPageParser`'s
   `ParseSourceFileIntoAllExtractors`, whose callers walk `_sourceDirs` — the directories
   `RecordPatches.AddSourceDirs` was given.
2. **A loaded dependency's page symbols.** `BcAppSymbolCache.TryParsePageSymbol`, over the
   `SymbolReference.json` inside each registered `.app`.

Neither can answer a null `PageType`: both apply AL's absent-property default of `Card`. So
"`TryGetAnyPageType` is null" and "`IsPageShapeKnown` is false" are the same statement — pinned
by `AlRunner.Tests/LiveTestPagePageTypeKnownTests.cs`.

## The three routes to a LiveNavTestPage

| route | built by | shape gate |
|---|---|---|
| 1. live over a record | `CodeunitPatches.CreateTestPageClient` → `TestPageFactory.TryBuild` | a resolvable source table, looked up through the same two inventories |
| 2. live, recordless | `CodeunitPatches.CreateTestPageClient` | `IsPageShapeKnown` outright |
| 3. handler-driven | `RunnerTestClientSession.GetPage`, for `[PageHandler]`/`[ModalPageHandler]` | **none** — its only gate is form construction (`CodeunitPatches.FindFormType`) |

Routes 1 and 2 cannot reach the refusal: a page in neither inventory gets `MockITestPage`, whose
`View()`/`Edit()` are the base mock's, and `CreateTestPageClient` prints
`[warn] … navigation mock` when that happens.

## <a id="route-3"></a>Route 3 rests on the compile, not on a shape check

Route 3 applies no shape gate, so what keeps the refusal out of reach is one step earlier: BC
selects the handler by its `TestPage "X"` parameter type, so the page must resolve in **this
bundle's compile** — and every source the compile can see is also a registration source.

The CLR-type inventory route 3 does gate on is **not** contained in the symbol inventory by
construction, which is why the argument has to be made about the compile rather than about the
types on hand. The two have different lifetimes:

- the symbol side is per-bundle — `RecordPatches.ResetForReload` clears `_sourceDirs` and
  `_parsedPages`, and `ClearPerBundleBcAppPaths` drops `_bcAppPaths`;
- loaded assemblies are process-wide, and `BcRuntime.IsStaleBundleAssembly` excludes only
  superseded generations of a registered name.

So in a multi-bundle run a form type from an earlier bundle can outlive the symbols that
described it. The containment claim is therefore about what THIS compile could name, not about
what is loaded.

### <a id="compile-sources"></a>The compile's symbol sources, and their registration

| what the compiler can see | where registration happens |
|---|---|
| resolved dependency `.app`s (`_resolvedDeps`, `BcCompiler.cs`) | the same `ordered` list feeds `DependencyLoader.LoadAll` and `RecordPatches.AddBcAppPath` (`Program.cs`) |
| Microsoft platform apps | `ReadDependencies` synthesises Optional `Application`/`System` roots, so they land in `ordered` too |
| layered-workspace source dependencies | `RecordPatches.AddSourceDir` in `SiblingCompile.cs` |
| extra symbol dirs (`SetExtraSymbolDirs`) | resolved as declared dependencies |
| **the suite's own `.al` folders** | `ProgramSupport.SuiteRegistrationDirs`, which **is** `CollectSuitePaths` |

The last row was a second derivation of the folder set until #3735, and it was wrong: the loops
registered `src/` when it existed and the suite root when no `test/` existed, while the compile
read `src/` + every `app*/` + `test/`. A page or table under `test/` or `app2/` therefore
compiled and was never parsed, and a `test/`-only suite registered nothing at all. Measured on
`ea27ed9f`: `Page.RunModal` on a page declared under `test/` answered

```
NavALException: You tried to invoke the Page object with the ID 63342 … An object with that
ID does not exist in the current application compiled with emit version 28014.
```

so the refusal was reachable — the run died one step before it. #3611/#3714 are the record of
the same two sets drifting apart once before. They are now one function, pinned by
`AlRunner.Tests/SuiteRootAlFilesTests.cs`
(`RegisteredDirs_AreExactlyTheCompiledDirs` and the two `PageUnder…` end-to-end rows).

### <a id="residual"></a>The one unmeasured source

`BcCompiler._usePackageCacheFallback` (`BcCompiler.cs`) specs every `.app` in the package-cache
directories with no matching registration, so a page declared only there would be nameable by
the compiler and absent from both inventories. Its only caller is `SiblingCompile.cs`'s
precompiled-app decompile compile, which never types a handler in a test bundle — so it is not
believed to be reachable, but nothing pins that. Tracked as issue #3769.

## What would widen the inventory

A runtime-package metadata reader (#3537). BC's captured emitter metadata
(`docs/object-metadata-capture.md`) cannot: it exists only for objects this run compiles, which
is a subset of `_parsedPages`, so a precompiled dependency page never passes through
`Compilation.Emit`.
