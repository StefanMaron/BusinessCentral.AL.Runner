# 02: Page Variable Shadows Built-in

This fixture is intentionally excluded from the main test loop.

It demonstrates a **compilation failure** that occurs when implicit `with` is
active (the default without `NoImplicitWith` feature flag). The page defines
variables named `FieldCaption` and `TableName` that shadow the built-in
record members `FieldCaption()` and `TableName`. With implicit `with Rec`,
the BC compiler resolves these names to the record members instead of the
page variables, producing false positive errors:

- **AL0135**: `FieldCaption(Joker)` — compiler sees the 1-arg built-in method
- **AL0166**: `TableName` — compiler sees a read-only property, not a variable
- **AL0129**: assignment to read-only expression

## Why it is excluded

Without `NoImplicitWith` in app.json, this code is genuinely invalid AL — the
compiler is correct to reject it. The companion test in
`tests/bucket-feature-niw/feature-niw/01-no-implicit-with/` proves that the same patterns compile
and run correctly when the `NoImplicitWith` feature flag is present.

## Related

- `tests/bucket-feature-niw/feature-niw/01-no-implicit-with/` — passing tests with feature flag
- `AlRunner/Program.cs` — `ExtractFeatures()` and `MapCompilerFeatures()`
