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

Tags without a hyphen are a separate question, tracked in #2221.
