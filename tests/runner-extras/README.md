# tests/runner-extras/

Runner-specific positive AL tests. The `tests/al-language/` submodule is
deliberately kept AL-Runner-unaware — it specifies BC behaviour and gets
validated against a real service tier. Anything that asserts a runner-specific
contract goes here instead.

Typical contents:

- Proofs that out-of-scope surfaces throw `RunnerOutOfScopeException` with the
  documented reason string (the mirror image of `expect-oos` entries in
  `tests/expectations/`).
- Proofs of runner-only behaviour that does not exist in real BC (e.g. test
  isolation modes, bundle dependency resolution, precompile subcommand
  outputs).
- Regression tests for fixes whose failing input is too runner-specific to
  belong in the corpus.

## Layout

Each suite is a self-contained AL package with its own `app.json`. Folders are
named by area, mirroring `tests/expectations/`:

```
tests/runner-extras/
  oos-reports/          ← proofs for the report-rendering OOS contract
  oos-http/             ← proofs for the http-egress OOS contract
  ...
```

A suite runs with the same command as the corpus:

```
dotnet run --project AlRunner -c Release -- tests/runner-extras/<suite>
```

CI runs every suite under `tests/runner-extras/`. See `.github/workflows/bc-tests.yml`.

## Authoring rules

- Every codeunit declares `Subtype = Test`.
- Every OOS-asserting test uses the literal token from
  `RunnerOutOfScopeException.BuildMessage` (`out-of-scope: <api> — <reason>`)
  in `Assert.ExpectedError` so the test does not silently pass if the runner
  starts throwing a different exception type.
- Do not import logic from the al-language submodule — these tests must stand
  alone so the corpus pin can move independently.
