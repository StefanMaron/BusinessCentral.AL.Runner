# Sample 06: Intentional Failure

This sample contains **deliberately broken tests**. It exists to demonstrate
AL Runner's error output format, not as a regression test.

The two failing tests show:

1. **Wrong expected value** — `Assert.AreEqual` output showing expected vs actual
2. **Wrong error message** — `Assert.ExpectedError` output showing the mismatch

The `samples-fail.yml` GitHub Actions workflow runs this sample and is expected
to fail. Click through to the workflow run to see exactly what test failure
output looks like.
