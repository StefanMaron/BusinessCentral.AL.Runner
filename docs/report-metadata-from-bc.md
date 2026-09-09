# Answering the report virtual tables from BC's own document

`Report Metadata` (2000000139) and `Report Data Items` (2000000203) used to be derived from AL
source text by `RecordPatches.AlReportParser`, while BC's own emitted metadata document for the
same reports sat in `AlReportMetadataRegistry` feeding the report *execution* path. The two
disagreed, and a caller could observe the difference. `RecordPatches.ReportRowFromBcDocument`
overlays the document onto the derived row; this page is the derivation its header points at.

The sibling page for tables is [`object-metadata-from-bc.md`](object-metadata-from-bc.md), which
`RecordPatches.NclMetaTableFromBcDocument` did first at #3584.

<a id="the-fixture"></a>

## The fixture the values below come from

`AlRunner.Tests/ReportMetadataDocumentColumnTests.cs` compiles report **90311**
`"RptMetaDoc Fixture"` over table 90310, and reads the document back out of the registry:

```al
report 90311 "RptMetaDoc Fixture"
{
    UsageCategory = None;
    ProcessingOnly = false;
    WordMergeDataItem = Src;

    dataset
    {
        dataitem(Src; "RptMetaDoc Sample")
        {
            DataItemTableView = sorting("Entry No.") order(descending);
            column(EntryNo; "Entry No.") { }

            dataitem(Child; "RptMetaDoc Sample")
            {
                DataItemLink = "Entry No." = field("Entry No.");
                column(ChildEntryNo; "Entry No.") { }
            }
        }
    }
}
```

<a id="what-differs"></a>

## Three columns where the document and the AL text disagree

Each was RED before the change with a value that is **wrong**, not merely absent — which is what
lets the test tell a fix from a no-op.

| column | AL-derived answer | BC's document |
|---|---|---|
| `WordMergeDataItem` | `""` — the property was never read | `Src` |
| `Data Item Table View` | the raw source text, `sorting("Entry No.") order(descending)` | BC's normal form, `SORTING(<field no>) ORDER(<n>)` |
| `Data Item ID` | a 1-based ordinal, `1`, `2` | BC's assigned id, a large non-sequential integer |

**The data-item id is load-bearing, not cosmetic.** Ncl's
`ReportDataItemsDataProvider.GetReportDataItems` keys, sorts and range-filters rows on
`MetaDataItem.Id`. So a caller that reads an id out of one row and filters this table by it got
nothing back while the ordinal stood in for it.

The `DataItemTableView` assertion is written as *shape*, not as a literal: the test requires
`SORTING(` and `ORDER(` and refuses `sorting(` and `descending`. A field **number** appears where
the source names a field, so pinning the exact string would pin this repo's fixture field
numbering rather than BC's normal form. The data-item id is asserted the same way — two distinct
ids, neither of them `1` or `2` — because the value BC assigns is not stable across compilations
and a literal would be a fixture detail masquerading as a claim about BC.

<a id="why-the-xml"></a>

## Why the XML, and not the `MetaReport` the execution path already builds

This is written down because it was the first implementation and it is a trap.

`NavReportSync.GetRealMetaReport` forces `MetaReport.RequestFormMetadata` as a deliberate side
effect, so a failure is contained at construction — it builds the report's whole **request page**.
Populating a virtual table enumerates **every** known report, so reading these columns that way
turned one `Report Metadata` read into 28 request-page builds on the al-language corpus and blew
the 60s per-test watchdog on the first assertion.

Every property above is stated directly by the document, so reading the XML costs one parse and
no page construction — measured at **65 ms** for the same read. The same arithmetic is why
dependency documents are not consulted here: 659 Base Application reports would each cost a
per-app symbol walk plus an AL source read.

<a id="what-this-does-not-claim"></a>

## What this does not claim

Ncl's provider fills 29 `Report Metadata` columns off `NCLMetaReport`. This fills the ones the
document states directly and leaves the rest on BC's own `GetDefaultNavValue`, exactly as before.
A report with no registered document — one from a precompiled dependency — keeps the row it had.
Nothing here answers a column with a guess.

Two things were deliberately left alone, each for a reason worth not rediscovering:

- **`ProcessingOnly = true` at `NavReportSync.cs:148`** is not this table's answer. RED-baselined:
  the virtual table already answered `false` correctly from the AL parser. That hardcode is on the
  legacy stub-`MetaReport` execution path and carries its own note recording that flipping it was
  tried and reverted.
- **`Report Layout List` (2000000234)** needed no conversion — it already reads the compiler's
  captured `rendering` block.

`AlReportParser.cs` loses nothing at #3607: it remains the fallback, and at that point was still
the only source of `Caption` and `RequestFilterFields`. #3607 expected a line-count drop there;
deleting any of it would have been a regression. #3620 then took `RequestFilterFields` off it —
see the next section for why that one field was not a fallback but a wrong answer.

<a id="sorting-and-request-filter-fields"></a>

## `Sorting Fields` and `Request Filter Fields` — already resolved in the document

#3607 deferred these two columns to **#3620** on the grounds that they are a different
mechanism: Ncl resolves both through a table's field numbering rather than reading them off
the row. That reading of `ReportDataItemsDataProvider` is correct —

```csharp
string dataColumnName = item.Key.DataItemViewName;
MetaFilterControlDefinition metaFilterControlDefinition =
    filterControls.SingleOrDefault(x => x.DataColumnName == dataColumnName);
NCLMetaTable metaTableById = base.NclMetadata.GetMetaTableById(key.DataItemTable, requireCompiled: false);
string sortingFieldsIfAny      = GetSortingFieldsIfAny(session, metaTableById, key.DataItemTableView);
string requestFilterFieldsIfAny = GetRequestFilterFieldsIfAny(metaTableById, metaFilterControlDefinition?.ReqFilterFields);
```

— and the conclusion drawn from it was wrong. **The resolution has already happened at compile
time**, and the runner can reach the identical numbers by reading the document.

### The measurement

Same fixture as above, extended with a data item that declares both properties against a
**non-primary** key and fields that are neither field 1 nor in field order, so neither "the
first field" nor "the primary key" is an accidentally-right answer:

```al
dataitem(Filtered; "RptMetaDoc Sample")          // fields 1 "Entry No.", 2 Description, 5 "Alt Code"
{                                                 // keys: PK ("Entry No."), Alt ("Alt Code", Description)
    DataItemTableView = sorting("Alt Code", Description);
    RequestFilterFields = Description, "Alt Code";
}
```

BC 28.1 emits, for that data item:

```xml
<DataItem>
  <DataItemTableView>SORTING(Field5,Field2)</DataItemTableView>
  <DataItemViewName>Report90311DataItem2TableView</DataItemViewName>
</DataItem>
...
<RequestPage><PageDefinition><Content><Containers>
  <Controls xsi:type="ControlGroupDefinition">
    <Controls xsi:type="FilterControlDefinition"
              DataColumnName="Report90311DataItem2TableView"
              FilterTableID="90310" Sorting="1" ReqFilterFields="Field2,Field5" />
```

Both are field numbers in the `Field<N>` token form, and the two orders differ from each other:
`ReqFilterFields` keeps AL declaration order (2 then 5), `SORTING` keeps key order (5 then 2).
A value copied from either column into the other is therefore detectably wrong.

### Why reading them is BC's own rule, not an approximation of it

`NCLMetaTable.FindFieldMatch` — which `GetRequestFilterFieldsIfAny` calls directly and
`GetSortingFieldsIfAny` reaches through `TableViewResolver.AddSortingField` → `ResolveField` —
opens with:

```csharp
string s = fieldIdentifier.StartsWith("Field", StringComparison.OrdinalIgnoreCase)
    ? fieldIdentifier.Substring(5)
    : (fieldIdentifier.StartsWith("#") ? fieldIdentifier.Substring(1) : fieldIdentifier);
if (int.TryParse(s, out var result))
    return GetFieldByNo(result, trapError);
```

Every token BC's emitter writes into either place is in that form, so the name-lookup and
prefix-fallback branches below it are never reached from this caller. `RecordPatches`'
`FieldNumbersFrom` applies exactly that rule and nothing else: strip `Field` or `#`, parse,
drop what does not parse — which is also what BC does with an unresolvable one, since
`GetRequestFilterFieldsIfAny` skips a null `FindFieldMatch` result and `AddSortingField` traces
and skips.

So there is no `NCLMetaTable` lookup on this path and — the point that mattered — **no request
page is built**. The filter control is reached by walking the document's own XML and pairing on
`DataColumnName`, which is the same key Ncl pairs on.

### The dependency path answers `Request Filter Fields` too

A precompiled dependency report has no emitted document, so it keeps its
`SymbolReference.json`-derived row. That row can answer this column as well: measured on BC
28.1's Base Application, **all 633 `RequestFilterFields` values across its 659 reports are
`Field<N>` lists** (the other 1294 data items state the property not at all). The same
`FieldNumbersFrom` therefore lands on BC's answer for every one of them.

`Sorting Fields` does **not** follow from the same rule, and needed its own mechanism —
[below](#sorting-fields-on-the-dependency-path).

<a id="sorting-fields-on-the-dependency-path"></a>

### `Sorting Fields` on the dependency path (#3627)

A symbol file's `DataItemTableView` is the AL *source text*, not the compiled form. Measured on
BC 28.1's Base Application `SymbolReference.json`: its **659 reports carry 1927 data items, 1795
of which state a `DataItemTableView` and 1759 of those a `SORTING` clause — 3201 sorting tokens
in total, 988 bare identifiers and 2213 double-quoted names, and none in `Field<N>` form.** So
`FieldNumbersFrom`, which drops every token that is not `Field<N>`, answered empty for all 1759.

Two things had to be different from the document path:

| | document (`SortingClauseOf`) | AL text (`AlSortingClauseOf`) |
|---|---|---|
| keyword | `SORTING(`, matched `Ordinal` | `sorting(`, matched case-insensitively |
| clause end | the first `)` — a field token cannot contain one | matched by depth, ignoring AL quotes: `sorting("Amount (LCY)")` is a legal name |

Pointing the document's parser at AL text answers empty for every one of the 1759, which is the
symptom the issue records. The two are kept as separate methods rather than one widened one: on
the document path a case-insensitive match would also accept a `WHERE` filter value that happens
to spell `sorting(`, and the first-`)` rule there is a stated property of the input.

#### The resolution order is BC's

A sorting token reaches `TableViewResolver.AddSortingField`, which calls
`TableFilterResolver.ResolveField(field, trapError: true)` —
`NCLMetaTable.FindFieldMatch(field, prefixFallback: true, trapError: true)`. Decompiled from BC
28.1, that method is, in order:

1. strip a leading `Field` or `#`; if the remainder parses as an integer, `GetFieldByNo`;
2. else `TryGetFieldByName`, then `TryGetFieldByCaption`;
3. else — `prefixFallback` is `true` on this path — the first field whose *name*, then whose
   *caption*, starts with the token;
4. else `null`, which `AddSortingField` traces and **skips**, keeping the rest of the clause.

`BuildSortFieldIndex` / `ResolveSortFieldNo` are that list. Step 1 is why a view already in BC's
normal form still resolves here without a lookup, so the #3620 path does not regress; step 4 is
why one unresolvable token does not discard the fields around it.

`BuildSortFieldIndex` reads `GetAllFieldsIncludingExtensions`, not `ParsedTable.Fields` — the
same rule `TryResolveDependencyFieldId`, the page-control field map and the AL page parser each
state at their own call site (#2490). Base Application 28.1 has **0 of its 3201 sorting tokens**
resolving only through a tableextension, so this is unmeasurable there and would have shipped
answering a silently *shorter* sort key for the first ISV app whose report sorts by an extension
field. `AlRunner.Tests/DependencyReportSortingFieldsTests.SortingToken_NamingATableExtensionField_Resolves`
pins it.

#### Where the cost is paid

The cost is the whole reason the column was left empty, so: the lookup is paid **once per run**,
not per population. `EnumerateKnownReports` caches its result per (registration epoch, parsed
report count), so every population after the first hands back the same list; within one build the
name index is memoized per *table*, so 1927 data items cost one index per distinct data-item
table; and each index is built off the `ParsedTable` that `ResolveTableIdByName` has already
faulted into `_parsedTables` one line above, so it costs no symbol read and constructs no
`NCLMetaTable`.

Measured end to end on the real Base Application — `tests/runner-extras/standalone-suites`
filtered to codeunit 61952, which loads all 660 dependency reports, five warm reps per arm on one
box, BC 28.1.49838.53910:

| | test-run phase | codeunit 61952 |
|---|---|---|
| before | 2.6 / 2.7 / 2.7 / 2.5 / 2.8 s — mean **2.66 s** | 6 of 9 passing |
| after | 2.7 / 2.5 / 2.7 / 2.8 / 2.6 s — mean **2.66 s** | 9 of 9 passing |

No measurable cost. The contrast is with the first attempt at #3607, which reached for the
request-page-building metadata synthesizer *per object* and blew the 60s per-test watchdog; that
is a per-population cost, and this is not one.

### What the AL parser lost

`ParsedReportDataItem.RequestFilterFields` is gone. The column reports numbers and the AL text
states names, so what the parser produced was not a partial answer that a better one improved
on — it was a value of the wrong kind, and a caller reading `Description` out of a column whose
contract is `2` had no way to tell. Nothing else read the field.
