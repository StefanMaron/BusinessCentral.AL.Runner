# Answering Page Control Field from BC's own document

`Page Control Field` (2000000192) used to be derived entirely from AL source text by
`RecordPatches.AlPageParser`, while BC's own emitted page document — the one BC's real
provider reads — sat in `AlObjectMetadataRegistry` feeding the page *execution* path.
The two disagreed on four columns, and a caller could observe every one of them.
`RecordPatches.PageControlFieldFromBcDocument` reads the document; this page is the
derivation its header points at.

The siblings are [`object-metadata-from-bc.md`](object-metadata-from-bc.md), which
`RecordPatches.NclMetaTableFromBcDocument` did first for tables at #3584, and
[`report-metadata-from-bc.md`](report-metadata-from-bc.md), which did reports at #3607.
The corpus adjudication for this one is
[corpus PR #300](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/300)
(page 60427, codeunit 60426).

<a id="what-bc-does"></a>

## What BC does

`Microsoft.Dynamics.Nav.Runtime.PageControlFieldDataProvider.GetControlsOnPage`, decompiled
from `Microsoft.Dynamics.Nav.Ncl.dll` 28.1, is the whole specification. The relevant part:

```csharp
page     = MetadataProvider.GetMasterPageForDesigner(pageNo, out metaTable);
tableNo  = page.PageProperties.SourceObject.SourceTable;

var enumerable = page.FindAll(ed => ed is ControlDefinition)
                     .Cast<ControlDefinition>()
                     .Select((cd, i) => (sequence: i, control: cd));
var val = enumerable.OrderBy(c => c.control.ID);        // Ascending; OrderByDescending otherwise

foreach (var pageControl in val) {
    string value = string.Empty, value2 = string.Empty;
    if (pageControl.control.IsBoundToTableField(out var fieldNo)) {
        if (tableNo > 0) {
            metaTable ??= MetadataProvider.GetTableMetadata(tableNo);
            var f = metaTable.GetFieldsById(fieldNo);
            if (f != null) {
                value  = (f.Type == NavType.Option) ? f.OptionString : string.Empty;
                value2 = f.Name;
            }
        }
    } else {
        var dfd = page.Expressions.FirstOrDefault(x => x.Name == pageControl.control.DataColumnName);
        if (dfd != null) {
            value2 = dfd.SourceExpression;
            if (dfd.Datatype == DataType.Option) value = dfd.OptionString;
        }
    }
    array[4]  = NavInteger.Create(tableNo);              // TableNo — outside both branches
    array[5]  = NavInteger.Create(fieldNo);              // FieldNo — the TryParse out
    array[6]  = ... pageControl.control.Enabled;
    array[7]  = ... pageControl.control.Editable;
    array[8]  = ... pageControl.control.Visible;
    array[9]  = ... value2;                              // SourceExpression
    array[10] = ... value;                               // OptionString
    array[11] = NavInteger.Create(pageControl.sequence);
}
```

Two things in there are easy to read past and both are load-bearing:

* **`array[4]` sits outside the bound/unbound branch**, so `TableNo` is the *page's* source
  table on every row — including a row whose control has no source field at all.
* **`sequence` is numbered on the `FindAll` walk, before the `OrderBy`**, so it is the
  document's own document order, not the sorted position.

<a id="the-four-wrong-answers"></a>

## The four wrong answers this removes

Measured on the probe bundle in the next section, against BC 28.1, and pinned by corpus
codeunit 60426 against a real service tier:

| column | AL derivation answered | BC answers |
|---|---|---|
| `SourceExpression` (bound) | `Rec."Entry No."` — the binding *text* | `Entry No.` — the source field's **name** |
| a variable-bound control | **no row at all** | a row whose `SourceExpression` is `LocalTreeVar` |
| `OptionString` (Option-bound) | `''` — fell through to `GetDefaultNavValue` | `" ",Draft,Active,Closed` |
| `SourceExpression` (Text-bound) | `Rec."Description Field"` | `Description Field` |

The missing-row defect had a named cause: the AL derivation kept only a control whose source
expression was exactly `Rec.Something`, and omitted everything else rather than guessing at
it. That was a defensible rule for a text-parsing derivation and it is simply not what the
table is. BC applies **no** binding filter — `ed is ControlDefinition` is the only predicate.

**Nesting was not one of the defects.** The issue's "done when" asked for depth coverage; the
AL parser already walked the layout to arbitrary depth, and the depth-3 test
(`ControlNestedThreeGroupsDeep_IsARow`) passed before this change as well as after. It is
carried as a regression guard, not as a RED.

<a id="the-emitted-document"></a>

## The emitted document

A probe bundle declaring the same shapes as corpus fixture 60427 emits this (elided to the
attributes that matter; `AL_RUNNER_TRACE_OBJECT_METADATA=2` prints it in full):

```xml
<PageDefinition ID="70002" Name="PCF Probe Page">
  <Properties PageType="Card" Editable="1">
    <SourceObject SourceTable="70001"/>
  </Properties>
  <Content>
    <Containers xsi:type="ControlContainerDefinition" ContainerType="ContentArea">
      <Controls xsi:type="ControlGroupDefinition" ID="1780766225" Name="Level1" Editable="true" Enabled="true" Visible="true">
        <Controls xsi:type="ControlDefinition" ID="369657861"  Name="Entry No."        DataColumnName="1"                Visible="true"/>
        <Controls xsi:type="ControlDefinition" ID="419265194"  Name="TreeLocalVar"     DataColumnName="Control419265194" Visible="true"/>
        <Controls xsi:type="ControlGroupDefinition" ID="601111220" Name="Level2" ...>
          <Controls xsi:type="ControlDefinition" ID="1110612037" Name="TreeOption"     DataColumnName="14"               Visible="true"/>
          <Controls xsi:type="ControlGroupDefinition" ID="1238807837" Name="Level3" ...>
            <Controls xsi:type="ControlDefinition" ID="1061314080" Name="TreeDeepHidden"   DataColumnName="18" Visible="false"/>
            <Controls xsi:type="ControlDefinition" ID="1327436012" Name="TreeDeepEditable" DataColumnName="17" Editable="false" Visible="true"/>
          </Controls>
        </Controls>
      </Controls>
    </Containers>
    <Containers xsi:type="ControlContainerDefinition" ContainerType="FactBoxArea">
      <Controls xsi:type="InfopartSystemDefinition" ID="2016712184" Name="DefaultSummaryPart" .../>
    </Containers>
  </Content>
  <Expressions>
    <Expression Name="Control419265194" Datatype="Integer" ExpressionType="SourceExpression"
                SourceExpression="LocalTreeVar" ExpressionIsAssignable="1"/>
  </Expressions>
</PageDefinition>
```

Three things to read off it:

* `DataColumnName` is `"1"`, `"14"`, `"17"`, `"18"` for the Rec-bound controls and
  `"Control419265194"` for the variable-bound one. `ControlDefinition.IsBoundToTableField` is
  13 bytes of IL — `int.TryParse(DataColumnName, out fieldNo)` — so the document states the
  binding decision directly and nothing has to parse an AL expression to recover it.
* The variable-bound control carries **no** `SourceExpression` attribute. The name lives in
  the `<Expressions>` section, keyed by the control's `DataColumnName` — which is exactly
  `page.Expressions.FirstOrDefault(x => x.Name == control.DataColumnName)` in BC's code.
* The fact box part is an `InfopartSystemDefinition` and the groups are
  `ControlGroupDefinition`. Neither is a `ControlDefinition`, so neither becomes a row.

<a id="control-definition-has-no-subtypes"></a>

## `ControlDefinition` has no subtypes

BC's predicate is `ed is ControlDefinition`, a type test that would also select any subclass.
The runner matches on the document's `xsi:type` *string*, so it has to know whether that
predicate can select a type spelled anything other than `ControlDefinition` or
`MetaControlDefinition`. Measured by reflection over
`Microsoft.Dynamics.Nav.Types.dll` 28.1 — enumerate every type whose `BaseType` is the one in
question:

| type | base | direct subtypes |
|---|---|---|
| `ControlDefinition` | `ControlDataboundDefinition` | **none** |
| `MetaControlDefinition` | `MetaControlDataboundDefinition` | **none** |
| `DataFieldDefinition` | `ElementDefinition` | **none** |
| `ControlGroupDefinition` | `ControlGroupBaseDefinition` | **none** |

So naming the two type strings selects precisely the set BC's `is` test selects. This is the
one place where an exact string match is equivalent to a type test rather than an
approximation of one, and it stops being true the moment a BC release adds a subclass — which
is why it is written down here rather than left implicit at the call site.

<a id="the-three-property-defaults"></a>

## The three property defaults, and why `Editable` is not `"true"`

`Enabled`, `Editable` and `Visible` are **text** columns carrying the declared property
expression, so a control with `Visible = NoFieldVisible` reports the variable's name. The
question this section answers is narrower: what does BC report when the property is *absent*
from the document, which is the common case?

The document omits an attribute when the property is at its default, so the answer is whatever
BC's deserializer supplies. Measured by instantiating `ControlDefinition` through reflection
and reading each property off a fresh instance:

| property | `DefaultValueAttribute` | value on a fresh instance |
|---|---|---|
| `Enabled` | yes | `"true"` |
| `Visible` | yes | `"true"` |
| `Editable` | **no** | **`null`** |

`Enabled` and `Visible` therefore default to `"true"`, which is what the AL derivation already
substituted and what corpus codeunit 60921 pins for `Visible`. `Editable` does **not**: BC
passes the null straight to `NavText.CreateTruncated`, which renders it as the empty string.
The AL derivation substituted `"true"` there too, so this change makes `Editable` answer `""`
for a control that does not declare it.

**That last cell is measured from BC's own type, not from a service tier.** No corpus test
pins `Editable` today — codeunit 60921 pins `Visible` only, and corpus PR #300's codeunit
60426 asserts `Editable` nowhere. So the claim rests on the reflection measurement above plus
BC's decompiled `array[7] = NavText.CreateTruncated(len, control.Editable)`, and not on a real
tier having answered it. It is the honest reading of both, and it is the kind of claim
`.claude/rules/ask-the-corpus-before-claiming-bc-behavior.md` says to name rather than to
assert quietly. A corpus test asserting `Editable` on an undeclared control is the follow-up
that would settle it; #3625 tracks it.

<a id="option-and-enum"></a>

## `OptionString`, and why an Enum field takes the same branch

BC gates `OptionString` on `f.Type == NavType.Option`, so the guard is the field's `NavType`
and explicitly not "does the field carry option metadata". Those two look interchangeable and
are not, which is worth stating because the obvious reading of the guard is that an AL
`Enum "X"` field — a different AL type, and a distinct `NavType.Enum` member exists — should
answer `''`.

It does not, and the document says so. BC's own emitted **table** document, for a table
declaring both an `Option` field and an `Enum` field:

```xml
<Field Name="Option Field" ID="14" Datatype="Option" OptionString=" ,Draft,Active,Closed" ... />
<Field Name="Status Field" ID="19" Datatype="Option" EnumTypeName="PCF Probe Status" EnumTypeId="70004" ... />
```

Both are `Datatype="Option"`. The Enum field is distinguished only by carrying an
`EnumTypeId`/`EnumTypeName` pair, and by having no inline `OptionString` of its own — BC
resolves that from the enum object instead.

The runner stores it the same way, for a reason of its own that is load-bearing and
independent: BC's generated AL calls `ValidateExpectedType(fieldNo, NavType.Option)` when
reading an enum-typed record field, so the metadata side has to match or every
`TestField`/`Read*EnumField` path throws `NavObjectDefinitionChangedException`
(`RecordPatches.NclMetaTableBuilder.cs`). So `FieldNavType == NavType.Option` is true for an
Enum-bound control here as well, and it answers the enum's members.

**This is a reading of BC's document and BC's decompiled provider, not a tier measurement.**
No corpus test pins `OptionString` for an Enum-bound page control — corpus codeunit 60426's
`OptionBoundControl_OptionStringIsTheFieldsMembers` uses `ALT Universal`'s `"Option Field"`,
which is genuinely `Option`-typed, and `ALT Universal` also has an `Enum`-typed
`"Status Field"` that no test binds a control to. An implementation keyed on "has option
metadata" and one keyed on `NavType` therefore agree on everything the corpus currently
measures, and this file takes the `NavType` route because it is what BC's code says.
[#3625](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/3625) tracks getting
the Enum case in front of a real tier.

<a id="scope"></a>

## Scope — emit-captured pages only

The gate is `HasBcPageMetadataDocument(pageId)`, mirroring `HasBcTableMetadataDocument`
(#3552). A page from a precompiled dependency `.app` keeps its `SymbolReference.json`-derived
rows, and that is deliberate rather than incidental: `DependencyPageMetadataXml.EmitPageXml`
reconstructs **no `<Controls>` element** for such a page — its own header says so — so routing
dependency pages through this file would answer **zero rows for every one of them**. That is a
regression wearing the shape of a conversion, and it is the single decision on this change
that a reviewer should check first.

Two consequences follow, and both are the reason the AL derivation is **not** dead code:

* a dependency page still needs `ResolveDependencyControlField` and the symbol-derived path;
* a source-compiled page whose document was never captured — compiled before the registry
  existed, or served from a cache written without it — still needs the AL-parsed path.

`OptionString` for a dependency page stays the empty string. `SymbolReference.json` does not
state the source field's option members and there is no `<Controls>` element to read them
from, so answering anything else would be a guess rather than a narrower answer.

<a id="not-done"></a>

## Deliberately not done here

* **Pageextension deltas (#3605).** A `pageextension`'s added controls do not appear in the
  base page's `<PageDefinition>` at all; BC emits them in a separate `MetadataRuntimeDeltas`
  document with `<ControlAdd>`/`<ControlChange>` elements. That is a second document source
  reached through a different registry entry, not a different branch of this file's parse, so
  it stays its own change.
* **`Sequence` on the dependency-symbol path.** BC's `Sequence` is a **0-based** enumeration
  index, captured before the `OrderBy` that sorts rows by control id
  (`Select((cd, i) => (sequence: i, control: cd))`). Both paths this change touches now
  produce that: the document path by construction, and the AL fallback by correcting
  `GetSourceParsedPageControlRows` from `++seq` to `seq++`. The **precompiled-dependency**
  path in `BcAppSymbolCache.CollectPageControlSymbols` is still 1-based and is deliberately
  left alone here — that file is being changed by another open pull request, and the fix is a
  one-character edit that belongs with whoever owns it rather than in a merge conflict. No
  corpus test pins `Sequence` on any path, so nothing catches any of this either way; the
  document and AL paths were corrected because leaving them disagreeing with each other would
  make one column mean two different things depending on which route answered.
