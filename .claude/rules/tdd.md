# TDD is non-negotiable

Every feature, fix, or mock change requires a test. No exceptions.

**Strict red → green:**
1. RED — write the failing AL test first, run it, confirm it fails.
2. GREEN — implement the fix, run again, confirm it passes.

**Cover both directions in every test:**
- Positive: correct input → expected value (`Assert.AreEqual`).
- Negative: invalid input → specific error (`asserterror` + `Assert.ExpectedError('...')`).

**Tests must prove, not just pass.** Ask: would this test still pass if the implementation always returned a default value (0, `''`, `false`)? If yes, it is noise — strengthen it. Assert specific concrete values, never `Assert.IsTrue(true, ...)` or bare `asserterror` without an expected message.

The only valid exception is a "no-op stub" test where the *entire* claim is "this does not crash" — name it `*_NoThrow` / `*_IsNoOp` so the limited claim is explicit.
