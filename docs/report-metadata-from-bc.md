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

Three things were deliberately left alone, each for a reason worth not rediscovering:

- **`Sorting Fields` and `Request Filter Fields`** resolve through a table's `NCLMetaField`
  numbering, and `ReqFilterFields` is stated on the request page's *filter control* rather than
  on the data item at all. That is a different mechanism; **#3620** tracks it.
- **`ProcessingOnly = true` at `NavReportSync.cs:148`** is not this table's answer. RED-baselined:
  the virtual table already answered `false` correctly from the AL parser. That hardcode is on the
  legacy stub-`MetaReport` execution path and carries its own note recording that flipping it was
  tried and reverted.
- **`Report Layout List` (2000000234)** needed no conversion — it already reads the compiler's
  captured `rendering` block.

`AlReportParser.cs` loses nothing either: it remains the fallback, and the only source of
`Caption` and `RequestFilterFields`. #3607 expected a line-count drop there; deleting any of it
would have been a regression.
