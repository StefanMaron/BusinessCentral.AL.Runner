# Page properties the runner reconstructs from `SymbolReference.json`

`RecordPatches.EmitPageXml` rebuilds a runtime `PageDefinition` document for a page that ships
compiled inside a dependency `.app` (`AlRunner/Patches/DependencyPageMetadataXml.cs`). This file
records what BC's own metadata emitter writes for each page property, measured against the
ground truth, so the emitter's per-property rule can be checked rather than assumed.

**Unless a section names more builds, every figure here is one BC build: `28.1.49838.53910`**, over the **235** `PageDefinition`
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
| `Properties/@HelpLink` | 6 | **235** | derived: stated `HelpLink`, else manifest URL + `ContextSensitiveHelpPage`, else manifest URL, else none | **implemented** |
| `@CaptionML` (root) | 196 | 196 | write-iff-stated, `ENU=` + text | **implemented** |
| `Properties/@DataCaptionExpr` | 32 | 32 | write-iff-stated, constant `DataCaptionExprCode` | **implemented** |
| `Properties/@AnalysisModeEnabled` | 1 | 94 | derived: stated value, else `1` for List/Worksheet or no stated PageType | **implemented** |
| `Properties/@CardFormID` | 10 | 10 | write-iff-stated, as the resolved page **id** | **implemented** |
| `SourceObject/@IndirectPermissions` | **0** | 84 | derived from `Permissions`, only with a `SourceTable` | **implemented** |
| `SourceObject/@ModifyAllowed` | 90 | 90 | write-iff-stated | already emitted (#2860) |
| `SourceObject/@DelayedInsert` | 15 | 15 | write-iff-stated | already emitted (#2860) |
| `SourceObject/@ShowFilter` | 26 | 26 | write-iff-stated | already emitted (#2860) |
| `Properties/@AboutTitleML` / `@AboutTextML` | 21 / 21 | 21 / 21 | write-iff-stated, `ENU=` + text | already emitted (#3784) |
| `ActionContainers` (element) | 106 state `Actions` | **235** | every page gets one | left, see below |
| `ViewContainers` (element) | 4 state `Views` | **91** | not a plain read | left |

### HelpLink

The one `<Properties>` scalar BC derives rather than copies. The base is the declaring app's
manifest `ContextSensitiveHelpUrl`, not a fixed URL (#4675). Business Foundation and System
Application both state `https://learn.microsoft.com/dynamics365/business-central/`:

| the page states | count | BC writes |
|---|---|---|
| `HelpLink` | 6 | that value, verbatim |
| `ContextSensitiveHelpPage` (relative) | 36 | the manifest URL + it, no separator |
| neither | 193 | the manifest URL alone |

6 + 36 + 193 = 235 on 28.1.49838.53910, and each arm matched BC's exact string on every page it
covers. The obvious rule — write it only for the 6 that state it, as `UsageCategory` beside it is
written — is wrong for **229** pages.

When the manifest states **no** `ContextSensitiveHelpUrl`, BC writes no `HelpLink` unless the page
states one, even when `ContextSensitiveHelpPage` is set. This is Base Application's case on every
build from 27.0 to 28.5. The rule is `PageMetadataEmitHelper.GetContextSensitiveHelpUrl` in
Microsoft.Dynamics.Nav.CodeAnalysis.dll, which the page, request-page and query emitters all call;
the runner's copy is `RecordPatches.DeriveHelpLink`. Corpus codeunit 67250 measures the no-URL arm
through a Base Application report's `Report.SaveAs(Xml)` dataset, whose
`BCReportInformation/ReportMetadata/ReportHelpLink` is the request page's `HelpLink`.

A query the runner reads from the bundle's own loose `SymbolReference.json` has no `.app` and so
no manifest. Its URL is the bundle's `app.json` `contextSensitiveHelpUrl`, which every site that
registers the file passes with it, including the AL-output cache HIT paths, which read it from the
current `app.json` rather than from the cache (#4744).

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

**Two pages state a blank caption and it is load-bearing.** 1433 "Satisfaction Survey" and 9260
"Customer Experience Survey" state `Caption` as a single space, and BC writes `CaptionML="ENU= "`
for both. So the emit guard is `IsNullOrEmpty` rather than `IsNullOrWhiteSpace`, and `Caption`
deliberately skips the `OrNullIfBlank` filter its neighbours use — either change drops the
attribute on those two and states no caption where BC states a blank one.

They are also why a mutation dropping the `ENU=` prefix reds **194** rather than 196: `" "` and
`"ENU= "` both render as an empty `ENU=`, so those two agree under that mutation alone. A
mutation that corrupts the *value* instead (`"ENU=Z" + Caption`) reds all **196**, which is the
measurement establishing that the whole population is pinned.

### No PageType

A page whose AL states no `PageType` is **not** the same document as one stating `Card`, though
BC emits `PageType="Card"` for both. For the unstated one BC's compiler also writes
`AnalysisModeEnabled="1"`, `IsPreview="0"`, `APIVersion="beta"` and `DataAccessIntent="ReadWrite"`
— with or without a source table. Page 1998 "Guided Experience Item Cleanup" is the only such
page in the 235 (its AL declares no `PageType`; it is not an API page), which is why it looked like
an outlier. Settled by compiling a probe app through `tools/metadata-ground-truth` with BC's own
compiler at 28.1.49838.53910: pages 50101 (no PageType, source table) and 50109 (no PageType, no
source table) both carry all four; a stated `PageType = Card` page carries none of them.

The runner writes the first two, because the equivalence harness reads them (a missing
`IsPreview="0"` was a declared unobservable omission). It does not write `APIVersion` or
`DataAccessIntent`, which the harness does not compare for pages; neither was measured to matter.

`BcAppSymbolCache.PageSymbol.PageType` folds absence into `"Card"`, so the emitter keys on
`PageTypeStated`.

### AnalysisModeEnabled

The stated value when the page states one (`"0"` on page 8350); otherwise `"1"` for a `List` or
`Worksheet` page or a page stating no `PageType`; otherwise nothing. Reproduces BC's attribute on
every page of four bundles — 28.1.49838.53910 (94 of 235 written), 28.1.49838.54044 (94/235),
27.5.46862.53931 (86/223), 28.4.53241.53989 (95/236) — with zero disagreements. The probe adds the
arms the shipped apps do not show: a stated `AnalysisModeEnabled = false` on a List gives `"0"`,
`ListPart` and `API` pages get none, and AL refuses the property on anything but List/Worksheet
(`AL0167`).

### CardFormID

The symbol file states a page **name** (`Retention Policy Setup Card`), spelled `CardPageId` on 9
pages and `CardPageID` on 1; BC writes the resolved **id** (`3901`). Resolved through the same
page-name index the Page Metadata virtual table uses. All 10 resolve to BC's id on each of the four
bundles above. The probe shows BC also writes it for a numeric `CardPageId` and on a `Document`
page, so the emitter accepts both forms and does not key on PageType. An unresolvable name is
omitted with a stderr line.

### IndirectPermissions

BC compiles the page's AL `Permissions` into `SourceObject/@IndirectPermissions`: the table id and
a mask per entry, in declared order, then `0, 0` —
`tabledata "Sent Email" = rd, tabledata "Email Message Attachment" = r` becomes
`8889, 288, 8904, 32, 0, 0`. The mask is the **indirect** bits, `r=32 i=64 m=128 d=256`, and the
letter's case does not matter: the probe's `RIMD` gives 480 and `Rm` gives 160. That is the
opposite of the inherent-permission masks, where case decides direct against indirect, so the two
must not share a decoder.

Written only for a page with a `SourceTable`: 86 pages state `Permissions` and BC writes 84; the
other two (2718, 7775) have no source table, and the probe's page 50108 confirms it. The rule
reproduces BC's exact string on every page of the four bundles (84, 84, 82, 84 written). AL accepts
only `tabledata` entries on a page (`AL0104` for `codeunit`), and the table names resolve through
`ResolveTableIdByName`. An entry the runner cannot parse or resolve withdraws the whole attribute,
with a stderr line.

<a id="part-controls"></a>
### Subpage parts (`InfopartPageDefinition`)

BC writes `ApplicationArea`, `Editable`, `Enabled`, `ShowFilter` and `Visible` on **every** part,
whatever the AL states, and `AboutTitleML`/`AboutTextML` when stated. `EmitPartControlXml`'s rule:

| attribute | written |
|---|---|
| `ApplicationArea` | the part's own, else the **host page's**, else nothing |
| `Editable` / `Enabled` / `Visible` | stated value verbatim, else `true` |
| `ShowFilter` | stated value verbatim (the symbol file states `0`/`1`), else `1` |
| `AboutTitleML` / `AboutTextML` / `CaptionML` | iff stated, through BC's own `ToMultiLanguageString` |

Cross-tabulated over every part of Business Foundation + System Application — 53 parts on
`27.5.46862.53931`, 55 on each of `28.1.49838.53910`, `28.1.49838.54308` and `28.4.53241.54407`:
**zero** disagreements on any attribute whose stated value is a literal or absent. A stated
*expression* (`Visible = IsTenant`, `Editable = not Rec.Active`) is passed through as AL text, which
BC rewrites into its compiled notation (`p9855p9855IsTenant`, `not Active`) — the boundary
`InfopartPageDefinition.Visible` and `.Editable` declare in the allowlist.

Every part in those apps states an area or sits on a page that does, so the "else nothing" arm and
the nesting arms were settled by a compiled probe (the recipe under *Reproducing*): a part on a page
stating no `ApplicationArea` gets none; a part inside a `group`, and one in `area(FactBoxes)`, both
inherit the page's; a stated area wins over the page's.

**The quoting.** BC's `MultiLanguage.Parse` splits an unquoted value at `;` and treats a leading
`"` as the start of a quoted one, so the probe's `AboutTitle = 'Title; with semicolon'` is written
`ENU="Title; with semicolon"`. `MultiLanguageExtensions.ToMultiLanguageString` (Types.dll) is the
serializer that does that — quoting on `;`, `=` or `"`, doubling `"` — so every `ENU=` attribute in
`EmitPageXml` goes through it rather than concatenating. Page 4312's "Agent Available Tools"
`AboutText` is the instance in the bundles.

Nothing at runtime reads a part's `ApplicationArea` here: BC's license/area filter over the page is
switched off by `MetadataProviderElementRemoval`, so this is document equivalence only.

## What is deliberately not implemented, and why

- **`ActionContainers` / `ViewContainers`.** BC writes `ActionContainers` on all 235 pages
  (1,153 elements) while only 106 state an `Actions` array, and `ViewContainers` on 91 while 4
  state `Views`. Both are subtrees rather than scalars and neither is a plain read.

## Reproducing

```bash
tools/gen-metadata-ground-truth.sh        # once per BC build
dotnet test AlRunner.Tests/AlRunner.Tests.csproj -c Release --no-build \
  --settings engine.runsettings --filter "FullyQualifiedName~MetadataEquivalenceHarnessTests"
```

The "probe" cited above is a hand-built `.app` — a zip holding a `NavxManifest.xml` (no
dependencies, so only its own table and the platform's system tables resolve) and one `src/Probe.al` declaring a
table and eleven pages, one per arm — run through
`dotnet tools/metadata-ground-truth/bin/Release/net8.0/metadata-ground-truth.dll --app <probe.app> --out <dir> --artifacts <BC build>`.
The tool compiles the source with BC's own compiler and writes the emitted `PageDefinition`
documents, which is the same thing it does for Microsoft's apps.

`Every_difference_is_declared_with_a_reason` prints each undeclared difference with its count
and one example, which is how the "BC" column above was read back independently of the symbol
files.
