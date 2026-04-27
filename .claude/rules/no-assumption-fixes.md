# No assumption-based fixes

Before fixing a reported issue, you MUST understand the exact AL pattern that triggered it.

If the issue body or telemetry payload is too thin to identify the root cause — no runnable AL snippet, no failing-test reproducer, no specific input/output — **do not guess**.

**What to do instead:**
1. Add label `status: needs-input` to the issue.
2. Post a comment asking the reporter for: a minimal AL reproducer, the exact failing assertion or compiler diagnostic, and the surrounding context (codeunit/table definition) needed to reproduce locally.
3. Stop work on the issue until that input arrives.

**Why:** assumption-based "fixes" produce ghost tests that pass against a wrong mental model. The patch ships green, the real bug stays, and the test suite gains noise. This has happened before — apply the rule strictly.

**Triggers:** any time an impl agent picks up an issue from telemetry, `coverage-gap`, or auto-generated reports and the body lacks a concrete reproducer.
