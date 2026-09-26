# How `--coverage` attributes a statement, and why a packaged `.app` dependency drops out

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
old `scope.GetType()` keying and are kept as the record of why the key moved.

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

## The embedded-source route is a dead end, and this is the measurement

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
