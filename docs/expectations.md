# Test expectations manifest

The runner consumes tests from the `tests/al-language` submodule
(`StefanMaron/BusinessCentral.AL.Language.Tests`). That corpus is the canonical
spec of AL language behaviour against a real BC service tier. By design, some
tests in the corpus exercise surfaces the runner cannot — and will never —
support in-process (report rendering, SMTP, HTTP egress, etc.), and on a few
surfaces the runner deliberately answers differently from BC (the task
scheduler, `docs/scope.md` §3.6). The runner does not modify the corpus to make
those tests pass; instead it declares its expectations about them in this
directory.

## Activation

The manifest loads once at startup, before any BC initialisation. Without the
flag, the runner probes `./tests/expectations` relative to the working
directory and activates classification only when that directory exists — a run
outside this repo behaves exactly as if the mechanism did not exist. Pass
`--expectations <dir>` to point at another manifest directory explicitly (the
directory must exist). A malformed manifest aborts the invocation with exit
code 2 before a single test runs.

## Layout

```
tests/expectations/
  oos-<area>.json         ← out-of-scope-by-design (most common)
  known-gaps-<area>.json  ← in-scope but not yet implemented (transient, links to GH issues)
  divergence-<area>.json  ← runner intentionally answers differently from BC (permanent)
  disabled-<area>.json    ← won't compile or won't run; pure skip
```

One file per area. Sharding matches Microsoft's
`ALAppExtensions/Build/DisabledTests/` convention so anyone familiar with the
BC ecosystem recognises the shape — we extend the schema with extra fields
rather than replace it.

## Entry schema

```jsonc
[
  {
    // Required (Microsoft-compatible core)
    "codeunitId":   60042,
    "CodeunitName": "Report Layout Render",
    "Method":       "Report_SaveAs_RendersPdf",   // "*" matches every test in the codeunit

    // Required runner extension
    "Mode": "expect-oos",                          // expect-oos | expect-fail-known-gap
                                                   //   | expect-divergence | skip

    // Conditional
    "Reason": "report-rendering",                  // required when Mode = expect-oos
                                                   //   must match a section anchor in docs/scope.md
                                                   // required when Mode = expect-divergence
                                                   //   short label for what diverges
    "Issue":  "https://github.com/.../issues/123", // required when Mode = expect-fail-known-gap
                                                   // FORBIDDEN when Mode = expect-divergence
    "Doc":  "docs/scope.md#reports",               // required when Mode = expect-divergence
                                                   //   where the decision is written down
                                                   // optional otherwise

    // Optional
    "Note": "BC service tier renders PDF via report engine; runner is in-process only."
  }
]
```

Field names follow Microsoft's casing (`codeunitId` lowercase-c, `CodeunitName`
and `Method` PascalCase) so external BC tooling that reads MS's `DisabledTests/`
shape also reads ours.

### `Method: "*"`

Wildcard matches every `[Test]` procedure in the named codeunit. Use this when
an entire test codeunit is unsupported (e.g. all of `Test Report Saving` is
OOS because report rendering is OOS). Method-level granularity is preferred
when only some tests in a codeunit are affected.

### Mode semantics

| Mode | Test must… | Runner counts as | When to use |
|---|---|---|---|
| `expect-oos` | raise an out-of-scope signal with matching `Reason` | `pass-oos` | Surface is OOS by design (see `docs/scope.md`) — runner will never support it in-process |
| `expect-fail-known-gap` | fail (any exception or assertion mismatch) | `pass-known-gap` | Surface is in scope but not yet implemented; `Issue` tracks the work |
| `expect-divergence` | fail, without raising an out-of-scope signal | `pass-divergence` | Runner *intentionally* answers differently from real BC, permanently; `Doc` cites the decision |
| `skip` | n/a — runner does not invoke the test | `skipped` | Test cannot compile against the current AL output, or otherwise must not run |

### Which throw shapes `expect-oos` recognises

Two, and only two — both are "the runner said this surface is out of scope":

1. **Typed** — `RunnerOutOfScopeException(api, reason)`, thrown from managed C#
   patches. Matched anywhere in the inner-exception chain.
2. **The message convention** — any exception whose message carries
   `out-of-scope: <api> — <reason> — see docs/scope.md#<anchor>`. Cecil-injected
   IL (`NclCecilRewrite`) cannot construct our typed exception, so every OOS
   surface implemented by IL rewrite — HTTP egress, RDLC/Word/Excel rendering,
   `RunRequestPage` — signals this way. The convention is also what AL-side
   `asserterror` + `Assert.ExpectedError('out-of-scope:')` matches on, so it is
   a first-class contract, not a fallback. Parsed in exactly one place,
   `Infrastructure.OutOfScopeMessage`, shared with the reporter's failure
   bucketing.

An exception carrying *neither* is not an out-of-scope signal. A plain
`InvalidOperationException("Sequence contains no elements")` under an
`expect-oos` entry stays a failure — widening the matcher must not turn it into
one that says yes to everything.

New Cecil throw sites therefore have to put the **`docs/scope.md` anchor first
in the reason slot**, and the API name in the API slot. A message shaped
`out-of-scope: report-rendering-external — RDLC layout processing …` puts a
reason where the API belongs and is undeclarable; fix the throw site rather
than writing a prose `Reason` into the entry.

`Reason` matches on the anchor: throw sites may append free-text detail after
an ` — ` (em-dash) separator, while the entry holds only the leading
`docs/scope.md` anchor (e.g. a throw site's
`query-join-rightouterjoin-not-implemented — only InnerJoin and …` matches an
entry declaring `query-join-rightouterjoin-not-implemented`). Anchors are
compared for **equality** after that trim, not by prefix or substring:
`external-htt` does not match `external-http`.

### `expect-divergence` vs the other two failure modes

All three declare a failing test, and picking the wrong one is how the manifest
starts lying:

- **`expect-fail-known-gap`** means *transient*: the runner should behave like BC
  here, does not yet, and `Issue` links the open work. Nothing in the run can see
  whether that issue is still open — the manifest is evaluated in-process with no
  network — so this mode is only as honest as the person writing it.
- **`expect-divergence`** means *settled*: the runner behaves differently on
  purpose and nobody is going to change it. There is no issue to link (linking one
  is rejected at load time), so the entry carries `Doc` instead — a pointer to
  where the decision is written down, e.g. `docs/scope.md#jobs` for the task
  scheduler.
- **`expect-oos`** means the runner refuses the surface outright and says so with
  an out-of-scope signal. A test declared `expect-divergence` that *does* raise
  one fails with "declare it expect-oos instead", so divergence cannot quietly
  absorb new OOS surfaces.

### Result classification table

When the runner finishes a test, it consults the manifest:

"OOS signal" below means either shape from the section above: the typed
`RunnerOutOfScopeException` or the `out-of-scope: …` message convention.

| Test outcome | Manifest entry | Classification | Action |
|---|---|---|---|
| OOS signal, `Reason` anchor matches | `expect-oos` | **pass-oos** | (none) |
| OOS signal, reason mismatches | `expect-oos` | **fail** | Either update `Reason` or fix the throw site to emit the correct reason |
| Failed with no OOS signal | `expect-oos` | **fail** | Either implement the surface, or make the throw site raise `RunnerOutOfScopeException` / the documented message |
| Passed cleanly | `expect-oos` | **fail** | Runner now implements the surface — remove the manifest entry |
| OOS signal | absent | **fail** | New OOS surface — add a manifest entry citing the scope.md reason |
| Any non-pass result | `expect-fail-known-gap` | **pass-known-gap** | (none) |
| Passed cleanly | `expect-fail-known-gap` | **fail** | Gap is fixed — remove the entry and close the linked issue |
| Failed with no OOS signal | `expect-divergence` | **pass-divergence** | (none) |
| OOS signal | `expect-divergence` | **fail** | Wrong mode — declare it `expect-oos` with the thrown reason |
| Passed cleanly | `expect-divergence` | **fail** | Runner no longer diverges from BC — remove the entry |
| n/a | `skip` | **skipped** | (none — does not contribute to pass/fail counts) |
| Any normal outcome | absent | **pass/fail** as usual | (none) |

Manifest drift in any direction is loud: silent additions, silent fixes, and
silent regressions all surface as test failures with explicit diagnostics
telling the reader what to do.

## Reporter output

```
Tests:         1945 total
  pass:        1945
    pass-oos:        2
    pass-known-gap:  3
    pass-divergence: 1
  fail:        0
```

The reclassified categories are surfaced separately (and omitted when zero) so
a clean run does not hide manifested deviations from the corpus.

## Authoring rules

1. **One reason per `expect-oos` entry.** The reason must reference a section
   anchor in `docs/scope.md`. If a new surface is OOS for a reason not yet
   documented, add the section to `docs/scope.md` in the same PR.
2. **`expect-fail-known-gap` requires an `Issue` link.** No untracked known
   failures. The issue must be open at PR time. Nothing in the run can check
   that — the manifest is evaluated in-process with no network — so when the
   issue closes, the entry must move to whichever mode is now true
   (`expect-oos`, `expect-divergence`) or be deleted. A known-gap entry pointing
   at a closed issue is the exact bookkeeping lie this mode set exists to avoid.
3. **`expect-divergence` requires `Reason` + `Doc`, and forbids `Issue`.** It
   declares a standing decision, so it has to point at where that decision is
   recorded. If you find yourself wanting to link an issue, the entry is a gap,
   not a divergence.
4. **`skip` is a last resort.** Prefer fixing the compile gap or quarantining
   in the corpus repo via preprocessor symbols. Use `skip` only when neither
   is feasible.
5. **No `Note` lies.** The note is human-readable context that survives
   reviewers reading the diff months later. Either keep it accurate or omit it.
6. **Schema validation.** The loader (`ExpectationManifest.cs`) rejects
   unknown `Mode` values and missing required fields. Runner startup fails
   loudly if any expectation file is malformed.
