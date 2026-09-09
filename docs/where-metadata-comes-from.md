# Where a table's metadata comes from

The runner needs BC's own answer for every object it serves — key counts, per-field `Editable`,
`DataClassification`, relations. **Where that answer comes from depends on what the app ships**, and
the three shipping shapes are genuinely different problems with different costs. This document names
them, because the distinction was being rediscovered per issue instead of written down once.

The route a table actually took is observable:

```
AL_RUNNER_TRACE_TABLE_METADATA_SOURCE=1    # one line per built table: [table-metadata] <id> source=<route>
```

Exactly `"1"` or `"2"` — any other value, including `"true"`, is a silent no-op. There are exactly
two route values, `bc-document` and `derived`, and no failure route: a failed document read refuses
rather than falling through to the derivation (`loud-failures.md`).

## The three shapes

| the app ships | source? | metadata XML? | what the runner does | cost |
|---|---|---|---|---|
| **source only** | yes | no | compiles it; the emitter's document is captured and consumed | one compile, paid once per app per BC version |
| **source + a prebuilt DLL** (Base Application, most Microsoft apps) | yes | **no** | reads `SymbolReference.json` — **does not compile** | a symbol-file read |
| **a runtime package** (`.NEA`) | no | **yes** | reads `/bin/*.xml` straight out of the package | a container decode |

The middle row is the one that keeps getting mis-stated, so it is worth being exact.

### An R2R `.app` ships source AND no metadata

Measured on `Microsoft_Base Application_28.4.53241.53989.app`: **8,095 source files, 0 `/bin`
metadata documents.** So "ships a DLL" does not mean "ships no source" — it means the source is
there but was compiled by Microsoft ahead of time, and **we do not want to compile it again**.

That is a deliberate choice, not a limitation. `.claude/rules/no-base-app-in-csharp-tests.md` has the
measurements: loading the Base Application floor costs about **70 seconds cold and 6 seconds warm per
runner invocation**. Recompiling it to obtain metadata is far worse — roughly **2 minutes and ~9 GB
peak RSS**. For an app that already ships a working DLL, that is spending a compile to learn what
`SymbolReference.json` already states.

**So for this shape the answer is the symbol file, and the symbol file is held to a standard rather
than treated as second-class.**

## The symbol-file path is not a fallback of last resort

The standard, from #3533:

> Where we have the code, derive metadata faithfully by compiling it with BC's own compiler. Where we
> do not, fall back to `SymbolReference.json` — and that fallback must produce the **same answer** the
> source-backed generator would, for every piece of information the symbol file can express.

That is testable rather than aspirational, and it is tested: any app the runner source-compiles
yields **both** derivations in the same run — BC's metadata XML from `CaptureOutputter`, and ours
from `SymbolReference.json` — so the standard is a differential assertion over the same tables.

The gap that measurement found was one-directional every time: BC sets a real value, the runner took
a constructor default.

| property | wrong | BC said | we said |
|---|---|---|---|
| `Editable` | 1,262 / 2,036 | `False` | `True` |
| `DataClassification` | 1,561 / 2,036 | `SystemMetadata` / `EndUserPseudonymousIdentifiers` | `CustomerContent` |

#3545 fixed two defined meanings of an *absent* property the reader was ignoring: `Editable` defaults
to `true`, and a field's `DataClassification` inherits the table's. Both issues are closed.

**What the symbol file cannot express, it cannot express**, and no amount of reader work changes
that — which is why the compile path exists for apps that ship source without a DLL, and why the
runtime-package path exists for packages that ship metadata without source.

## Why this matters for the conversion work

`#3562` tracks converting every metadata consumer to BC's captured document. It has two halves, and
conflating them makes the tracker read as finished when it is half done:

- **Objects the runner compiles** — done, merged, verified. These take the `bc-document` route.
- **Dependencies** — where nearly all of the error surface lives, and the un-started half. `#3549` is
  the issue, with 20 more blocked behind it.

Measured, so it is not a worry anyone needs to re-raise: a source-compiled dependency's document is
**consumed, not merely captured**. It is persisted as a sidecar, replayed by `DependencyLoader`, and
the table takes the `bc-document` route on a cold run *and* on a cache HIT.

Not measured, and stated as such rather than inferred: pages and pageextensions have **no route
trace**, so whether their converted consumers fire is unverified in either direction.

## The sequencing, and why

The repository owner's call, recorded here because it is a judgement about what the runner is for
rather than a technical constraint:

> **Runtime packages are a side story. Get the regular cases working first.**

The regular cases are the first two rows of the table — an app that ships source, and an app that
ships source plus a DLL where we read symbols instead of recompiling. Between them they cover
essentially every real project. The runtime-package reader is merged (#3537, #3733) and is not going
anywhere; **executing** from a runtime package remains unproven and is tracked separately as #3732.

## Sister material

- `.claude/rules/no-base-app-in-csharp-tests.md` — what loading the Base Application floor costs, and
  why a fixture declares `platform` and never `application`
- `docs/object-metadata-capture.md` — how the emitter's document is captured
- `docs/metadata-equivalence.md` — the differential harness that holds the two derivations to each other
- `docs/runtime-packages.md` — the `.NEA` container, and what reading one does and does not prove
