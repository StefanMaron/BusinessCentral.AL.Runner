# 04: TableNo Local Procedure Scope

This fixture is intentionally excluded from the main test loop.

It demonstrates a **compilation failure** that occurs when implicit `with` is
active (the default without `NoImplicitWith` feature flag). A codeunit with
`TableNo` property defines a local procedure `GetStatus(var Rec)` that shadows
the record's `GetStatus()` method. With implicit `with Rec`, the BC compiler
resolves the call to the record's 0-argument method, producing:

- **AL0126**: No overload for method 'GetStatus' takes 1 arguments

## Why it is excluded

Without `NoImplicitWith` in app.json, this code is genuinely invalid AL — the
compiler resolves `GetStatus(Rec)` to the record's parameterless method via
implicit `with`, then rejects the extra argument. The companion test in
`tests/bucket-feature-niw/01-no-implicit-with/` proves that the same pattern compiles
and runs correctly when the `NoImplicitWith` feature flag is present.

## Related

- `tests/bucket-feature-niw/01-no-implicit-with/` — passing tests with feature flag
- `AlRunner/Program.cs` — `ExtractFeatures()` and `MapCompilerFeatures()`
