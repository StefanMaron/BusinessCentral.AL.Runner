# The page `<Methods>` subtree

How the runner derives a precompiled dependency page's method table, what it deliberately does
not derive, and the measurements behind both. Issue
[#4267](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/4267).

The codeunit half of the same mechanism is
[`docs/codeunit-metadata-from-bc.md`](codeunit-metadata-from-bc.md) (#3788); this page states only
what differs.

All figures below are from **BC 28.1.49838.53910** — `Microsoft.Dynamics.Nav.Ncl.dll` sha256
`49b11d9b541e82959604b68b4ff6b4990fa06db0ffa48dd5b49284cf63507788`, 18,847,032-byte System
Application R2R chunk — over the two apps
`tests/expectations/metadata-equivalence/apps.json` declares: **Business Foundation** (11 pages)
and **System Application** (224). Re-derive rather than quote: `sha256sum` over the artifact
directory and `tools/gen-metadata-ground-truth.sh` reproduce both sides.

## 107 is not the number of pages that get a subtree

#4267's headline figure is that `SymbolReference.json` carries a non-empty `Methods` array on
**107 of 235** pages. That reproduces exactly. It is also not the population BC emits for:

| | pages |
|---|---:|
| total | 235 |
| state a non-empty `Methods` array | **107** |
| state a method carrying **any** attribute | 13 |
| state an **event-publisher** attribute | **5** |
| **BC emits `<Methods>` for** | **5** |

The 5 are the same five on both sides, by id:

| page | method | attribute |
|---|---|---|
| 2610 Feature Management | `OnOpenFeatureMgtPage` | `IntegrationEvent` |
| 4333 Agent Consumption Overview | `OnGetTotalsVisible` | `IntegrationEvent` |
| 7775 Copilot AI Capabilities | `OnRegisterCopilotCapability` | `IntegrationEvent` |
| 9801 User Subform | `OnPermissionSetNotFound` | `IntegrationEvent` |
| 9995 Word Template Creation Wizard | `OnSetTableNo` | `IntegrationEvent` |

The gap between 107 and 13 is **105 pages stating only ordinary public procedures**, which BC's
`ObjectMetadataEmitter` does not write. The gap between 13 and 5 is **8 pages stating only
`Scope`, `Obsolete` or `NonDebuggable`**, which it does not write either — pages 502, 3712, 8887,
9806, 9810, 9821, 9843, 9848.

So keying the derivation on "the array is non-empty" would manufacture a difference on **102**
pages where BC writes nothing. The filter is `BcAppSymbolCache.EmittedMethodAttributeKinds`,
shared with the codeunit path so one rule has one spelling.

## A page can host an event subscriber, so the witness is not optional

The symbol file is an app's consumer-facing API surface and an AL event subscriber is always
`local`, so the file states publishers exactly and subscribers not at all. That is the #3788
argument, and the open question for pages was whether a page can host one.

It can. Base Application 28.1.49838.53910 ships
`src/Modules/System/EventRecorder/EventRecorder.Page.al`, which declares an `[EventSubscriber]`.
It is the **only** one of that app's 2,772 `.Page.al` / `.PageExt.al` source files that does;
System Application's 228 and Business Foundation's 12 declare none.

One is enough, because `MetadataObjectDiff` pairs `Methods` **positionally**: a subtree that
omits a subscriber does not merely under-report, it puts a different method in BC's slot from the
first omission onward. So the page path is gated on the same assembly witness as the codeunit
path, reading `Page<N>` type names instead of `Codeunit<N>`
(`AlRunner/Patches/RecordPatches.CodeunitSubscriberWitness.cs`).

Two properties of that witness carry over unchanged, and both are third states rather than
answers (`.claude/rules/guards-need-a-third-state.md`):

- a page the scan never **saw** is UNKNOWN, not clear — the System Application chunk carries 220
  `Page<N>` types against 224 pages in its symbol file, so four pages are genuinely unscanned;
- an app with no witness at all is UNKNOWN, not clear.

`PageExtension<N>` types are rejected by the same trailing-digits rule that selects `Page<N>`:
the remainder `Extension<N>` does not parse as an integer.

## `<Methods>` is written last

BC's own document order, read off the five ground-truth documents:

```
Properties, Content, ActionContainers…, ViewContainers, AnalysisViewContainers,
Expressions, Triggers, Methods
```

`Methods` is the final child in all five. `EmitPageXml` writes it after `<Content>`, which is the
last element it writes.

## Parameters are not derived

BC writes a `<Parameters>` child on each `<Method>`, including an empty `<Parameters />`. The
runner writes none, so the metadata-equivalence harness reports three one-directional differences
— `PageMethodDefinition.Parameters.<presence>` and its two aliases — over the 5 emitted methods
and the 5 `<Parameter>` elements they carry between them.

This is the **same** gap as the codeunit side's `MetaMethod.Parameters.<presence>`, tracked on
[#4084](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/4084), and for the same
reason: the symbol file states the parameter list in AL's spelling and BC emits the runtime's,
differing in both fields BC writes. Page 2610's first parameter is

```xml
<Parameter IsVar="True" IsArray="False" Name="featureIDFilter"
           RuntimeAttributes="" RuntimeType="ByRef&lt;NavText&gt;" />
```

against a symbol-file entry naming the AL identifier and an AL `TypeDefinition`. Closing it needs
an AL-type → `RuntimeType` mapping and the parameter-name casing rule, neither of which has been
measured — and a casing rule guessed wrong produces a `<Parameter>` element that looks right and
names something else. #4084 owns both, applies verbatim to a page method because BC's emitter
writes one parameter shape for both object kinds, and **stays open**: #4267's pull request does
not close it.

The runner states no parameter it cannot derive, so nothing manufactures agreement in the
direction that matters.

## Nothing outside BC's own object model reads a page's method table

The element is not AL-observable, and that is measured rather than assumed — it is what makes
#4267's runner PR declare `Corpus-NA:` rather than opening a corpus PR.

A Mono.Cecil scan of **all 495 readable assemblies** in the 28.1.49838.53910 artifact directory
(6 unreadable, native or non-managed) for call sites of
`Microsoft.Dynamics.Nav.Types.Metadata.PageDefinition::get_Methods` finds **22**, and every one
of them is inside `Microsoft.Dynamics.Nav.Types.dll` itself — the object model's own `.ctor`,
`Freeze`, `WithMergedMultiLanguage` and equality plumbing. No service assembly, no runtime
engine, nothing else.

The control matters, because a zero — or a count confined to one assembly — is exactly what a
scan pointed at the wrong subject also returns. Two run in the same invocation:

| symbol | call sites | outside `…Nav.Types.dll` |
|---|---:|---|
| `PageDefinition::get_Methods` | 22 | **none** |
| `PageDefinition::get_Properties` (control) | 38 | yes — `Microsoft.Dynamics.Nav.Service.dll`, `NsDataAccess::GetResourceDefinedFormTable` and `NSErrorActionInvocation::GetPageToOpen` |

A first attempt scanned `Microsoft.Dynamics.Nav.Ncl.dll` alone and answered 0 for every needle,
which read like a finding and was not: `PageDefinition` is defined in
`Microsoft.Dynamics.Nav.Types.dll` and Ncl.dll does not reference it at all.

The second, independent route to the same answer: the **Page Metadata** virtual table
(2000000138) exposes no method column — `RecordPatches.PageMetadataVirtualTable.cs` answers `id`,
`name`, `caption`, `editable`, `pagetype`, `sourcetable`, `cardpageid`, the Insert/Modify/Delete
trio, `sourcetabletemporary`, `sourcetableview`, `delayedinsert`, `showfilter`,
`multiplenewlines`, `savevalues`, `autosplitkey`, `datacaptionfields`, `linksallowed`,
`populateallfields` and nothing about methods. So there is no AL statement that reads a page's
method table either directly or through a virtual table.

## What pins this

| claim | pinned by |
|---|---|
| the emitted set, the filter, the two abstention states | `AlRunner.Tests/DependencyPageMethodSubtreeTests.cs` |
| the page and codeunit renderers produce one element shape | `AlRunner.Tests/DependencyPageMethodSubtreeRenderingParityTests.cs` |
| the runner agrees with BC's own emitter over both apps | `AlRunner.Tests/MetadataEquivalenceHarnessTests.cs`, against `tools/gen-metadata-ground-truth.sh` output |
