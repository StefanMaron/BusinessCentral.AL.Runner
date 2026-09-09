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
  dataitem, and unchanged by #3571.
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
