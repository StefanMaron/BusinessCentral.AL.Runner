# 140 — Source-dir input + package-cache copy → CS0101

Regression test for issue #1566: when the user provides App A as a source
directory input AND App A's compiled .app is also in the --packages cache,
`AutoDiscoverDependencies` used to re-extract App A's AL and add it to the
compilation a second time, causing Roslyn CS0101 errors.

## Why excluded

This test requires the BC compiler (alc.exe) to build the .app files. It is
covered by the C# integration test
`AlRunner.Tests/AutoDiscoverDuplicateTests.SourceDirPlusPackageCopy_AppBDotApp_NoDuplicateCS0101`,
which skips automatically when alc is not available.

## Trigger scenario

```
al-runner --packages .pkg src/ test.app
```

Where:
- `src/` contains `codeunit 50105 "MZ2 MyCode Management"` (with `app.json`)
- `test.app` is a compiled test app that depends on App A
- `.pkg/` contains App A's compiled `.app`

Before the fix, Roslyn reported:
```
CS0101: The namespace 'Microsoft.Dynamics.Nav.BusinessApplication' already
contains a definition for 'Codeunit50105'
```

## Fix

1. `AutoDiscoverDependencies` now reads `app.json` from source-directory
   inputs and marks their AppIds as already-present, preventing the
   package-cache copy from being re-extracted.

2. The multi-group `Transpile` path deduplicates `generatedCSharpList` by
   primary class name after aggregating all groups, as a belt-and-suspenders
   guard against duplicate runner stubs compiled by `LoadAssertStubs`.
