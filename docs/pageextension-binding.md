# Binding a page's pageextensions to the page object

## Binding

The runner constructs a page's `Page{id}` .NET type directly (`RunnerPageInstance.TryCreate`),
rather than going through BC's `NCLMetaForm.CreateObjectInstance`. That BC method does three
things, measured on `Microsoft.Dynamics.Nav.Ncl.dll` 28.1
(`Microsoft.Dynamics.Nav.Runtime.NCLMetaForm.CreateObjectInstance`):

```csharp
public NavForm CreateObjectInstance(NavRecord record)
{
    NavForm navForm = /* construct the Page{id} type */;
    foreach (NCLPageExtension orderedExtensionObject in orderedExtensionObjects)
        orderedExtensionObject.CreateExtensionInstanceAndBindToParent(navForm, record);
    navForm.CallOnMetadataLoadedExtensionMethod();
    return navForm;
}
```

The runner does all three, but it used to do the second one LAST: the binding ran from the
`RunnerPageInstance` constructor, which is reached only after `SetSourceTable` /
`EnsureMetadataLoaded` has already driven the page's metadata load. See "Ordering" below for why
that is too late.

`RunnerPageInstance.RegisterPageExtensionsOnTheForm` is the middle step, added by #290 so that
BC's `RaiseOn<trigger>Async` would run each extension's copy of a page trigger. It constructs the
emitted `PageExtension{id}` type and passes it to `NavForm.RegisterPageExtension`.

**It already existed and already worked. What #4145 changed is only WHEN it runs.**

## Why an empty list loses only the globals

`CallOnMetadataLoadedExtensionMethod` walks `PageExtensions` and calls each extension's
`OnMetadataLoaded`. For a pageextension the AL compiler emits that override containing one
`RegisterSourceExpression(...)` call per control the extension declares, with a getter and a
setter closing over the extension's own globals.

So with the list empty:

| a control the pageextension declares | resolves? | why |
|---|---|---|
| bound to a **`SourceTable` field** | yes | it comes from the merged page metadata, which already carries extension controls |
| bound to the **extension's own global** | **no** | only `RegisterSourceExpression` can publish that binding, and it never ran |

That asymmetry is the whole diagnostic signature of the bug: one control found and its
neighbour in the same `addlast(Content)` block not found. It is also what separated the two
candidate causes in issue #4145 — had the metadata not carried extension controls, neither
would have resolved.

Corpus codeunit 60978 (`PageExtControl_BoundToExtensionGlobal_*`,
`PageExtControl_BoundToRecField_*`, `PageExtControl_BaseControl_*`; corpus PR
StefanMaron/BusinessCentral.AL.Language.Tests#359) measured a real BC service tier finding
**both** on all 8 cloud legs.

## Ordering

`RegisterPageExtensionsOnTheForm` must run after the page object exists and **before** whatever drives the
page's metadata load — `SetSourceTable` on the record-bearing path, `EnsureMetadataLoaded` on
the record-less one. That call is what raises `OnMetadataLoaded`; an extension registered
afterwards registers nothing.

## Pages the runner builds for AL, not for a TestPage

`Page.Run` / `Page.RunModal` (`BcRuntime.NCLMetaForm_CreateObjectInstance` →
`ConstructFormForStaticEntry`) and a Page variable (`CodeunitPatches.NavFormHandle_CreateTarget`)
construct the page themselves and then call `SetSourceTable`. Both call
`RunnerPageInstance.BindPageExtensionsBeforeMetadataLoad` between the two, for the reason in
"Ordering". When a TestPage traps or is handed that page later, `RunnerPageInstance.Adopt`
reuses those instances instead of binding a second time (#4738).

Before this, the binding happened only at adoption, so on a trapped page an extension's
expression-bound control (`field(Context; Format(Rec."Context Record ID"))`, Base Application
pageextension 705 on page 700) and its global-bound controls were not found, and its
`OnOpenPage` did not run. Corpus codeunit 67470 "PXR Tests"
(StefanMaron/BusinessCentral.AL.Language.Tests#452) is the service-tier measurement.

## What is deliberately NOT done

`NavForm.CallInitializeComponentExtensionMethod` also walks `PageExtensions`, calling each
extension's `InitializeComponent()`. It sits behind `RunnerFormInit.ShouldRunRealFormInit` and
answers `false` for a TestPage form, because BC calls it from the generated
`InitializeComponent()` **inside the page constructor**, while the runner marks the instance on
the line after `ctor.Invoke` returns.

Widening that guard looks like part of this fix and is not. Measured (#4145): with the guard
widened to `ShouldResolveMasterPage`, the call still observes `PageExtensions.Count == 0`,
because binding cannot have happened yet — it needs the constructed form, and this runs inside
the constructor. Reverting the guard to the narrow gate leaves all three arms of
`tests/runner-extras/pageext-control-source-expression` green, so the widening was shipped-dead
code and was dropped.

So the guard stays as it was. If a future change needs extensions bound during construction,
that needs a different mechanism than a guard widening, and the constraint above is why.

## The subpage part

A pageextension control on a page used as a **subpage part** is covered by the same bind.
The part's form is built through `NavFormHandle.CreateTarget`, the Page-variable path above
(measured: removing the bind there, and only there, reds the arm below), so its extensions are
bound before its `SetSourceTable` and `RunnerPageInstance.Adopt` / `AdoptFromHost` reuse them. Corpus codeunit 60980
`SubPageExtControl_BoundToExtensionGlobal_IsFoundAndReadsItsValue` measures it (#4181, fixed
together with #4738).
