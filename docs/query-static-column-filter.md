# A query's static `ColumnFilter` conditions

AL lets a query column carry a design-time filter:

```al
query 50100 "Customers By Country"
{
    elements
    {
        dataitem(Cust; Customer)
        {
            column(Country; "Country/Region Code") { ColumnFilter = Country = const('DE'); }
            column(Name; Name) { }
        }
    }
}
```

BC's `NclMetaQueryBuilder.BuildMetaQueryDesign` collects those into `MetaQuery.ColumnFilters`,
and `BuildFilterExpressionCollection` turns them into `NCLMetaQuery.ColumnFilters` — a
`ReadOnlyCollection<Tuple<NCLMetaQueryColumn, FilterExpression>>`. The property is `internal`
to `Ncl.dll`, so `RecordPatches.GetStaticColumnFilters` reads it through reflection.

Two passes consume what it yields, and they route the same conditions differently:

| pass | file | what it does with a static filter |
|---|---|---|
| `TranslateQueryFilters` (single dataitem) | `RecordPatches.QueryProjection.cs` | retargets it onto the column's `SourceTableField` and pushes it into the temp provider's WHERE, or into the HAVING list for an aggregated column (#2418) |
| `ApplyJoinRuntimeFilters` (multi-dataitem join) | `RecordPatches.QueryProjection.cs` | evaluates it against the already-projected join row, post-aggregation, so an aggregated column's filter is naturally HAVING-equivalent (#2444) |

A runtime `SetRange`/`SetFilter` on the same column **replaces** the static one rather than
combining with it, which is why both passes skip a column already in
`runtimeFilteredColumnIds`.

<a id="every-exit-here-is-a-failed-read"></a>

## Every exit here is a failed read

Issue #3660, and the sibling of #3647 one method away in the same file. The guard was:

```csharp
_pNCLMetaQueryColumnFilters ??= _tNCLMetaQuery!.GetProperty("ColumnFilters",
    BindingFlags.NonPublic | BindingFlags.Instance);
var raw = _pNCLMetaQueryColumnFilters?.GetValue(metaAppObj) as System.Collections.IEnumerable;
if (raw == null) yield break;
```

Both call sites are `foreach`es that add whatever is yielded, so a `yield break` there is
indistinguishable from "this query declares no `ColumnFilter`" — and the observable of getting
it wrong is a query **returning more rows than it should**, with nothing said.

What makes this one unambiguous, where #3647 had to sort its exits one by one, is the
invariant: on a real `NCLMetaQuery` the property is a `ReadOnlyCollection` that is **empty**
when the query declares no `ColumnFilter`, never null. Verified on `Ncl` 28.4 —
`Microsoft.Dynamics.Nav.Runtime.NCLMetaQuery.ColumnFilters`, internal instance property,
`ReadOnlyCollection`. So none of the three ways to reach that `yield break` is an answer BC
gives, and all three refuse:

| exit | now | why |
|---|---|---|
| the `ColumnFilters` lookup returns null | **refuses** — `property not found` | the member is absent: BC's layout moved |
| the property reads null | **refuses** — `read as null` | BC leaves an empty collection, never a null, so the runner cannot tell a filter-free query from a moved member |
| the value is not `IEnumerable` | **refuses** — `cannot be enumerated`, naming the type it held | present but uninterpretable is the same "layout moved" case (`BcShapeGapException.cs`'s own line) |
| an **empty** `ColumnFilters` collection | **silent** | BC's answer, and the overwhelmingly common case — refusing here would break every ordinary query on every BC version |
| a tuple whose `Item1` is not an `NCLMetaQueryColumn` | **silent** | a runner-side type test on the framework's own `Tuple` members, not a question about BC's layout |

The three refusals are **worded apart on purpose**. A reader who sees `read as null` must not
go looking for a rename that did not happen, and — the reason it is load-bearing rather than
cosmetic — an arm asserting only "something threw" passes on an implementation where one
branch still refuses and the one under test does not. That is measured, not predicted:
reverting the lookup alone still raised a `BcShapeGapException`, from the null branch, and only
the `property not found` / not-`cannot be enumerated` pair distinguished them.

Latent, not live. `ColumnFilters` resolves on every BC version this repository tests; this is a
trap for the next one.

### Why the query type is a parameter

The lookup is against `_tNCLMetaQuery`, a `RecordPatches` static only a loaded `Ncl.dll`
populates. Passing it in is the same idiom #3657 used for
`GetSingleDataItemTableFilterTuples`: real production code driven with fakes standing in for a
BC type whose member moved, with no BC install and without poisoning the statics for the rest
of the test assembly.

The `_pNCLMetaQueryColumnFilters` cache went with it, and had to. A static memoising a
`PropertyInfo` resolved against a type that is now a **parameter** would answer one caller's
type from another's — the same defect #3657 removed alongside its own parameterisation. There
is nothing to reclaim: `Type.GetProperty` is a metadata lookup on a cached runtime type, and it
runs once per query read, not per row.

## Where the coverage is

- `AlRunner.Tests/QueryStaticColumnFilterShapeGapTests` — the three refusals, each asserting
  its own wording **and the absence of the other modes'**, plus the empty-collection negative
  control, the AL-seam arm (`asserterror` and `[TryFunction]` must both fail to absorb it) and
  the signature pin.
- `AlRunner.Tests/ReflectionDrivenHelperLivenessTests` — liveness, from IL. Severing the two
  production call sites while leaving the method declared fails it by name.
- `AlRunner.Tests/QueryJoinColumnFilterProjectionTests` — the live join-path behaviour.
- `docs/query-dataitem-table-filter.md` — the sibling surface, a dataitem's
  `DataItemTableFilter`, and why it is not expressible as a `ColumnFilter`.
