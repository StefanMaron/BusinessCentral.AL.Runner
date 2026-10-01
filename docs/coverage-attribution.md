# How `--coverage` attributes a statement, and which packaged dependencies it can place

`AlCoverageTracker` records a hit per `(scope type, AL statement index)` from a Cecil-rewrite
hook on `NavMethodScope.StmtHit(int)`, then resolves each scope type back to an AL object and a
source line. This file records what that resolution actually depends on, because the dependency
is not the one the code's comments claimed until #4273 measured it.

All figures below were measured on BC `28.1.49838.53910` — the only artifact set on the box that
took them — with `Microsoft_System Application_28.1.49838.53910.app` as the packaged dependency.
One build, so nothing here is a cross-version claim.

**Since #4697 the key is not `scope.GetType()`.** The runner compiles AL in BC's inline-scope mode,
and every reader keys on `AlScopeKey.Of(scope)` (`AlRunner/Infrastructure/AlScopeKey.cs`): the AL
method an `ALMethodScope` runs — found by its `[MethodId]` on the object type and cached per
`(type, id)` — and the scope class otherwise (event publishers keep one). So question 1 below is
asked of that member, and a packaged `.app`'s statements now pass questions 1-3; they still stop at
question 4, because no root maps a packaged object. The measurements below were taken under the
old `scope.GetType()` keying and are kept as the record of why the key moved. What #4273 then
changed at question 4 is [the next section](#what-a-packaged-dependency-contributes-since-4273).

## What a packaged dependency contributes since #4273

A packaged dependency reaches a run in one of two ways, and the report treats them differently.

| the package's code is | attributed? | why |
|---|---|---|
| **compiled by this run from the package's embedded AL** (`DependencyLoader` Tier 3: the `.app` ships `src/*.al` and no DLL — what `alc` produces with `includeSourceInSymbolFile`) | **yes** | the `[SourceSpans]` came from compiling exactly the text the map parses |
| **precompiled** — a `.deps-bin` sidecar (Tier 1), an R2R DLL inside the `.app` (Tier 2), a service-tier DLL (Tier 2.5) | **no, and the run names it** | nothing ties the DLL to the package's `src/` text, so a line mapping would be unverified |

**Attributed:** `PackagedDependencySources` records each Tier-3 package and, when a coverage map
is built, materializes its `src/**/*.al` into `compiled-deps/<cacheKey>.src/` beside the compiled
DLL, at the package's own paths. `AlCoverageSourceMap.RootsWithParsedSourceDependencies` adds
those directories, so the CLI, `--server` and the DAP maps all see them. The cache key hashes the
package's content, so the directory is written once and reused by every later run; the warm run
(a `source-cache HIT`, which never writes the Tier-3 scratch directory) reads the same one.
Pinned cold and warm, against one cache root, by `CoveragePackagedDependencyTests`.

The registry is cleared with the other per-request source state (`RecordPatches.ResetForReload`,
once per `--server` request and `--watch` cycle), and a request that reuses an already-loaded
package registers it again. Held for the whole process, it made a later request re-read a
package only an earlier one had resolved, and fail when that file had gone
(`ServerCoveragePackagedDependencyTests`). A package whose AL cannot be extracted is reported as a
scan failure — the report is marked incomplete — rather than failing the request.

**Microsoft's packages are not recorded**, Tier 3 or not. The runner compiles Microsoft's Test
Runner this way on an ordinary run (measured on `28.1.49838.53910`); mapping it filled a
one-codeunit fixture's report with the Test Runner's files and cut its total from 50% to under 5%.
Microsoft code is outside the report by design, the same as Microsoft's precompiled apps.

**Named, not attributed:** after writing the report the CLI lists every non-Microsoft dependency
whose AL executed and that no root maps:

```
Coverage note: AL executed in these dependencies is not in the report above, because their code was precompiled rather than compiled from source by this run (docs/limitations.md#packaged-dependency-coverage):
  AL Runner_Runner Tests Fixture - Coverage Dependency Source Subject_1.0.0.0  (1 object(s) executed)
```

It reads the hit-tracked keys only (`AlCoverageTracker.UnattributedExecutedObjects`), which is
where a question-4 failure means AL that ran — see
[the two causes](#the-two-causes-of-a-null-resolution-are-separable-4350). It is a note, not an
incomplete-coverage exit: the omission is declared in `docs/limitations.md`.

## A sibling source folder next to a package (#4991)

In the layout `App/` (source), `App.Test/` (source) and `App.Test/.alpackages/App.app`, with a run
naming only `App.Test`, the code that runs is the package's, but `BuildSiblingSourceDeps` still
registers `App/` as a parsed source folder, and `Build` keeps the last root's mapping of an object.
Before #4991 every object of `App/` therefore won over the package's own source, and when `App/`
had been edited without rebuilding the package, `--coverage`, `coverage` and `perTestCoverage`
named lines of `App/` that held something else — measured: a comment on the line reported as hit.

`RootsWithParsedSourceDependencies` now marks a registered folder that is not an execution root and
whose `app.json` names a Tier-3 package recorded by `PackagedDependencySources`. `Build` attributes
such a folder's object only when the package's root mapped the same object and the two texts are
equal line for line: the file's preamble plus the object's own lines, which is the text a decoded
`[SourceSpans]` line indexes, so equal texts give equal file lines. Per object, not per file: an
object whose text matches keeps `App/` even when another object in the same file was edited.

| `App/`'s object | reported against |
|---|---|
| equal to the package's | `App/`, as before |
| different | the package's embedded AL under `compiled-deps/<cacheKey>.src/` — what a package with no sibling folder gets |
| not in the package's root, or unreadable on either side | the package's mapping if it has one, else nothing; never `App/` unverified |

A package whose AL could not be extracted maps nothing, so its sibling's objects stay unmapped and
the map carries the package's scan failure: the report is marked incomplete rather than filled
from text nobody verified. Pinned by `CoveragePackagedSiblingSourceTests` (CLI and `--server`) and
`CoveragePackagedSiblingSourceMapTests` (the rows, in process).

`affectedOnly` ignores a covered package's statements in either place: `PackagedSourceRoots` lists
the package's `compiled-deps` root beside its source folder (see
[server-mode.md](server-mode.md#affectedonly-and-packaged-dependencies)).

Not covered: a **precompiled** package (Tier 1, 2 or 2.5) next to a sibling source folder. No
embedded text was compiled, so there is nothing to compare against, and the folder is attributed
as before. Not measured; tracked in #5155.

## The resolution chain

`AlCoverageTracker.ResolveScopeInfo` asks four questions in order, and any one of them ends the
attribution:

1. does `scope.GetType()` carry BC's `[SourceSpansAttribute]`?
2. is its `EncodedSpans` array non-empty?
3. does `AlCallStackCapture.ParseObjectTypeAndId` yield a non-zero AL object id?
4. is that `(label, id)` in the `AlSourceLocationMap`?

Only question 4 is about source on disk. #3965 and #4272 were both failures of question 4, which
is why widening the root set fixed them.

**There is a fifth gate, downstream of all four, and leaving it out makes this document look
wrong.** Both callers then ask `AlCoverageInstrumentedStatements.Find(scopeType)` for the span
indices BC actually backed with a `StmtHit`/`CStmtHit` call, and emit a line only for those. BC
always emits one trailing span beyond the highest instrumented index — a sentinel at the method's
closing `end;`, which can never register a hit — so **a scope whose `EncodedSpans` holds exactly
one entry has the sentinel and nothing else: zero coverable statements.** It passes questions 1-3
and contributes no line either way.

That is not a footnote here, because it is the only reason the packaged assembly's 155
attributable scopes do not appear in the report. Measured on the System Application assembly:
**all 155 have exactly 1 span**, against `1 span × 26` and `≥2 spans × 8,331` at method level.
They are event publishers, and an event publisher has an empty body.

## The two scope shapes BC's compiler emits, and where `[SourceSpans]` sits

A statement hit is keyed on `scope.GetType()`, and BC's compiler produces **two different runtime
scope shapes** for AL. They put `[SourceSpans]` in different places, and the runner reads only one
of them.

| | AL the runner compiles from source | Microsoft's shipped `.app` |
|---|---|---|
| runtime scope type | a dedicated `<Method>_Scope_<hash>` class per method | the shared generic `Microsoft.Dynamics.Nav.Runtime.ALMethodScope<TLocals>` |
| `[SourceSpans]` sits on | the scope **type** | the **method** |
| `ParseObjectTypeAndId(scope.GetType())` | resolves, e.g. `(CodeUnit, 70860)` | resolves nothing — the type is Ncl's, not the app's |
| question 1 above | passes | **fails** |

Measured, on the runner-emitted dependency assembly for fixture
`AlRunner.Tests/Fixtures/CoverageDependencySource/dep` (5 types): `Never_Scope__889937171` and
`Twice_Scope_1516892452`, both carrying type-level `[SourceSpans]`, zero method-level.

Measured, on the packaged System Application assembly (11,022 types, 8,512 `[SourceSpans]`
applications): **155 type-level and 8,357 method-level.** The ordinary AL method of a packaged app
is in the 8,357, where the runner does not look.

All 155 are event-publisher scopes, and the check for that is the **attribute, not the name**:
every one of the 155 backing methods carries `Microsoft.Dynamics.Nav.Types.NavEventAttribute`
(155 of 155, none without). A name test gets this wrong — 152 of the 155 are `On*_Scope`, and the
three that are not (`ProcessSharePointFileMetadata_Scope`, `ProcessSharePointListItemMetadata_Scope`,
`GetPrinterSelectionsPage_Scope`) are event publishers exactly like the rest. All 155 do end in
`_Scope`, and their outermost declaring types are `Codeunit` × 149, `Page` × 5, `XmlPort` × 1.

## What that produces on a real run

One test calling `Base64Convert.ToBase64('abc')`, with System Application supplied through
`--package-cache`:

| | sibling **source** dependency | packaged **`.app`** dependency |
|---|---|---|
| hit-tracked scope types | 2 | 98 |
| of those, carrying type-level `[SourceSpans]` | 2 | **1** (the consuming test codeunit) |
| the other 97 | — | `ALMethodScope<T>`, from `Microsoft.Dynamics.Nav.Ncl` |
| classes in the Cobertura report | 2 | **1** |

The packaged dependency's AL genuinely executes and its statements are genuinely counted — 97
distinct scope types recorded hits. They are discarded at question 1.

## The embedded-source route was a dead end under the old key

**Superseded by #4697 and #4273** — kept because the measurement explains why the key had to move
first. Under `scope.GetType()` keying no amount of source could help; under `AlScopeKey` the
embedded source is exactly what attributes a Tier-3 package.

#4273 proposed recovering attribution from the `.app`'s embedded AL source. The premise that the
source is unavailable is **false** — Microsoft's `.app`s ship it, 1,319 `.al` files under `src/`
in System Application, and `AppLoader.ExtractAlWithPaths` already reads them — but supplying it
does not help, because the failure is at question 1 and the source map is question 4.

Extracting all 1,319 files and adding that directory as a coverage root:

| | map entries | classes in report |
|---|---|---|
| without the extra root | 1 | 1 |
| with the extra root | **1,054** | **1** |

A thousand-fold larger source map, byte-identical output. Any fix that begins by finding more
source is answering a question that was never the one failing.

## The gap is reachable, so it is not-yet-implemented rather than out of scope

The scope **instance** passed to `StmtHit` knows everything the type does not.
`ALMethodScope.GetDeclaringMethodInfo()` returns the executing AL method, and measured on eight
distinct packaged scopes it resolved every time:

```
scopeType=ALMethodScope`1 declaringMethod=OnInstallAppPerDatabase methodHasSourceSpans=True
    declType=Codeunit9056 parsed=(CodeUnit,9056) appObj=Codeunit9056
scopeType=ALMethodScope`1 declaringMethod=HasUpgradeTag        methodHasSourceSpans=True
    declType=Codeunit9996 parsed=(CodeUnit,9996) appObj=Codeunit9996
```

So the method carries its `[SourceSpans]`, its `DeclaringType` is the AL object type, and
`ParseObjectTypeAndId` already resolves that to `(CodeUnit, 9056)`. `ALMethodScope` also exposes
`MethodId` and `ApplicationObject`.

**This is why `docs/limitations.md` records the gap under "Known gaps", not as an exclusion.**
Writing it down as out of scope would have been a false claim, and route 2 of #4273 proposed
exactly that.

What a fix still has to answer, none of which #4273 measured:

- **Cost, and it is worse than a dispatch.** `OnStmtHit` runs on every AL statement.
  `GetDeclaringMethodInfo` is **`internal` and non-virtual** — measured on
  `Microsoft.Dynamics.Nav.Ncl.dll` sha256 `49b11d9b…`: `attrs = Assembly, HideBySig`,
  `isVirtual = False`, a **376-byte** IL body plus a 58-byte closure helper
  `<GetDeclaringMethodInfo>b__24_0`. So the runner cannot simply call it — it needs reflection or
  a cached delegate — and what it then runs per statement is substantial rather than a cheap
  virtual dispatch. A per-scope-**type** cache does not rescue this, because one generic type
  serves thousands of methods.
- **A method-keyed hit table.** `_hits` is `(Type, int)` and `AlCoverageInstrumentedStatements.Find`
  takes a `Type`; both need method-keyed analogues.
- **Where the reported file path points.** The package's entries are URL-double-encoded
  (`src/Base64%2520Convert/...`) and are not on disk, so a Cobertura `filename` for them is a
  design decision, not a lookup.

## The two causes of a null resolution ARE separable (#4350)

#4350 records that `TryResolveScope`'s silent null cannot distinguish *packaged dependency, no
source* from *source dependency outside the root set*. From the source map alone that is true.
From the scope type it is not — the two fail at **different questions**, and the same run
measures both:

| | fails at | `noSourceSpans` | `not-in-map` |
|---|---|---|---|
| source dependency outside the root set | question **4** | 0 | **1** |
| packaged `.app` dependency | question **1** | **97** | 0 |

Disjoint, in one run each. A refusal that wants to name its own cause can already tell them
apart; what it cannot do is tell either of them from a genuine "this object has no executable
statements", which is the part that stays hard.

**One bound on that, and it decides where such a refusal may live.** A question-1 failure is also
what every non-AL framework type produces — `ALMethodScope<T>` is one, but so is any other class
that never carried `[SourceSpans]`. The discrimination is therefore only meaningful where the
population is already known to be AL that executed, i.e. the **hit-tracked** paths
(`CollectStatementTable`, `CollectPerTestStatementTable`, and the `StmtHit`-time resolver), which
is where the 97 above were counted. It is meaningless in `Collect`, which scans every loaded
assembly and whose question-1 rejections are overwhelmingly types that are not AL at all. Since
`TryResolveScope` is shared by both, a third state added inside it would inherit the wrong
population; the caller is what knows which it has.
