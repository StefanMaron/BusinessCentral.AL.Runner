# A query dataitem's `DataItemTableFilter`, and its multi-field `DataItemLink`

How the two static dataitem properties reach the runner's query execution, which of the two
metadata construction routes carries them, and why the fix for each landed where it did.
Issues #3571 and #3572.

## The two routes, and why only one of them was broken

A query's `MetaQuery` design object is built by one of two routes, chosen on **availability**
in `RecordPatches.NclMetaQueryBuilder.BuildMetaQueryDesign`:

| route | when | source |
|---|---|---|
| **BC document** | the runner COMPILED this query | BC's own emitted metadata document, captured by #3548, parsed by `Types.Metadata.MetaQuery(XmlNode, int, int)` |
| **SymbolReference derivation** | the query lives in a **precompiled dependency** `.app` | `SymbolReference.json`, read by `BcAppSymbolCache` |

#3608 / PR #3617 converted the first route. A query in a precompiled dependency was never
emitted here, so no document exists and the derivation stays — and that is the route both
issues report.

Measured on BC 28.1, parsing the documents BC's own emitter produced for the two fixture
queries in `tests/runner-extras/query-dataitem-filter-precompiled-dep`:

```
query 65893 (DataItemTableFilter = Status = const(Open))
  DI Row  tableNo=65890 linkType=None
     FF fieldNo=2 type=CONST value='0'
     FieldFilters count=1

query 65894 (DataItemLink = "Header No." = Hdr."No.", "Variant Code" = Hdr."Variant Code")
  DI Ln   tableNo=65892 linkType=InnerJoin
     LINK src=Hdr srcF=1 dstF=1 op==
     LINK src=Hdr srcF=2 dstF=2 op==
     DataItemLinks count=2
```

So on the document route both properties already arrive correctly, and `FieldFilters` is
**not** empty after BC parses a document carrying `<DataItemTableFilter>` — a question left
open on #3571 and settled by the measurement above.

## Where a static table filter lands, and why it is not `ColumnFilters`

`DataItemTableFilter` and `ColumnFilter` (#2418) look alike in AL and are different inputs:

| | keyed by | needs a projected column? | design-object home |
|---|---|---|---|
| `ColumnFilter` | a **query column** id | yes — it names a column of the query | `MetaQuery.ColumnFilters` |
| `DataItemTableFilter` | a **table field** number | no | `MetaQueryDataItem.FieldFilters` |

The distinction is load-bearing: a `DataItemTableFilter` routinely filters on a field the
query never projects (the fixture filters on `Status` while selecting only `Code`), so there
is no column id to key it by and `ColumnFilters` cannot express it.

`MetaQueryDataItem.FieldFilters` is a `List<MetaQueryFieldFilter>`, and
`MetaQueryFieldFilter` carries exactly `FieldNo`, `TypeOfFilter` and `Value` — the three
things the AL property states. BC then does the rest itself, in
`NCLMetaQuery.CreateTableFiltersAndMarksFromDataItemFieldFilters`, which:

- resolves each `FieldNo` against the dataitem's `MetaTable`,
- refuses a filter on a non-`Normal` field class (`QueryDataItemTableFilterOnFlowFieldNotSupported`),
- builds a `UnaryFilterExpression(Equal, …)` for `CONST` and runs `FilterExpressionParser.Parse`
  for `FILTER`,
- **ANDs together** multiple filters that land on the same field, and
- returns a `FiltersAndMarks`, which it parks on `NCLMetaQueryDataItem.TableFiltersAndMarks`.

So the runner's whole job for #3571 is to populate `FieldFilters`; every semantic above is
BC's own code, unmodified.

## The execution half, and the single-dataitem gap

`TableFiltersAndMarks` was already being consumed — but on one path only:

- **Join path** — `RecordPatches.QueryJoin.BuildTableFindAllRequest` reads each dataitem's
  `TableFiltersAndMarks` when it builds that dataitem's own read request. Correct, per
  dataitem, and unchanged by #3571 — but it used to answer a DEFAULT when the read FAILED,
  which is what #3656 fixed. See "The join path refuses too" below.
- **Single-dataitem path** — `RecordPatches.QueryProjection.TranslateQueryFilters` handled
  runtime `SetRange`/`SetFilter` filters and static `ColumnFilters`, and read
  `TableFiltersAndMarks` nowhere.

That asymmetry is the #3571 execution defect, and it is why the filter was missing even on
the document route where the metadata was already correct. `GetSingleDataItemTableFilterTuples`
closes it.

Two properties of that helper are deliberate:

- **It yields nothing for a multi-dataitem query.** The join path already applies these per
  dataitem; yielding them here as well would push one dataitem's table-field filter into a
  request covering a different dataitem's table.
- **Its tuples need no retargeting.** The two existing passes in `TranslateQueryFilters`
  retarget a filter keyed by an `NCLMetaQueryColumn` onto that column's `SourceTableField`.
  BC already keyed these by `NCLMetaField` — a table field — so they are added as-is.

It excludes a FlowField-calculation synthesized dataitem when counting, for the same reason
#2300 excludes it in `DataAccessSource_GetDataAccessForQuery`: counting it makes a genuinely
single-dataitem query look like a join, and the pass would then skip itself entirely.

## The multi-field `DataItemLink` (#3572)

AL permits a comma-separated list of equalities:

```al
DataItemLink = "Header No." = Hdr."No.", "Variant Code" = Hdr."Variant Code";
```

`SymbolReference.json` states that as **one string** carrying both, which is what the
compiler emits verbatim:

```json
{ "Name": "DataItemLink",
  "Value": "\"Header No.\" = Hdr.\"No.\", \"Variant Code\" = Hdr.\"Variant Code\"" }
```

`ParseDataItemLink` used to take the first `=` and the first `.` of the whole string. On the
value above that yields a source field named, literally:

```
No.", "Variant Code" = Hdr."Variant Code
```

which resolves to no field, so the parse answered null and `BuildMetaQueryDesign` **abandoned
the entire build**. `NCLMetaQuery` was then null, and the first AL operation on the query
dereferenced it inside `NavQuery.ValidateTablesNotVirtual` — the `NullReferenceException`
#3572 reports, which is a symptom of the abandoned build rather than a separate defect in
that method.

`ParseDataItemLinks` splits on **top-level** commas and parses each equality separately, with
`TopLevelIndexOf` for both the `=` and the `.`. Top-level matters in three places at once: a
quoted AL identifier may legally contain a comma, an `=` and a `.`, and `"No."` contains one
of them in every single-field link there has ever been. The design object's `DataItemLinks` is
a `List` precisely because more than one link per dataitem is the supported shape, and BC's
own document states one `<DataItemLink>` element per equality.

A failure to parse or resolve **any** equality refuses the whole property. Applying a subset
would join on fewer fields than AL declares, which widens the result silently — the failure
mode that is worse than the loud one, and the reason this returns null rather than what it
managed to parse.

## Parsing the filter text

`DataItemTableFilter` shares its grammar with `ColumnFilter`, so it reuses
`RecordPatches.AlSourceParser.TryParseColumnFilterText`: a comma-separated list of
`<Name> = const(<value>)` / `filter(<expr>)`, split with the same quote-aware
`SplitTopLevelCommas` and `TopLevelIndexOf`. That satisfies the issue's "quoted names and
`CONST`/`FILTER` values parse without naïve comma splitting" criterion by construction rather
than by a second parser.

The one difference is what the left-hand side names. For `ColumnFilter` it is a **query
column** of the same query, resolved against `columnIdByName`. For `DataItemTableFilter` it
is a **table field** of the dataitem's own `RelatedTable`, resolved against that table's
`fieldNoByName`. An unresolvable name abandons the build in both cases, matching the
`ColumnFilter` discipline: running the query unrestricted returns rows real BC excludes.

## The join path refuses too

#3656, the sibling of #3647 one code path over. `BuildTableFindAllRequest` builds the
per-dataitem read request for a **multi-dataitem (join)** query, and reached the same
`NCLMetaQueryDataItem.TableFiltersAndMarks` through a lookup whose failure answered a default:

```csharp
object? filtersAndMarks = null;
try
{
    var p = dataItem.GetType().GetProperty("TableFiltersAndMarks", ...);
    filtersAndMarks = p?.GetValue(dataItem);
}
catch { filtersAndMarks = null; }
filtersAndMarks ??= StaticMember("FiltersAndMarks", "Empty");
```

Three separate ways to answer "no filters" for something that is not an empty answer — an
unresolved property, anything the getter raised, and a fallback indistinguishable from a
dataitem that genuinely declares none.

### Why the consequence is worse here than a wrong filter

The one call site is `JoinExecutor.ReadDataItemRows`, and it reads **two** of this method's
answers as ordinary facts:

| the builder answers | ReadDataItemRows does | observable result |
|---|---|---|
| a request carrying `FiltersAndMarks.Empty` | reads the dataitem's table unfiltered | the join returns **more rows than it should** |
| `null` | `if (req == null) return result;` | that dataitem contributes **zero rows**, collapsing the whole join to empty |

Neither throws. A passing test cannot tell the difference unless it asserts the row count,
which is the silent-default shape `.claude/rules/loud-failures.md` forbids.

### The bare `catch` was the second half of the defect

`catch { filtersAndMarks = null; }` swallowed **everything** the getter raised, including a
`BcShapeGapException` from a read beneath it — the one exception type that exists to tear
through both of AL's trapping seams. Converting the lookups to throw without removing that
catch would have produced refusals this very method ate: a guard that is quiet rather than
armed.

It also swallowed BC's own errors. `NCLMetaQuery.CreateTableFiltersAndMarksFromDataItemField-`
`Filters` raises `NavNotSupportedException` for a `DataItemTableFilter` on a FlowField, and the
getter can reach it; the old code turned that into "no filters" and ran the query, instead of
reporting the error real BC reports.

Nothing is caught now. The one `catch` that remains rethrows the getter's real exception
through `ExceptionDispatchInfo`, never a bare `throw tie.InnerException` — the same
stack-trace-preservation reason `ExecuteJoinQuery` gives a few lines above it. Without the
unwrap a caller sees a `TargetInvocationException` whose text names reflection rather than the
member that moved.

### Which exits are now loud, and which stay silent

Five lookups refuse; one exit stays silent, and it is the one that carries most traffic.

| exit | now | why |
|---|---|---|
| `FindProviderRequest` type lookup | **refuses** | a moved type; `!` would NRE inside the builder naming a parameter |
| `FiltersAndMarks` / `TableFilterDictionary` type lookups | **refuses** | same |
| `FindProviderRequest` ctor not found | **refuses** | used to `return null`, which the executor read as "no rows" |
| `TableFiltersAndMarks` lookup | **refuses** | the member #3656 names |
| `FiltersAndMarks.Empty` / `TableFilterDictionary.Empty` statics | **refuses** | `Empty` singletons that always exist on a real Ncl; absence is a moved member |
| anything the getter raises | **propagates** | was swallowed by the bare `catch` |
| **`TableFiltersAndMarks` reads null** | **silent** | **BC's own answer** — see below |

The silent row is load-bearing and must stay silent. Measured on the `bc284` context,
`NCLMetaQuery.CreateTableFiltersAndMarksFromDataItemFieldFilters` opens with

```csharp
if (fieldFilters.Count == 0) { return null; }
```

and ends with a second `return null;` when no filter expression was built. So **null is BC's
answer for "this dataitem declares no `DataItemTableFilter`"** — the ordinary case for most
join dataitems. Converting it would turn every unfiltered join into an error.

### What proves it

`AlRunner.Tests/QueryJoinFindRequestShapeGapTests` — 12 arms, driving the real production
helper with fakes standing in for a BC type whose member moved, the same idiom
`PermissionMetadataShapeGapTests` uses. RED baseline **6 failed / 4 passed**, every failure
reading `Assert.Throws() Failure: No exception was thrown`; GREEN **12/12**.

The negative controls are what stop the fix becoming a blanket conversion, and they are
load-bearing rather than decorative: a sabotage that refuses on a *successful* read of null
breaks `ADataItemWhoseTableFiltersAndMarksReadsNull_GetsEmpty_AndDoesNotThrow` and
`TheRequestCarriesTheTableAndTheEmptyGlobalFilters`.

`TheAbsentMemberAndTheNullRead_AreDistinguishedByWhetherAnythingIsThrownAtAll` pins the pair in
one place. #3647's equivalent arm was found *unarmed* because both of its outcomes refused and
only the message wording separated them; here they are separated by whether anything is thrown
at all, so a fix collapsing the two cannot pass both lines. Verified in both directions —
reverting the lookup fails it with `IsType Failure: Value is null`, and over-refusing fails it
with `Assert.Null Failure: Value is not null`.

## What proves it

- `tests/runner-extras/query-dataitem-filter-precompiled-dep` — the AL suite, on a genuinely
  precompiled dependency so the derivation route is the one under test. RED on `origin/main`
  at `96e1cbc4`: `Expected 1 but got 2` for the filter, and the
  `ValidateTablesNotVirtual` `NullReferenceException` for the composite link.
  `REGENERATE-FIXTURES.txt` in that folder has the fixture recipe and why the source must
  stay outside the suite.
- `AlRunner.Tests/BcAppSymbolCacheQueryDataItemFilterTests` — the symbol layer, asserting the
  property text arrives verbatim on root and nested dataitems, with a negative control for a
  dataitem declaring no filter.

## A failed lookup refuses

Issue #3647. `GetSingleDataItemTableFilterTuples` reads four BC members through reflection,
and its early exits were all the same `yield break`. Two different facts shared it:

- **a failed reflection lookup** — "BC does not have the member I need", and
- **an empty answer** — "this dataitem legitimately has no filters".

The one call site is a `foreach` that adds whatever it yields, so it cannot tell them apart.
That made a BC rename of `NCLMetaQueryDataItem.TableFiltersAndMarks` **unapply the filter
silently**: the query returns more rows than it should, nothing throws, and a test only
notices if it asserts the row count. `.claude/rules/loud-failures.md` forbids that shape, and
`AlRunner/Infrastructure/BcShapeGapException.cs` puts a read that could not be *performed* on
the refusing side.

Latent, not live: every BC version this repository tests resolves all four, and
`tests/runner-extras/query-dataitem-filter-precompiled-dep` covers the live path. The change
is about the BC version where one of them moves.

### Which exit is which

| exit | now | why |
|---|---|---|
| `QueryDefinition` lookup | **refuses** | the member is absent — BC's layout moved |
| `QueryDefinition` reads null | silent | an answer: this metaquery carries no definition |
| `DataItems` lookup | **refuses** | as above, and worded so it cannot be confused with the next row |
| `DataItems` reads null | **refuses** | BC builds no query without a dataitem, so there is no empty-list answer this could be |
| `DataItems` holds a non-enumerable | **refuses** | present but uninterpretable is the same "layout moved" case |
| `SubQueryDefinition` / `TableFiltersAndMarks` lookup | **refuses** | the members #3647 names |
| `di == null`, sub-query dataitem | silent | structural — #2300's exclusion, not a failed read |
| `real.Count != 1` | silent | the method is explicitly the single-dataitem case; the join path applies a multi-dataitem query's filters itself |
| `TableFiltersAndMarks` reads null | silent | BC's answer: this dataitem has no filters |
| `Filters` reads null | silent | BC's answer: no filter dictionary |
| `Items` lookup | **refuses** | absent member |
| `Items` reads null | silent | BC's answer: an empty dictionary |

The refusals raise `BcShapeGapException`, which tears through both AL trapping seams — under
`asserterror` a swallowed refusal would *invert* the result, since real BC reads the filter
fine and the asserterror fails there.

### What is not covered

`RecordPatches.QueryJoin.BuildTableFindAllRequest` reads the same
`TableFiltersAndMarks` for the **join** path and has the same shape, plus a bare `catch`
that falls back to `FiltersAndMarks.Empty` — so a rename unapplies a join dataitem's filter
just as silently. Different file and different call path, so it is tracked separately rather
than folded in.

### What proves it

`AlRunner.Tests/QueryDataItemFilterShapeGapTests` — five arms for the refusals, each removing
one member and leaving the others, and four negative controls for the exits that must stay
silent. The method takes its three BC types as parameters so fakes can stand in for a moved
member without a BC install and without poisoning the `RecordPatches` statics for other tests.
