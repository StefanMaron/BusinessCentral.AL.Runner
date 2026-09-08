# Page rowset triggers: `OnFindRecord` and `OnNextRecord`

A page can decide for itself which rows the client shows, instead of letting the platform
filter its `SourceTable`. It does that with two triggers:

```al
trigger OnFindRecord(Which: Text): Boolean
trigger OnNextRecord(Steps: Integer): Integer
```

The usual shape serves rows from a page-global `temporary` record when some flag is set, and
falls through to the platform otherwise:

```al
trigger OnFindRecord(Which: Text): Boolean
var
    Found: Boolean;
begin
    if not RunOnTemp then
        exit(Rec.Find(Which));
    TempBuf.Copy(Rec);
    Found := TempBuf.Find(Which);
    if Found then
        Rec := TempBuf;
    exit(Found);
end;
```

`TempBuf.Copy(Rec)` is what keeps the user's filters, the current key and the sort direction
applying to a rowset the page produced itself, and `Rec := TempBuf` is what positions the row
the client then reads.

## What a real service tier does (BC 28.4, measured)

The client does **not** call a trigger per navigation step. It anchors with `OnFindRecord` and
then walks with `OnNextRecord(±1)`, caching rows as it goes:

| moment | calls, in order |
|---|---|
| page open | `OnFindRecord('=><')`, then `OnNextRecord(1)`, then `OnNextRecord(-1)` repeatedly |
| `CurrPage.Update(false)` from an action | `OnFindRecord('=<')`, then `OnNextRecord(±1)` until the viewport is full |
| `TestPage.First()` / `Next()` / `Last()` / `Previous()` / `GoToKey()` over rows already cached | **nothing at all** |
| a walk that runs past the cached rows | further `OnNextRecord(±1)`, one per row |

Raw trace from the probe, 60 rows in the table and 30 in the buffer, `F[…]` an `OnFindRecord`
call and `N[…]` an `OnNextRecord` call, `=n:Row` the value each returned:

```
ACTION{F[=<]=Yes:L0001 N[1]=0:L0001 N[-1]=-1:L0003 … N[-1]=-1:L0041 }
WALK{N[-1]=-1:L0043 … N[-1]=-1:L0059 N[-1]=0:L0059 }
ROWS=L0059,L0057,L0055,…,L0011
```

Two things fall out of that and both matter here:

* **The `Steps` sign is the direction of travel in the record's own sort order**, not a screen
  direction. In the trace above the anchor was the bottom row and the client walked *up* with
  `-1`; anchored at the top of the same descending set it walks *down* with `+1`.
* **`First()` can raise trigger calls** — it did in the trace above, because the client had not
  yet reached the top of the set — and it can equally raise none. How many rows the client
  prefetches is a client-side detail.

The corpus pins the rows, not the call pattern, for exactly that reason: codeunit 60679
(`pagefindrecord/`) and codeunit 60680 (`record/TestCopyOntoTemporaryRecord.al`), both green
on a real 28.4 tier.

## What the runner does, and how it differs

The runner has no viewport and no prefetch. `TestPage` navigation is the only thing moving the
cursor, so it maps one navigation call to one trigger call:

| `TestPage` | runner raises |
|---|---|
| `First()` | `OnFindRecord('-')` |
| `Last()` | `OnFindRecord('+')` |
| `Next()` | `OnNextRecord(1)` |
| `Previous()` | `OnNextRecord(-1)` |
| `GoToKey()` / `GoToRecord()` / a field-value search | `OnFindRecord('-')` then `OnNextRecord(1)` per row of the scan |

This is deliberately not a reproduction of the client's prefetch. What it reproduces is the
observable: which rows a walk produces, in which order, and where it stops.

**`Steps` matches BC. `Which` does not, and cannot be made to.** The corpus pins `Steps ∈ {1,
-1}` because those are the only values a real client was observed to pass, and the runner passes
the same two. There is no equivalent to copy for `Which`: across every container run above, a
real client issued `OnFindRecord` only when *anchoring* — `=><` at page open, `=<` at a
`CurrPage.Update` refresh — and **never once in response to a `First()`, `Last()`, `Previous()`
or `GoToKey()`**, because by then it is walking rows it already holds. So BC has no `Which`
value for "go to the first row of this rowset" for the runner to reuse.

Passing `=><` anyway would be wrong rather than merely different: `=><` means *nearest to the
key the record currently holds*, so a page forwarding it to `Find` would answer whatever row is
near the cursor instead of the first one. `-` and `+` are the AL spelling of what `First()` and
`Last()` mean, which is why the runner sends them.

The honest cost: a page that **branches** on `Which` rather than forwarding it — `case Which of
'=><': …` — is a legitimate shape that would behave differently here than on a tier, and
nothing in the corpus pins `Which`, so nothing would catch it. A corpus test that did pin it
would have to assert `=><`/`=<`, which the runner does not send, so it would encode this
divergence as a failure rather than describe it. If that shape turns up in real AL, the fix is
for the runner to anchor the way the client does — not to change these two values.

The *number* of calls is a separate matter and is not a divergence anyone can depend on: no
page can observe it and no corpus test asserts it.

## The declaration check reads BC's metadata

<a id="page-trigger-metadata"></a>

`NavForm.OnFindRecord` and `NavForm.OnNextRecord` are virtuals whose **base** bodies are the
platform find and the platform step, so "does this page declare the trigger" cannot be answered
by looking a method up — every page resolves one, and invoking it always returns a plausible
answer.

BC answers it from page metadata: `NCLMetaForm.IsFindRecordTriggerDefined` and
`IsNextRecordTriggerDefined`, two of twelve `Is<Trigger>Defined` flags, all of them
`(DefinedTriggers & PageTriggers.X) != 0`. `NCLMetaForm.DefinedTriggers` is **reflection**, not a
read of the page's metadata document:

```csharp
string triggerName = pageTriggers2.ToString();
pageTriggers |= (IsTriggerImplemented<NavForm>(triggerName, isPublic: false) ? pageTriggers2 : 0);
if (pageTriggers2 == OnFindRecord || pageTriggers2 == OnNextRecord || pageTriggers2 == OnInit) continue;
foreach (NCLPageExtension ext in orderedExtensionObjects)
    if ((pageTriggers & pageTriggers2) == 0 && ext.IsTriggerImplemented<NavFormExtension>(triggerName, isPublic: true))
        pageTriggers |= pageTriggers2;
```

`IsTriggerImplemented` is `GetMethod(name)` / `GetMethod(name + "Async")` on
`ApplicationObjectClrType` with `DeclaringType != typeof(NavForm)` — the same `DeclaredOnly`
question, asked by BC.

`RunnerPageInstance.DeclaresRowsetTrigger` reads those two flags. It used to reflect over the
compiled page class instead, because they answered **false** for a page that did declare the
trigger (#3439, #3447): `IsTriggerImplemented` returns false for every name without throwing
when `clrType == null`, and the runner's Cecil replacement of `get_ApplicationObjectClrType`
resolved `Form{id}` where the AL compiler emits `Page{id}`. PR #3445 fixed that arm, and #3447
fixed what was left — `orderedExtensionObjects` is empty on a `CreateEmptyNCLMetaForm`-built
metaform, so a trigger a **pageextension** declared read false. The runner now replaces
`get_DefinedTriggers` with a body that runs BC's declaration check against its own pageextension
registry (`AlRunner/Patches/RecordPatches.PageTriggerMetadata.cs`). That check is transcribed
rather than called: BC's own static `IsTriggerImplemented(Type, string, bool)` — the form the
extension arm needs, because the runner holds an extension's `Type` and not an `NCLPageExtension`
— does not exist on 27.0, which declares only the instance form reading its own receiver.
Resolving it and answering "no triggers" when it was absent is what made every flag false on the
27.0 leg of #3557.

Registering the extensions is a separate step from counting them, and BC does both: every
`RaiseOn<trigger>Async` ends with a pass over `NavForm.pageExtensions`, filled only by
`RegisterPageExtension`, which BC calls from `NCLPageExtension.CreateExtensionInstanceAndBindToParent`
inside `NCLMetaForm.CreateObjectInstance` — a path the runner replaces. `RunnerPageInstance` binds
them at construction so BC's own loops run each extension's copy of the trigger; corpus codeunit
60658 (BusinessCentral.AL.Language.Tests#290) is what measures it.

`setSize` stays 0 on `RaiseOnNextRecordAsync`. Not because the flag is wrong any more — BC would
read the same value this caller already read — but because a non-zero value asks BC to make the
routing decision a second time, and one decision is better than two agreeing.

`AL_RUNNER_PAGE_TRIGGER_AUDIT=1` prints what the runner computed, one line per page, at process
exit. An inspection channel like `AL_RUNNER_HOOK_AUDIT`, not a diagnosis; it is what
`AlRunner.Tests/PageTriggerMetadataTests.cs` asserts against, because the flags have no
AL-observable of their own.

Where the page declares neither rowset trigger, nothing changes: the six navigation sites in
`AlRunner/Patches/MockTestPage.cs` fall back to `NavRecord.ALFindFirstAsync` /
`ALFindLastAsync` / `ALNextAsync` exactly as before.
