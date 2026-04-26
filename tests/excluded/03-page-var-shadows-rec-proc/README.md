# 03: Page Variable Shadows Record Procedure

This fixture is intentionally excluded from the main test loop.

It demonstrates a **compilation failure** that occurs when implicit `with` is
active (the default without `NoImplicitWith` feature flag). The page defines
a variable `CanDownloadResult` that shadows a record procedure of the same
name. With implicit `with Rec`, the BC compiler resolves the name to the
record procedure instead of the page variable, producing:

- **AL0129**: the left-hand side of an assignment must be a variable or field

## Why it is excluded

Without `NoImplicitWith` in app.json, this code is genuinely invalid AL — the
compiler treats the assignment target as a procedure call, not a variable.
The companion test in `tests/bucket-4/01-no-implicit-with/` proves that the
same pattern compiles and runs correctly when the `NoImplicitWith` feature
flag is present.

## Related

- `tests/bucket-4/01-no-implicit-with/` — passing tests with feature flag
- `AlRunner/Program.cs` — `ExtractFeatures()` and `MapCompilerFeatures()`
