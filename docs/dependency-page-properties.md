# Page properties the runner reconstructs from `SymbolReference.json`

`RecordPatches.EmitPageXml` rebuilds a runtime `PageDefinition` document for a page that ships
compiled inside a dependency `.app` (`AlRunner/Patches/DependencyPageMetadataXml.cs`). This file
records what BC's own metadata emitter writes for each page property, measured against the
ground truth, so the emitter's per-property rule can be checked rather than assumed.

**Every figure here is one BC build: `28.1.49838.53910`**, over the **235** `PageDefinition`
documents of Business Foundation (11) + System Application (224). Ground truth:
`tools/gen-metadata-ground-truth.sh`, which writes
`~/.local/share/al-runner/metadata-ground-truth/<build>/`. The symbol side is the
`SymbolReference.json` inside each `.app` — note that pages live in the **`Namespaces`** tree,
not in a top-level `Pages` array, which is empty.

## The cross-tabulation

"symbol" is the number of pages whose `SymbolReference.json` entry states the property; "BC" is
the number of pages BC's emitter writes the corresponding attribute or element on. **They are
different questions, and the second is the specification.**

| property | symbol | BC | BC's rule | status |
|---|---|---|---|---|
| `Properties/@HelpLink` | 6 | **235** | derived: stated `HelpLink`, else base + `ContextSensitiveHelpPage`, else base | **implemented** |
| `@CaptionML` (root) | 196 | 196 | write-iff-stated, `ENU=` + text | **implemented** |
| `Properties/@DataCaptionExpr` | 32 | 32 | write-iff-stated, constant `DataCaptionExprCode` | **implemented** |
| `Properties/@AnalysisModeEnabled` | 1 | 94 | derived, 93 of 94 from PageType; one unexplained | left, see below |
| `Properties/@CardFormID` | 10 | 10 | write-iff-stated, but as a resolved page **id** | left, see below |
| `SourceObject/@IndirectPermissions` | **0** | 84 | derived from `Permissions`; not a read at all | left, see below |
| `SourceObject/@ModifyAllowed` | 90 | 90 | write-iff-stated | already emitted (#2860) |
| `SourceObject/@DelayedInsert` | 15 | 15 | write-iff-stated | already emitted (#2860) |
| `SourceObject/@ShowFilter` | 26 | 26 | write-iff-stated | already emitted (#2860) |
| `Properties/@AboutTitleML` / `@AboutTextML` | 21 / 21 | 21 / 21 | write-iff-stated, `ENU=` + text | already emitted (#3784) |
| `ActionContainers` (element) | 106 state `Actions` | **235** | every page gets one | left, see below |
| `ViewContainers` (element) | 4 state `Views` | **91** | not a plain read | left |

### HelpLink

A three-way partition, and the one `<Properties>` scalar BC derives rather than copies, and a total rule with no
exceptions on this build:

| the page states | count | BC writes |
|---|---|---|
| `HelpLink` | 6 | that value, verbatim |
| `ContextSensitiveHelpPage` (relative) | 36 | `https://learn.microsoft.com/dynamics365/business-central/` + it |
| neither | 193 | the bare base URL |

6 + 36 + 193 = 235, and each arm matched BC's exact string on every page it covers. The obvious
rule — write it only for the 6 that state it, as `UsageCategory` beside it is written — is
wrong for **229** pages.

### `DataCaptionExpr` — write-iff-stated with a constant

32 pages state `DataCaptionExpression` and BC writes the attribute on exactly those 32. The
value is the literal `DataCaptionExprCode` on all 32, never the AL expression: the expression
compiles into the page's own IL, and this attribute only records that the page has one. Writing
the AL text would be a different wrong answer rather than the missing one.

### `CaptionML` — the right member, in the wrong place

196 pages state `Caption`; BC writes `CaptionML` on exactly those 196, as a **root attribute** in
the `ENU=<text>` MultiLanguage form, and `"ENU=" + stated` equals BC's value on all 196.

The runner already wrote a `<CaptionML>` child *element* inside `<Properties>`, which
`PageDefinition(XmlNode)` does not read — its `CaptionMLString` came back `<empty>` for all 196.
The element is left in place: nothing here established what else may read it, and it contributes
nothing to the parsed member either way.

## What is deliberately not implemented, and why

- **`AnalysisModeEnabled` (1 stated, 94 written).** BC writes it for every page whose emitted
  `PageType` is `List` or `Worksheet` — 93 of 93, with the value `1` unless the page states
  otherwise (page 8350 states `0` and BC writes `0`). The 94th is page **1998 "Guided Experience
  Item Cleanup"**, whose emitted `PageType` is `Card` and which BC still gives
  `AnalysisModeEnabled="1"`. That page is also the only one of the 235 stating no `PageType` at
  all. One unexplained page out of 94 is not a rule, and shipping the PageType rule would
  manufacture a *missing* attribute on 1998.
- **`CardFormID` (10 stated, 10 written).** The sets match exactly, but the symbol file states a
  page **name** (`Retention Policy Setup Card`) and BC writes the resolved page **id** (`3901`).
  That is a cross-object name lookup, a different mechanism from every property above. Note also
  that the symbol file spells the property two ways — `CardPageId` on 9 pages and `CardPageID` on
  1 — which sum to BC's 10; a reader matching one spelling finds 9 and looks correct.
- **`IndirectPermissions` (0 stated, 84 written).** The symbol file does not state this property
  anywhere. 86 pages state `Permissions`, which BC resolves into the `SourceObject` vector, so
  this is a derivation whose rule is unmeasured here — not the plain read the issue describes.
- **`ActionContainers` / `ViewContainers`.** BC writes `ActionContainers` on all 235 pages
  (1,153 elements) while only 106 state an `Actions` array, and `ViewContainers` on 91 while 4
  state `Views`. Both are subtrees rather than scalars and neither is a plain read.

## Reproducing

```bash
tools/gen-metadata-ground-truth.sh        # once per BC build
dotnet test AlRunner.Tests/AlRunner.Tests.csproj -c Release --no-build \
  --settings engine.runsettings --filter "FullyQualifiedName~MetadataEquivalenceHarnessTests"
```

`Every_difference_is_declared_with_a_reason` prints each undeclared difference with its count
and one example, which is how the "BC" column above was read back independently of the symbol
files.
