# No silent out-of-scope failures (runtime-side companion to precompiled-DLL respect)

The runner reuses unmodified MS / ISV BC DLLs so AL test code exercises **real business
logic**, not a mock approximation. That collapses if a method on the test's path silently
returns a default value — a green test then lies about what was actually executed.

So when AL test code touches a runner surface we **cannot faithfully support**, the runner
MUST throw loudly, naming the API and the reason. Never silently return a default. Never no-op
a method whose return value the test code might rely on.

## What "loud" means

Throw `AlRunner.Infrastructure.RunnerOutOfScopeException` with:
- the BC API name that was touched (e.g. `NavEmail.Send`),
- a short reason citing `docs/scope.md` (e.g. `email-smtp — see docs/scope.md#email`),
- optionally the test name / stack origin if it's cheap to capture.

The test runner surfaces it as the failure message, so the developer sees exactly which
surface is unsupported and where to look.

## In scope — must run as real code

- Posting, validating, journal entries — anything through the real Base App / System App
  business logic.
- AL records, FlowFields, table extensions, key handling — against the in-memory table provider.
- Skeleton session / company / tenant / permission state — populated faithfully, not mocked.
- .NET interop used in-process by the apps (`MemoryStream`, encoders, regex, in-process crypto,
  etc.) — runs natively.
- Reports/forms to the extent of firing the test's `[RequestPageHandler]` / `[ReportHandler]` /
  `[MessageHandler]` etc. (rendering is out of scope; callback dispatch is in scope).

## Permanently out of scope — must throw

- SMTP / email sending.
- HTTP calls to external services, OAuth flows, web-API consumers.
- File I/O against blob storage, external filesystems, network shares.
- OData / SOAP / web service *publishing* endpoints.
- Printing to physical printers.
- Background job scheduling, NAS, job queue execution against a real scheduler.
- Anything else requiring a process or service outside the runner's in-process world.

`docs/scope.md` has the precise per-API list.

## In scope but not yet implemented

Placeholder hooks must throw `RunnerOutOfScopeException` with reason `"not-yet-implemented"`,
NOT silently return a default, so the developer notices and can either:
1. Implement it (a real in-memory backend or a faithful replacement), or
2. Open a runner-gap issue and add a `known-gaps-<area>.json` entry in `tests/expectations/`
   linking it (`docs/expectations.md`). `tests/excluded/` was the pre-cutover mechanism; it now
   lives frozen under `tests/archive/excluded/` and is not wired into CI.

## Audit obligation

Any new patch under `AlRunner/Patches/` (or anywhere else substituting BC method behaviour)
must justify in a code comment why its return value is **observably equivalent** to the real BC
behaviour for in-scope test code. If it isn't, it throws instead. Existing patches predate this
rule; a `SCOPE-AUDIT.md` exercise classifies each as faithful / silent-fake / TODO, and
silent-fakes are converted to throws as they're identified.

### The justification is a claim plus a citation, not the derivation behind it

The obligation above is unchanged and not negotiable: **no patch ships without a stated
reason why its answer is observably equivalent**, and a patch that cannot state one throws
instead. What this section bounds is the *form* of that statement, because the requirement
was being discharged by writing the whole investigation into the file (#3260; the
measurement is in docs/incidents/loud-failures.md).

**What the audit justification must contain, at the line:**

1. **The claim** — what an in-scope caller observes, and why that equals what real BC answers.
   One to three sentences.
2. **The citation** — what settled it. A corpus codeunit number and its verdict
   (`corpus 60940, green on 27.5 and 28.3`), an issue number, a `docs/` anchor, or the BC member
   whose body decides it. A citation is a pointer a reader can follow, not a summary of what
   they would find.
3. **The trap, if there is one** — the thing a later editor would get wrong. "Re-check the call
   count if a BC version changes shape" is worth its line; the scan that produced the count is not.

**What belongs elsewhere**, with the pointer left behind:

| | goes to |
|---|---|
| how the corpus adjudicated it, leg by leg | `docs/`, cited by anchor |
| the decompiled BC internals you walked to reach the conclusion | `docs/`, or the citation alone |
| what you tried first and why it failed | the PR body |
| a measurement table | `docs/`, where it is versioned and can be re-run |

A justification reduced this way is **still a justification**. A reviewer may ask for one to
be shortened, never for one to be removed, and may never accept a patch that has none. Where
the shortened claim and the `docs/` section disagree, the `docs/` section is the one under
test, because it is the copy a drift test can check.

**Why the citation must be right even when the claim is: a reviewer can only check the
account, never the result.** A correct finding reached by a method you have misdescribed is not
merely at risk of being disbelieved — it is *indistinguishable* from a wrong one, because the
account is all a checker has to work with. Measured twice on 2026-09-11 (#3399): an agent
produced the correct answer in round one and could not defend it for three rounds, having
described the wrong mechanism; and the coordinator's own correction to it was wrong for the
same reason. Both were settled by re-running the scan, never by argument. So state the method
you actually used, and when a disagreement persists, re-measure rather than restate — a
disagreement kept attached to something measurable converges, and one that is not becomes two
positions.

**Leave the pointer, and pin a load-bearing claim with a drift test.** Prose moved out of the
code can stop matching it with nothing failing; the pointer is what lets a reader who finds
the claim find the document, and this repository already has about ten such drift tests
(`tools/test_matrix_docs_drift.py`, `CliDocumentationTests` and siblings).

## Anti-patterns (don't ship these)

- `public static string ALDatabase_ALSid(string userName) => "S-1-0-0";` — silent fake. Either
  it's faithful (an in-scope SID computed from session state, with a comment explaining why) or
  it throws.
- Void no-op replacements without justification — same rule.
- Catch-and-swallow blocks in patches that hide BC NREs — those signal missing state, not
  behaviour to discard.

## Sister rules

- `.claude/rules/precompiled-dll-respect.md` — what we may NOT rewrite. The DLL contract.
- `.claude/rules/no-assumption-fixes.md` — never fix without understanding the AL pattern.
- `.claude/rules/file-issues-for-gaps.md` — gaps go to issues + `tests/expectations/`, never silent workarounds.
- `.claude/rules/tdd.md` — every fix needs a RED → GREEN.
- `.claude/rules/guards-need-a-third-state.md` — the build-time companion: a guard that
  could not measure must say so, never return its success code.

History: docs/incidents/loud-failures.md
