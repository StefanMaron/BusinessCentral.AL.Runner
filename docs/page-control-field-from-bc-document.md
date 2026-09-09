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
<a id="solveeditable"></a>

## The three property defaults, and why `Editable` answers `True` where the others answer `true`

`Enabled`, `Editable` and `Visible` are **text** columns carrying the declared property
expression, so a control with `Visible = NoFieldVisible` reports the variable's name. The
question this section answers is narrower: what does BC report when the property is *absent*
from the document, which is the common case?

**It does not answer alike, and the casing is the tell.** Measured on a real service tier —
corpus codeunit 60424 against fixture page 60425, run
[`34329910568`](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/310),
identically on 27.0, 27.3, 27.5, 28.0, 28.1, 28.2, 28.3 and 28.4:

| column, control declaring none of the three | BC answers |
|---|---|
| `Enabled` | `true` |
| `Visible` | `true` |
| `Editable` | **`True`** |

Two different mechanisms produce those two spellings, and separating them is the whole of
this section.

### `Enabled` and `Visible`: the deserializer's own default, verbatim

Both are declared on `UIElementDefinition` and carry `[DefaultValue("true")]` — a **string**
literal. The document omits an attribute at its default, the deserializer substitutes that
string, and nothing afterwards rewrites it: no method on `PropertiesSolveHelper` writes either
property. So the lower-case `"true"` a caller sees is the attribute's own text, copied through.

### `Editable`: resolved afterwards by `SolveEditable`, and rendered from a `Boolean`

`Editable` is declared on `ControlDataboundDefinition` and carries **no**
`DefaultValueAttribute`, so a freshly deserialized control really does read `null` — and
`NavText.CreateTruncated` really does render null as `""`. Both facts are true and neither
decides the answer, because **BC's provider never reads that null.**

`GetControlsOnPage` obtains its page from `MetadataProvider.GetMasterPageForDesigner`, and the
chain below runs to completion before a single row is built:

```
GetMasterPageForDesigner
  -> GetMasterPageWithoutConfiguration
       -> MergePageAndTable
            -> PropertiesSolveHelper.SolvePropertiesDefaulting
                 -> SolvePropertiesDefaultingControls        // per control, with its MetaField
                      -> ControlDataboundDefinition.SolveProperties
                           -> PropertiesSolveHelper.SolveEditable   // writes control.Editable
```

`SolveEditable` (`Microsoft.Dynamics.Nav.Types` 28.1) is the specification, four rules in
order, the first two returning:

```csharp
control.TableEditable = field?.Editable ?? true;
control.TableAllowInCustomizations = field?.AllowInCustomizations ?? AllowInCustomizations.ToBeClassified;

if (!control.SourceExpressionIsAssignable) {                       // 1
    control.Editable = false.ToString(CultureInfo.InvariantCulture); return; }

if (control.Editable == null) {                                    // 2  <- the undeclared case
    control.Editable = (field != null ? field.Editable : true)
                          .ToString(CultureInfo.InvariantCulture);  return; }

if (!PropertyHelper.PropertyIsFalse(control.Editable)              // 3
    && masterPage?.PageProperties?.Editable == false)
    control.Editable = false.ToString(CultureInfo.InvariantCulture);

if (!PropertyHelper.PropertyIsFalse(control.Editable)              // 4
    && control.TableAllowInCustomizations != AllowInCustomizations.AsReadWrite) {
    // personalization / configuration SourceAppId, or "Editable" in Personalized/ConfiguredProperties
    control.Editable = false.ToString(CultureInfo.InvariantCulture); }
```

Rule 2 is the one that answers the question, and it explains the capital directly:
`Boolean.ToString(InvariantCulture)` returns `"True"`, not `"true"`. So the value **and** its
spelling both come from the same place — a `Boolean` being formatted, rather than a string
literal being copied. `Entry No.` on fixture page 60425 declares no `Editable` and neither
does the field it binds to, so `field.Editable` is `true` and the column reports `True`.

Rule 2 also explains why a declared `Editable = false` round-trips unchanged: the branch is
gated on `control.Editable == null`, so a declared value never reaches it. That is corpus test
`Record_PageControlField_DeclaredEditableFalse_RoundTripsAsFalse`, green throughout.

### What the runner reproduces, and what it does not

`SolveDocumentControlEditable` in `RecordPatches.PageControlFieldFromBcDocument.cs` implements
rules 1-3. `SolveParsedControlEditable` in `RecordPatches.PageControlFieldVirtualTable.cs`
implements rule 2 for the AL-parsed and precompiled-dependency paths, which carry neither a
`SourceExpressionIsAssignable` nor a page-level `Editable` to evaluate the other rules against.

BC has two near-identical attribute names on **different** elements, and confusing them is what
made #3659's rule 1 dead code before review caught it: a **control**
(`ControlDataboundDefinition`) carries `SourceExpressionIsAssignable`, while an `<Expression>`
entry (`DataFieldDefinition`) carries `ExpressionIsAssignable`. The sample above is an
`<Expression>`, so its shorter spelling is correct.

**Rule 4 is deliberately not reproduced**, on any path: the runner has no personalization or
configuration layer, so `SourceAppId` is never `PersonalizationAppId`/`ConfigurationAppId` and
`PersonalizedProperties`/`ConfiguredProperties` are never populated. The rule cannot fire, and
reproducing it would mean inventing the state it reads. If personalization ever lands, those
two methods are its call sites.

### How this was got wrong, and what generalizes

The reading this section replaces was: `Editable` has no `DefaultValueAttribute`, so a fresh
`ControlDefinition` reads `null`, so `CreateTruncated` renders `''`. Every step of that is
true and the conclusion was false, because a **fresh reflected instance is not the object the
provider reads** — something ran in between and overwrote the field.

The general form, worth carrying to the next virtual-table column: *reading a default off a
freshly constructed instance answers what the constructor does, never what a caller observes.*
The question to ask instead is what runs between deserialization and the read, and BC's
answer here — a whole property-defaulting pass, `SolvePropertiesDefaulting` — was two call
levels above the provider method already being read.

**And the check that would have caught it costs one PR**:
`.claude/rules/ask-the-corpus-before-claiming-bc-behavior.md`. The claim was correctly flagged
in this file as resting on a reflection measurement rather than a tier verdict, and #3625 was
filed to settle it. That is the process working — the cost was one wrong column shipped in the
meantime, not a wrong claim left standing.

<a id="a-failed-lookup-refuses"></a>

### Reading the field's `Editable`: a failed lookup refuses, BC's own answer stays silent

Rule 2 above resolves an undeclared `Editable` against the bound field's own `Editable`. That
value is not on `NCLMetaField` — it lives on the original `Types.Metadata.MetaField` hanging
off the NCLMetaTable's private `metadataAppGroupMetaTable`, so `GetMetaFieldEditable` reaches
it through five reflection lookups.

Every one of those used to answer `true` on failure (#3669). That is not a neutral sentinel
here: rule 2 renders the value as the AL-visible `"True"`, so a field BC reports non-editable
would be reported **editable** on the BC version where any of the five members moves — a
plausible wrong value, which is worse than an empty one, and the shape
`.claude/rules/loud-failures.md` forbids. Latent, not live: every BC version this repository
tests resolves all five.

The conversion is per read, because getting it wrong in the other direction breaks an ordinary
page on **every** BC version rather than only a future one. What decides each case is whether a
null can be BC's own answer. Measured off `Microsoft.Dynamics.Nav.Types.dll` and
`Microsoft.Dynamics.Nav.Ncl.dll` at 28.1.49838.54308, via `MetadataLoadContext`:

| read | BC's declared type | a null means | verdict |
|---|---|---|---|
| `NCLMetaTable.metadataAppGroupMetaTable` (lookup) | `MetadataExtension<MetaTable>` | the field is gone | **refuses** |
| …its **value** | reference type | never assigned | silent |
| `MetadataExtension\`1.Item` (lookup) | — | the property is gone | **refuses** |
| …its **value** | `MetaTable`, reference type | no original MetaTable | silent |
| `MetaTable.Fields` | `ImmutableArray<MetaField>` — a **struct** | impossible; boxes, never null | **refuses** |
| `MetaField.Id` | `System.Int32` | impossible | **refuses** |
| `MetaField.Editable` | `System.Boolean` | impossible | **refuses** |
| the `foreach` completing | — | the table has no such field | silent |

The three struct/value-typed members are the load-bearing half: `GetValue` on them cannot
return null, so a null read can only be a lookup that failed, and there is no BC answer for it
to be confused with. `Fields` being an `ImmutableArray` is also why a **non-enumerable** read
refuses rather than folding into the null branch — a member that is present and holds an
uninterpretable shape is the same "BC's layout moved" case, and folding the two is how #2786's
silent skip happened.

The three silent exits are BC's own answers, not failed reads, and the citation for the first
two is BC's own code: `NCLMetaTable.GetMetaTableOriginal()` is literally
`return metadataAppGroupMetaTable?.Item;` (Ncl 28.1), so BC treats a null at either level as an
answer and its callers take name-based fallbacks. The runner's `AssignMetaTableOriginal` is
best-effort by the same design — its own comment says BC "keeps its existing fallbacks rather
than the table failing to build". Refusing there would turn every table whose original MetaTable
was never assigned into an error. The third is BC's `field?.Editable ?? true`.

`ReadMetaFieldEditable` takes the metatable as `object` so each refusal can be driven with a
fake standing in for a moved member, without a BC install — the idiom of #3657 and #3664.
`AlRunner.Tests/PageControlFieldEditableShapeGapTests` has an arm per refusal, each asserting
the member name *and* the absence of the sibling branch's wording, plus a negative control per
silent exit.

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

<a id="pageextension-deltas"></a>

## Pageextension deltas — a second document, not a second branch

A `pageextension`'s added controls are **not** in the base page's `<PageDefinition>`. BC emits
them in the extension's own document, rooted `<MetadataRuntimeDeltas>`, and the runner keeps it
in `AlObjectMetadataRegistry` under kind `PageExtension` (#3548). Measured on
`AlRunner.Tests/Fixtures/ObjectMetadataCapture` with `AL_RUNNER_TRACE_OBJECT_METADATA=2`, where
`pageextension 70663` adds `Extra Note` to `page 70660`: the string `Extra Note` appears
**zero** times in the page's document and only in the extension's.

**Tables are the exact inverse** (#3600): a same-app `tableextension`'s added fields *are*
folded into the base `<MetaTable>`. A table intuition carried across produces the wrong
conclusion here, which is why this is written down rather than left to be re-derived.

The delta's payload is the same element shape the base document uses, so the same collector
reads both:

```xml
<MetadataRuntimeDeltas ID="70663" Name="OMR Thing List Ext">
  <ControlAdd ParentContainer="ContentArea" SemanticKind="Content" Operation="ContentLast">
    <Controls xsi:type="ControlDefinition" ID="114635149" Name="Extra Note"
              DataColumnName="50" ExtensionId="70662" ApplicationArea="#All" Visible="true" />
  </ControlAdd>
</MetadataRuntimeDeltas>
```

`DataColumnName="50"` is the `tableextension`'s field number, so the bound branch resolves it
against the extended table exactly as a base-page control resolves. The control `ID` is hashed
in the **extension's** id space, not the base page's — `RunnerPageInstance.MemberId(70663,
"Extra Note")`, matching what BC asks `LiveNavTestPage.GetField` for.

Only `<ControlAdd>` is read. `<ControlChange>` and `<ControlMove>` modify an *existing* row
rather than adding one; applying them is a separate claim, tracked by #3614 for the table twin.

### This was a regression, and the shape of it is worth keeping

Before #3628 the AL derivation merged extensions — `GetSourceParsedPageControlRows` folds
`_parsedPageExtensions` into the base page's rows. The document path short-circuits before
reaching it, so converting the table silently dropped every extension-added control. Measured
on a live probe (page 70700, `pageextension` 70702 adding `Extra Note`):

| runner | Page Control Field rows for page 70700 |
|---|---|
| `e2d96307~1` (before #3628) | `Entry No.`, `Extra Note` |
| `84068be8` (after #3628) | `Entry No.` |
| with #3605 | `Entry No.`, `Extra Note` |

**`TestPage` binding was never affected**, and that is measured rather than assumed: the same
probe reads `tp."Extra Note"` as `HELLO` on all three, because `GetPageControlFieldMap` merges
extensions on its own path. The damage was confined to the virtual table.

### `GetExtensionDeltasForAppObject` is not the route, and returning deltas there changes nothing

#3605 proposed making `RunnerXmlMetadataLoader.GetExtensionDeltasForAppObject` return the real
deltas instead of `null`. Decompiled on BC 28.1, that would have had no effect, because BC
reaches a page's extensions by a different path and three independent gates on it are shut:

```
NCLMetaForm.CreatePageDefinitionWithExtensions
  -> GetExtensionObjects<NCLPageExtension>(MetadataAppGroup)     <- gate 1 and 2
  -> ApplyPageExtensions -> ApplyExtensionObjects
       -> NCLApplicationObjectExtension.ApplyRuntimeDeltas       <- reads .Deltas
```

`GetExtensionDeltasForAppObject` is called from exactly one place —
`NCLApplicationObjectExtension.LoadMetadata`, which sets `base.Deltas` on an
`NCLPageExtension` object that BC only ever obtains through `GetExtensionObjects`. So the
loader method cannot be reached until that enumeration returns something:

| gate | BC's condition | runner |
|---|---|---|
| 1 | `group != NavAppGroup.BaseGroup` | `SessionPatches.NavSession_NavAppGroup` returns `BaseGroup` |
| 2 | `ObjectLoader.MetadataCache != null` | `RunnerMetaApplicationObjectLoader.MetadataCache` **throws** |
| 3 | `NCLMetadata.GetExtensionApplicationObjects` walks `navAppGroup.OrderedAppMetadata` | empty — the runner's app group carries no object metadata summaries (measured for #2893) |

Populating all three means constructing a real `NavAppGroup` with per-app runtime metadata and
an extension registry — the same obstacle `RecordPatches.PermissionMetadataPopulator.cs`
documents for permission sets, where replacing `BaseGroup` would disturb roughly 60 BC call
sites that compare group identity. Reading the delta document directly is both reachable and
strictly less invasive, so that is what this file does. `GetExtensionDeltasForAppObject`
therefore still answers `null`, which is BC's own "no deltas" value.

<a id="not-done"></a>

## Deliberately not done here

* **`<ControlChange>` / `<ControlMove>`.** Only `<ControlAdd>` is applied; see above.
* **`Sequence` is now 0-based on all three paths.** BC's `Sequence` is a 0-based enumeration
  index captured before the `OrderBy` that sorts rows by control id
  (`Select((cd, i) => (sequence: i, control: cd))`). The document path produces that by
  construction and the AL fallback by `seq++`; the precompiled-dependency path in
  `BcAppSymbolCache.CollectPageControlSymbols` was left 1-based by #3628 only because that file
  was being changed by another open pull request, and #3631 closed that residue once it merged.
  The inconsistency mattered more than either value: AL cannot see whether a page was
  source-compiled or came from a dependency, so one column meant two different things.
  `BcAppSymbolCachePageMetadataTests` pins it on the dependency path, which no corpus test can
  reach — corpus tests are compiled from AL source by the runner, so they always take the
  source-compiled route.
