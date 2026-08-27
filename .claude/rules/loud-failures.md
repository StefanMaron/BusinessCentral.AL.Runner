# No silent out-of-scope failures (runtime-side companion to precompiled-DLL respect)

The runner reuses unmodified MS / ISV BC DLLs so AL test code exercises **real
business logic**, not a mock approximation. The whole point collapses if a
method on the test's path silently returns a default value — a green test then
lies about what was actually executed.

Therefore: when AL test code touches a runner surface that we **cannot
faithfully support**, the runner MUST throw loudly, naming the API and the
reason. Never silently return a default. Never no-op a method whose return value
the test code might rely on.

## What "loud" means

Throw `AlRunner.Infrastructure.RunnerOutOfScopeException` with:
- the BC API name that was touched (e.g. `NavEmail.Send`),
- a short reason citing `docs/scope.md` (e.g. `email-smtp — see docs/scope.md#email`),
- optionally the test name / stack origin if it's cheap to capture.

The test runner surfaces the exception with the BC API name + reason as the
failure message, so the developer reading test output sees exactly which
surface is unsupported and where to look.

## What's in scope, must run as real code

- Posting, validating, journal entries — anything that runs through the real
  Base App / System App business logic.
- AL records, FlowFields, table extensions, key handling — against the
  in-memory table provider.
- Skeleton session / company / tenant / permission state — populated
  faithfully, not mocked.
- .NET interop used in-process by the apps (`MemoryStream`, encoders, regex,
  in-process crypto, etc.) — runs natively.
- Reports/forms to the extent of firing the test's `[RequestPageHandler]` /
  `[ReportHandler]` / `[MessageHandler]` etc. (rendering is out of scope;
  callback dispatch is in scope).

## What's permanently out of scope, must throw

- SMTP / email sending.
- HTTP calls to external services, OAuth flows, web-API consumers.
- File I/O against blob storage, external filesystems, network shares.
- OData / SOAP / web service *publishing* endpoints.
- Printing to physical printers.
- Background job scheduling, NAS, job queue execution against a real scheduler.
- Anything else that requires a process or service outside the runner's
  in-process world.

See `docs/scope.md` for the precise per-API list.

## What's in scope but not yet implemented

Placeholder hooks for not-yet-implemented in-scope surfaces must throw
`RunnerOutOfScopeException` with reason `"not-yet-implemented"`, NOT silently
return a default. This is so the developer notices and can either:
1. Implement it (with a real in-memory backend or a faithful replacement), or
2. Open a runner-gap issue and add a `known-gaps-<area>.json` entry in
   `tests/expectations/` linking it (see `docs/expectations.md`). `tests/excluded/`
   was the pre-cutover mechanism; it now lives frozen under `tests/archive/excluded/`
   and is not wired into CI.

## Audit obligation

Any new patch under `AlRunner/Patches/` (or anywhere else that
substitutes BC method behaviour) must justify in a code comment why its return
value is **observably equivalent** to the real BC behaviour for in-scope test
code. If it isn't, it throws `RunnerOutOfScopeException` instead.

Existing patches predate this rule. A `SCOPE-AUDIT.md` exercise classifies
each one as faithful / silent-fake / TODO; silent-fakes are converted to
throws as they're identified.

## Anti-patterns (don't ship these)

- `public static string ALDatabase_ALSid(string userName) => "S-1-0-0";` — silent fake.
  Either it's faithful (an in-scope SID computed from session state, comment
  explaining why) or it throws.
- Void no-op replacements without justification — same rule.
- Catch-and-swallow blocks in patches that hide BC NREs — those are signals
  that state is missing, not behaviour to discard.

## Sister rules

- `.claude/rules/precompiled-dll-respect.md` — what we may NOT rewrite. The DLL contract.
- `.claude/rules/no-assumption-fixes.md` — never fix without understanding the AL pattern.
- `.claude/rules/file-issues-for-gaps.md` — gaps go to issues + `tests/expectations/`, never silent workarounds.
- `.claude/rules/tdd.md` — every fix needs a RED → GREEN.
