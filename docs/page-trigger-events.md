# Page platform trigger events

BC publishes a fixed set of platform trigger events from a page's lifecycle — the page
counterpart of a table's `OnBeforeInsertEvent` / `OnAfterModifyEvent` family. They are not the
manually declared `[IntegrationEvent]`s a page's own AL raises; those travel the universal
`<EventName>_Scope` path (issue #1794).

AL subscribes with `[EventSubscriber(ObjectType::Page, Page::"X", '<name>', '', false, false)]`.

## The nine events

Ordinals are BC's `NavTriggerEventType`, read off `NavForm`'s own
`EventReadyToFire((NavTriggerEventType)N)` call sites and cross-checked against
`NavPageTriggerEventHandler.On…EventAsync`'s `FireEventAsync(page, (NavTriggerEventType)N, …)`.
The two agree on every one. Decompiled from BC 28.1 with `ilspycmd`.

| ordinal | event | AL subscriber parameters |
|---|---|---|
| 11 | `OnOpenPageEvent` | `var Rec` |
| 12 | `OnClosePageEvent` | `var Rec` |
| 13 | `OnAfterGetRecordEvent` | `var Rec` |
| 14 | `OnAfterGetCurrRecordEvent` | `var Rec` |
| 15 | `OnNewRecordEvent` | `var Rec`, `BelowxRec: Boolean`, `var xRec` |
| 16 | `OnInsertRecordEvent` | `var Rec`, `BelowxRec: Boolean`, `var xRec`, `var AllowInsert: Boolean` |
| 17 | `OnModifyRecordEvent` | `var Rec`, `var xRec`, `var AllowModify: Boolean` |
| 18 | `OnDeleteRecordEvent` | `var Rec`, `var AllowDelete: Boolean` |
| 19 | `OnQueryClosePageEvent` | `var Rec`, `var AllowClose: Boolean` |

`OnQueryClosePageEvent`'s handler method carries a `closeAction` argument in
`NavPageTriggerEventHandler`, but the AL compiler refuses a subscriber that declares
`CloseAction` — `error AL0282: The member referenced by event subscriber
'OnQueryClosePageEvent' parameter 'CloseAction' is not found`. Measured, not assumed, while
building corpus PR 274.

## Ordering, measured on real BC

BC 28.4.53241.0, onprem w1 container, one page-driven save on a `PageType = List` with
`DelayedInsert = false`. Each name below is one `ALT Trigger Log` row, in `Entry No.` order:

```
PageOnOpenTrig  PageOpenEvt  PageAfterGetCurrEvt(L1)
PageOnModifyTrig(BEFORE>DIRECT)  PageModifyEvt(BEFORE>DIRECT)
PageOnQueryCloseTrig  PageQueryCloseEvt  PageOnCloseTrig  PageCloseEvt
```

So the page's own trigger of a given name always runs **before** the matching platform event,
and `OnQueryClosePageEvent` runs before `OnClosePageEvent`. `Rec` carries the post-edit row and
`xRec` the pre-edit row.

Two further measurements from the same container, which corrected guesses that looked
reasonable:

- **`OnInsertRecordEvent` sees the row as it stood when the KEY was committed.** Typing the
  primary key into a new row inserts it there and then; the later control write is a separate
  `OnModifyRecordEvent`. A test expecting the finished row at insert time fails on real BC.
- **`AllowInsert := false` is not a quiet no-op.** The client refuses the entry with
  `The view is filtered, and the entry is outside the filter.` `AllowModify := false`, by
  contrast, leaves the row unwritten with no error.

## How the runner serves them

`NavForm.RaiseOnModifyRecordAsync` and its eight siblings already run — `RunnerPageInstance
.InvokeRecordTrigger` calls them — and each ends by asking whether the event is subscribed and,
if so, firing it through `NCLMetaForm.PageTriggerEventHandler`. So the runner does not dispatch
these events; it registers subscribers where BC looks for them, and BC fires.

`EventSubscriberPatches.InjectPageTriggerSubs` (in
`AlRunner/Patches/EventSubscriberPatches.PageTriggerEvents.cs`) mirrors the table path:
resolve the page's `NCLMetaForm`, ensure its `pageTriggerEventHandler` is non-null, take the
event's `NavEventScope` with `CreateIfNotFound`, and append a `NavEventSubscription` built by
the same `BuildSubscription` the table path uses.

One dependency is easy to miss. `NavEventSubscription`'s constructor calls
`NavEventPublisherReflectionHelper.GetScopeType(nclMetaApplicationObject.ApplicationObjectClrType, …)`
and does not guard a null first argument. `RecordPatches
.NCLMetaApplicationObject_get_ApplicationObjectClrType` resolved a Page to `Form{id}`; AL-compiled
pages emit `Page{id}`, so it answered null for every page and all 115 subscriptions on a
Base-Application-bearing run threw `NullReferenceException` while the run reported nothing.

## Upstream coverage

`StefanMaron/BusinessCentral.AL.Language.Tests` PR 274, merged: nine tests in
`handlers/TestPageTriggerEvents.al`, green on all eight cloud legs.
`OnDeleteRecordEvent` and `OnAfterGetRecordEvent` are deliberately uncovered — `TestPage` offers
no direct page-level delete, and `OnAfterGetRecordEvent` fires per row fetched, which that suite
cannot make deterministic.
