# Log's component-tag filter

`AlRunner/Log.cs` wraps stdout and stderr and drops a line that starts with a `[Tag]` unless
`--verbose` (or `AL_RUNNER_VERBOSE=1`) is set. A short exemption list (`[bc]`, `[dep]`,
`[warn]`, …) stays visible. The reasons for each exemption are in the comment above the pattern.

## Hyphenated tags

The tag character class is `[A-Za-z0-9._+]`, with no hyphen, so a tag such as
`[dep-load-fail]` never matches and is never filtered. Its call site decides whether it prints.
Issue #2257 decided to keep it that way after classifying every call site, because a widened
pattern breaks two of the three kinds below:

| kind | what it is | why the filter must not touch it |
|---|---|---|
| **Loud** | a failure, a refusal, or a result the user asked for | hidden by default, it turns a real problem into silence |
| **OptIn** | a trace behind its own switch (`AL_RUNNER_TRACE_*`, `AL_RUNNER_DIAG_*`, `BCCOMPILER_TIMING`, `AL_RUNNER_HOOK_AUDIT`, a feature's `Enabled` flag) | the switch does not set `Log.Verbose`, so a filtered trace prints nothing when asked for |
| **VerboseGated** | chatter on a healthy run | hidden by an explicit `if (Log.Verbose)` at its call site, the lever #2239 used |

`AlRunner.Tests/LogHyphenatedTagContractTests.cs` holds the classification. It scans
`AlRunner/**/*.cs` for string literals that begin with a hyphenated `[tag]`, and fails when a
site is unclassified, when a declaration matches nothing, when a Loud or OptIn line would not
survive the default filter, when an OptIn line has no `if (` testing its switch within 40 lines,
or when a VerboseGated line has no `Log.Verbose` gate right above it. The gate checks read
source text, so they are proximity checks rather than proof of control flow.

### Classification as of #2257

Population: 42 distinct tags in string literals under `AlRunner/` (comment lines excluded). This
is larger than the 26 in the issue and the 30 in an earlier count because it includes
upper-case tags (`[DIAG-RETRY]`, `[FCE-NRE]`, `[BcCompiler-diag]`), return values printed
elsewhere (`[provision-gap]`, `[test-exec]` warnings), and exception messages (`[al-locals]`).

- **Loud:** `al-locals`, `bc-floor`, `count-baseline`, `count-out`, `dep-load-fail`,
  `dep-metadata` (bad switch value, cache write failure), `dep-metadata-fail`,
  `install-trigger`, `oos-in-try`, `page-control-field`, `phase-log`, `provision-gap`,
  `report-metadata` (document parse fallback), `servicetier-dll` (load failure, index skip),
  `source-dep`, `source-map`, `test-data`, `test-exec`, `type-index` (subscribers not
  registered).
- **OptIn:** `BcCompiler-diag`, `DIAG-RETRY`, `FCE-NRE`, `aggregate-permission-set`,
  `all-profile`, `dap-step-trace`, `dep-metadata` (trace), `emit-timing`, `first-chance`,
  `hook-audit`, `instrumentation-counters`, `mem-census`, `object-metadata`,
  `option-captions`, `page-metadata`, `page-trigger-audit`, `parse-counts`, `perm-metadata`,
  `permission-table`, `query-metadata`, `report-metadata` (trace), `shared-refs`,
  `table-metadata`, `table-trigger-audit`, `xmlport-metadata`, `sibling-symbols` (switch:
  `--watch` or `--verbose`, #2672).
- **VerboseGated:** `type-index` (reflection fallback), `servicetier-dll` (the "indexed N
  objects" summary).

`[source-dep]` is Loud because it is the source-sibling twin of the exempt `[layered]` progress
lines, and `CacheRootsIsolationTests` asserts `[source-dep] WROTE` in a run without `--verbose`.

## Single-word tags

These are the tags the filter actually acts on. Issue #2221 recorded five user-facing fixes
that each shipped invisible because their tag was not on the exemption list — `[bc]`,
`[expectations]` (#1984), `[reexec]` (#2034), `[dap]` (#1642) and `[warn]` (#2206) — and none
of the five was caught by a test. Two were caught by accident (a harness timing out, a debug
print vanishing); the rest by someone noticing.

**Severity is a class, not a component.** `warn|error|fatal` is its own alternation in
`Log.cs`, separate from the component exemptions. A line whose author thought it worth calling
a warning or an error is worth the user seeing, whatever raised it. Before #2221 `warn` sat in
the component list and `error` was absent altogether, so the first `[error] …` anyone wrote
would have been eaten — the sixth instance, pre-empted. `fatal` has no call site yet.

**The census is a ratchet.** `AlRunner.Tests/LogSingleWordTagContractTests.cs` scans
`AlRunner/**/*.cs` for single-word `[tag]` literals and fails when one has no entry in
`Declared`. Three kinds:

| kind | what it is | what the test checks |
|---|---|---|
| **UserFacing** | a result, a failure, or readiness the user asked for | the real literal, read from the call site, survives the real filter with no `--verbose` |
| **Internal** | per-object or per-method chatter | suppressed by default **and** still recovered by `--verbose`, so it is hidden rather than unreachable |
| **NotALogLine** | matches the tag shape but is never written as a console line | carries a written reason — this is the one kind the filter cannot adjudicate |

The classification is **per tag, not per site**, which is what the observed failure mode needs:
all five instances were a *new tag*. A tag already declared `Internal` that gains a site
deserving visibility is not caught here — that is the open backlog in #4234.

### Census as of #2221

Measured at `9cb68de`: **77** distinct tags, **1,016** literal sites.

These site counts are a **snapshot**, not a pinned contract: they move whenever anyone adds or
removes a tagged line. `LogSingleWordTagContractTests` does not re-derive them. What it does pin,
on every run, is the property that actually matters -- every tag reaching the filter is
classified, no declaration is stale, no UserFacing line is eaten, no Internal line leaks, and
severity is never suppressed. The three class totals partition the scan exactly, so they must sum
to the total above; they did not before #4233's review (180 + 25 + 809 = 1,014), which is how the
NotALogLine figure was found to be 27 rather than 25 -- it had been left at the six-colour
subtotal when `Oo` and `Content_Types` were added.

- **UserFacing (9 tags, 180 sites):** `bc`, `dap`, `dep`, `expectations`, `layered`,
  `provision`, `reexec`, `warn`, `watch`.
- **NotALogLine (8 tags, 27 sites):** `red`, `grey`, `green`, `blue`, `yellow`, `bold` —
  Spectre.Console markup in `WatchDashboard.cs`, which contains no `Console.WriteLine`; `Oo`,
  the `[Oo]bject` regex character class in a `BcCompiler` diagnostic pattern; `Content_Types`,
  the `[Content_Types].xml` entry name inside an `.app` package.
- **Internal (60 tags, 809 sites).** Concentrated: `Cecil` (369), `BcRuntime` (78),
  `RecordPatches` (76), `NavReportSync` (32), `Subscribers` (31) and `EventPipeJIT` (25) are
  611 of the 809. `[Cecil]` alone is 369 sites, not the ~280 quoted in #2221 and in the
  comment above `Log.cs`'s pattern.

### Why the default was not inverted

#2221 proposed inverting the filter — suppress only what is marked internal — on the premise
that the `[Cecil]` diagnostics are the volume and are "a small, known set to mark". The volume
is indeed concentrated (six tags, 75%), but the remaining 54 tags are not chatter: 89 of their
198 sites match failure-shaped wording, and the six bulk tags carry a further 254. So
inversion cannot be done by marking six tags, and it changes what every default run prints.
That is a product decision about the runner's default output rather than an engineering one,
recorded on #2221 and tracked with the per-site backlog in #4234. The ratchet above closes the
silent direction either way, and does not preempt the choice.
