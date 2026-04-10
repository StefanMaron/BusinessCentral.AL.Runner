# Test 06: Intentional Failure

This test contains **deliberately broken tests**. It exists to demonstrate
AL Runner's error output format, not as a regression test.

The two failing tests show:

1. **Wrong expected value** — `Assert.AreEqual` output showing expected vs actual
2. **Wrong error message** — `Assert.ExpectedError` output showing the mismatch

This test is excluded from the CI test matrix (the `grep -v '06-'` filter in
`test-matrix.yml` skips it). It is expected to fail by design.
