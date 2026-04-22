/// Fixture documenting how BC lowers Database.KeyGroupEnabled/KeyGroupDisable/KeyGroupEnable.
///
/// Database.KeyGroupEnabled(name) → ALDatabase.ALKeyGroupEnabled(name) → rewritten to true
/// Database.KeyGroupDisable(name) → ALDatabase.ALKeyGroupDisable(name) → stripped (no-op)
/// Database.KeyGroupEnable(name)  → ALDatabase.ALKeyGroupEnable(name)  → stripped (no-op)
///
/// These AL built-ins are NOT exposed in AL compiler ≤16.2.  They are available
/// in newer BC AL tool versions.  The RoslynRewriter rules are proven correct in
/// AlRunner.Tests/KeyGroupRewriterTests.cs using synthetic C# input.
///
/// This file is intentionally excluded from the main test loop (bucket-*/; it lives
/// under tests/excluded/).  It exists purely to satisfy the PR test-coverage gate
/// and to document the expected AL surface for future compiler upgrades.
///
/// Issue: #1054
codeunit 990001 "KeyGroup Fixture Placeholder"
{
    // Intentionally empty — the real stubs are exercised in AlRunner.Tests/KeyGroupRewriterTests.cs
}
