## Summary

This PR fixes several standalone pipeline issues that prevented `al-runner` from handling some report-heavy and test-heavy AL projects.

Before this change, affected projects could fail in one of these early stages:

- BC `Compilation.Emit()` failed before usable C# was produced
- generated C# failed to compile because required standalone mocks were missing
- test suites stopped during setup instead of reaching actual execution

After this change, those projects can now get through transpilation and Roslyn compilation, and the runner reaches test execution far more reliably.

## Problem

The failures came from a few separate gaps in the standalone pipeline:

### Report / request-page transpilation

- report and report-extension inputs could trigger BC layout-generation failures during `Compilation.Emit()`
- generated report, request-page, and query classes still depended on BC-only runtime infrastructure that does not exist in standalone mode

### Test surface gaps

- `TestPage` support was incomplete
- generated test code expected APIs such as `GoToRecord`, `Next`, `New`, `GetPart`, assignable field values, decimal conversion helpers, and filter reads
- `Subtype = TestRunner` codeunits still emitted BC-specific override members after the BC base type had been removed

### Runtime helper gaps

- some generated helper calls had no standalone equivalent yet, including:
  - `GetBySystemId`
  - object-to-array conversion helpers
  - record view helpers
  - field clearing helpers
  - stream/file helpers

### Stub/package collisions

- built-in test stubs could collide with real BC test packages when both were present

## Changes

### Transpilation / rewrite stability

- strip `rendering { ... }` blocks from `report` / `reportextension` AL before transpilation
- improve verbose exception logging around `Compilation.Emit()`
- stub generated report, request-page, and query classes so BC-only layout/runtime infrastructure does not block standalone compilation

### Stub handling

- skip built-in test stubs when the input does not use test-library surface
- transpile runtime assert/variable-storage stubs in isolation when needed, to avoid collisions with real BC test packages

### Runtime compatibility

- extend `MockRecordHandle` with missing support for:
  - `ALGetBySystemId`
  - `ALGetView` / `ALSetView`
  - `ClearFieldValue`
  - common system field access
  - global array variables
- extend `MockRecordRef` with matching `ALGetBySystemId` support
- add `AlCompat.ObjectToMockArray<T>()`
- add `MockFile.ALUploadIntoStream(...)`
- add small missing helpers in existing mocks such as:
  - `MockTextBuilder.ALAppendLine()`
  - `MockInStream.Clear()`
  - `MockSystemOperatingSystem.ALGetUrl(...)`

### TestPage / request-page support

- extend `MockTestPageHandle` with:
  - `ALGoToRecord`
  - `ALNext`
  - `ALNew`
  - `ClearReference`
  - `GetPart`
- extend `MockTestPageField` with:
  - assignable `ALValue`
  - `ALAsDecimal()`
  - `ALEnabled()`
- add filter tracking via `MockTestPageFilter.ALGetFilter(...)`
- add request-page handler dispatch in `HandlerRegistry`

### Report handle support

- add `MockReportHandle` as a standalone replacement for `NavReportHandle`
- support:
  - `SetTableView`
  - helper-procedure dispatch via `Invoke(...)`
  - `Run()`
  - `RunRequestPage()`

### TestRunner cleanup

- remove / neutralize BC-specific `TestRunner` members that become invalid after base-class removal
- add a no-op `ClearApplicationMemberVariables()` stub for rewritten codeunit classes

## Validation

Validated against a real private project that includes:

- source + test apps
- external BC test packages
- reports and request pages
- `TestPage` usage
- `Subtype = TestRunner` codeunits

### Before

- failed during transpilation or generated C# compilation
- did not reach meaningful test execution

### After

- AL transpilation succeeds
- Roslyn rewrite/compilation succeeds
- tests execute
- remaining failures are surfaced as runtime limitations or actual runtime failures instead of setup-time pipeline crashes

## Scope

This PR improves standalone pipeline robustness. It does not fully implement all external BC test-library codeunits.

Projects that depend heavily on helper libraries such as dialog, purchase/sales, CRM, or other BC test toolkit codeunits may still be blocked at runtime until those libraries are mocked more completely.

## Follow-ups

- add broader mocks for BC test-library codeunits used by integration-style AL test suites
- continue reducing runner limitations once projects successfully reach execution
