_Claude Code (main session, coord-1): repair after reviewer-2's FIX-FIRST._

- **Setters:** the guide now says which setter moves which answer. `Library - Permissions.SetTestTenantEnvironmentType(true)` makes `IsSandbox()` and `IsSaaS()` true and `IsProduction()` false. The Environment Info Test Library setters, now marked as OnPrem scope, move only `IsSandbox()` or `IsSaaS()`, and `IsProduction()` stays true with either. Confirmed in `NavTenantSettingsHelper`, NavUserAccount.dll 28.5.54151.55132 (sha256 prefix `46ecb131`): `IsProduction()` is `!IsSandbox()`, and `IsSandbox()` reads only the test-tenant override and the tenant setting.
- **License line:** narrowed to what #4700 covers: `License: none; the named-user limit on User writes is never enforced (#4700).`
- **Tests:** two new tests pin every line of the section. `GuideRuntimeEnvironmentTextTests` now passes 6 of 6. With `CliDocumentationTests` and the other help-text tests: `Failed: 0, Passed: 34, Total: 34`.
- **Mutations**, both applied with `tools/apply-mutation.py`, confirmed with `tools/mutation-verdict.py`, then restored (6 of 6 pass afterwards):
  - The reviewer's topology-to-SaaS change gives `Failed: 1, Passed: 5`. Only `Guide_StatesTopologyAndLicenseDefaults` fails.
  - Putting back the claim that the testability setters change `IsProduction()` gives `Failed: 1, Passed: 5`. Only `Guide_SaysWhichSetterMovesIsProduction` fails.
- **URL:** I kept the `blob/main` URL, as the review recommended.
