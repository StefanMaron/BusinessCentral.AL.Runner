# tests/expectations/

Runner-owned manifest declaring expected outcomes for tests in
`tests/al-language/` (BusinessCentral.AL.Language.Tests, checked out per run).

See [`docs/expectations.md`](../../docs/expectations.md) for the schema, mode
semantics, and result-classification table.

Each JSON file is an array of expectation objects following the schema. File
naming convention:

- `oos-<area>.json` — out-of-scope-by-design (most common). `Mode: expect-oos`,
  matched on the reason anchor of either a typed `RunnerOutOfScopeException` or
  the `out-of-scope: <api> — <reason>` message convention Cecil-injected throw
  sites carry.
- `known-gaps-<area>.json` — in-scope but not yet implemented (transient, links
  to an **open** GitHub issue). `Mode: expect-fail-known-gap`.
- `divergence-<area>.json` — the runner intentionally and permanently answers
  differently from real BC. `Mode: expect-divergence`; carries `Reason` + `Doc`
  and no `Issue`, because there is no open work to link.
- `disabled-<area>.json` — won't compile or won't run; pure skip.
- `accept-<area>.json` — a RUN-level condition this project knowingly accepts, not
  a test expectation. Today one mode: `accept-partial-company-init`, which names an
  initialization codeunit whose abort is accepted here and carries a mandatory
  free-text `Reason` and no `Issue`. It suppresses only the company-init exit 0 → 2
  escalation; the abort is still reported on every surface. See
  [`docs/partial-company-initialization.md`](../../docs/partial-company-initialization.md).

Sharding by area keeps PR diffs small. A single PR adding or removing one
expectation should touch one file with one entry.

An entry may also carry an optional `"Suites": [...]` naming the suite roots that can
cover it (#3347). Absent — the case for every entry naming a `tests/al-language`
test — it is audited by every run, unchanged. Present, it is audited only by a run that
covered a matching root, which is what lets an entry name a `tests/runner-extras/`
codeunit without failing the full-corpus leg that could never load it. It is not an
exemption: the run that owns the suite still audits the entry in full. See
[`docs/expectations.md`](../../docs/expectations.md#suites--which-runs-are-answerable-for-an-entry-3347).

The file prefix and the entry's `Mode` must agree — the prefix is what a human
scanning the directory reads. Moving an entry between modes means moving it
between files. `AlRunner.Tests/ExpectationFilePrefixTests.cs` checks every entry
against its file's prefix, and fails on a file name carrying none of the five
prefixes; a recognised file left holding `[]` is fine (#3114). Separately, a
`known-gaps-*.json` none of whose entries is `expect-fail-known-gap` fails the
guard below, because that disagreement would silence the whole file for it.

## A PR that closes a gap issue must delete or re-target its entry

`pr-gate.yml`'s `expectation-gap-issue-consistency` job goes red on a PR that
declares `Closes #N` while an `expect-fail-known-gap` entry here still links
issue N. The PR says the gap is fixed and the manifest says it is not; both are
in the same diff, so it is settled there rather than by a red `main` the next
morning. It is in the workflow whose jobs are meant to gate, but `main`'s
ruleset does not list it, so today it annotates rather than refuses the merge —
see `docs/expectations.md`.

It covers one of the two orderings: the entry already being in the checkout when
the closing PR is checked. The mirror case — the entry arriving *after* that
check has run — is invisible to it, and is what the same job's non-blocking
sweep reports, without failing, for entries linking an issue that is already
closed; a closed issue is a lead, not proof the entry is stale. The 2026-09-05
incidents behind this (#2844, #2858) were that inverse ordering, so the gate
would not have caught them; it closes the other direction. Details, the
measurements and the anti-vacuity rules:
[`docs/expectations.md`](../../docs/expectations.md#the-ci-guard-on-issue-links).

## `count-baseline/` is a different concern, deliberately not a top-level `.json`

`--expectations` (this directory, auto-probed by default) loads every
`*.json` file directly under `tests/expectations/` as an array of
per-test classification entries. `--count-baseline` (see
`AlRunner/Infrastructure/CountBaseline.cs`, #1880) is a *different* schema
entirely — an expected EXACT aggregate test/app-group COUNT per suite, not
a classification of one named test. It lives in the
`tests/expectations/count-baseline/` **subdirectory** specifically so the
`--expectations` directory scan (non-recursive) never tries to parse it as
a classification array. Do not add `*.json` files directly under
`tests/expectations/` unless they follow the classification-entry schema
above.

Its schema, how to bump it, and where per-bump rationale goes are in
[`count-baseline/README.md`](count-baseline/README.md); the log of past bumps is
[`count-baseline/history.md`](count-baseline/history.md).
