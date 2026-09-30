_Written by Claude Code (main session, coord-1) at the owner's request._

Closes #4932

## What changed

`--guide` gets a short `RUNTIME ENVIRONMENT` section listing the defaults AL code sees under the runner:

- The environment is Production, not a sandbox and not SaaS: `IsProduction() = true`, `IsSandbox() = false`, `IsSaaS() = false`. The section names the calls a test uses to switch it, and notes that the al-language corpus tier runs as a sandbox.
- Service topology is always on-premises.
- There is no license, so named-user license checks never refuse a write (#4700).

Each point points to `docs/limitations.md#environment-type`, which already has the full explanation (#3514).

`FURTHER READING` now also says where `docs/` paths can be read online. The installed tool has no `docs/` folder next to it, so the relative paths led nowhere.

## Tests

New `AlRunner.Tests/GuideRuntimeEnvironmentTextTests.cs` makes text assertions on `PrintGuide`, following the pattern of `TestDataCompanyHelpTextTests`.

- RED before the change: `Failed: 4, Passed: 0, Total: 4`
- GREEN after: `Failed: 0, Passed: 4`. With `CliDocumentationTests` and the other help-text tests: `Failed: 0, Passed: 32, Total: 32`
- Mutation: changing the guide text to `IsProduction() = false` (applied with `tools/apply-mutation.py`, confirmed with `tools/mutation-verdict.py`) gave `Failed: 1, Passed: 3`. Only `Guide_StatesTheDefaultEnvironmentType` failed. The file was then restored and the tests passed again, 4 of 4.
- `tools/test_doc_pointers.py` and `tools/test_matrix_docs_drift.py` pass.

## Queue scan

I searched open issues for "guide environment", "IsProduction", "IsSandbox", "guide docs link" and "CliText guide", and open PRs for "CliText". The only result was #2450, a Confirm handler problem on the User Card, which is unrelated. There was nothing to fold in.
