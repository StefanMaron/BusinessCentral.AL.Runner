# Where the data-layer prepends go, and why a page save used to miss them

Issue #4142. The runner's AutoIncrement assignment and system-field stamps used to sit on
`NavRecord.ALInsertAsync` / `ALModifyAsync`, the methods AL's `Rec.Insert()` and `Rec.Modify()`
lower to. A **page** save does not go through either, so a row written by `CurrPage.Update()`
kept an AutoIncrement key of `0` and empty system fields.

All IL below was read from `Microsoft.Dynamics.Nav.Ncl.dll` at **28.1.49838.53910**, the one
artifact set on the machine that wrote this. Re-measure before quoting it for another BC major:
27.x and 28.x are different binaries.

## The two routes

```
AL    Rec.Insert()      ->  ALInsertAsync(DataError,bool,bool)  ->  InsertAsync(DataError,bool,bool,bool)
page  CurrPage.Update() ->  NavForm.SaveRecordAsync(bool,bool)  ->  InsertAsync(DataError,bool,bool,bool)

AL    Rec.Modify()      ->  ALModifyAsync(DataError,bool,bool)  ->  ModifyAsync(DataError,bool,bool,bool)
page  CurrPage.Update() ->  NavForm.SaveRecordAsync(bool,bool)  ->  ModifyAsync(DataError,bool,bool)
                                                                ->  ModifyAsync(DataError,bool,bool,bool)
```

**Insert and modify do not have the same shape, and that is the trap.** Insert's two routes call
the same overload. Modify's do not: `NavForm.<SaveRecordAsync>d__43::MoveNext` calls the
**3-arg** `ModifyAsync`, while `NavRecord.<ALModifyAsync>d__277::MoveNext` calls the **4-arg**
one. They meet only because `ModifyAsync(3)` is a two-instruction forwarder:

```
IL_0000: ldarg.0
IL_0001: ldarg.1
IL_0002: ldarg.2
IL_0003: ldarg.3
IL_0004: ldc.i4.0                 // isBulkModify: false
IL_0005: callvirt ValueTask`1<bool> NavRecord::ModifyAsync(DataError, bool, bool, bool)
IL_000a: ret
```

So the funnels are `InsertAsync(4)` and `ModifyAsync(4)`, and **`ModifyAsync(3)` must not also
carry a prepend** — a page modify would stamp twice and an AL modify not at all.

`AlRunner.Tests/PageSaveDataLayerPrependBindingTests.cs` pins all of this by reading the
rewritten IL, including the two "must not also be on" directions.

## Which prepends moved, and which did not

Moved to the funnels by #4142:

| helper | now prepended to |
|---|---|
| `BcRuntime.AssignAutoIncrement` | `NavRecord.InsertAsync(DataError,bool,bool,bool)` |
| `BcRuntime.StampSystemFieldsOnInsert` | `NavRecord.InsertAsync(DataError,bool,bool,bool)` |
| `BcRuntime.StampSystemFieldsOnModify` | `NavRecord.ModifyAsync(DataError,bool,bool,bool)` |

`UserTableTriggerPatches.OnBeforeUserInsert` was moved to `InsertAsync(4)` earlier, by #4121,
for the same reason — it is the precedent this change follows.

Deliberately **not** moved, and tracked separately (#4325):

| helper | still on | why it is a separate change |
|---|---|---|
| `ALDatabasePatches.NoteRecordInsertWrite` / `NoteRecordWrite` | `ALInsertAsync`, `ALModifyAsync`, `ALDeleteAsync`, `ALRenameAsync`, `DeleteAllAsync`, `ModifyAllAsync` | bookkeeping, not a field write: its observables are `Database.IsInWriteTransaction()`, the rowversion clock and the rollback snapshot, and each needs its own corpus arm and its own double-count analysis against the bulk entry points |
| `AllProfileWritePatches.GuardAllProfileInsert` | `ALInsertAsync` | a refusal policy, not a stamp; moving it changes which writes are refused |
| `PageBackgroundTaskWritePatches.Guard*` | the four `AL*` entry points | same |
| `UserTableTriggerPatches.OnAfterUserDelete` | `ALDeleteAsync` | the delete route from a page was never measured — see below |

## What a page DELETE does was not measured

`NavForm.SaveRecordAsync` has no delete branch, so nothing here says whether a page-driven
delete reaches `ALDeleteAsync` (where the User cascade sits) or `DeleteAsync` directly. #4142's
body says the same — "I did not check the delete path" — and this change does not check it
either. Stated as unmeasured rather than resolved either way.

## The xRec ordering consequence, and why it is the right direction

`ALInsertAsync`'s state machine assigns the before-image **before** it calls the funnel:

```
<ALInsertAsync>d__251::MoveNext
  ...
  IL_0093: call     NavRecord NavRecord::get_OldRecord()
  IL_0099: callvirt void NavRecord::ALAssign(NavRecord)      // xRec := Rec
  IL_00b9: callvirt ValueTask`1<bool> NavRecord::InsertAsync(DataError, bool, bool, bool)
```

`ALModifyAsync` does the same at `IL_010f`. So moving a stamp from the AL entry point to the
funnel moves it from **before** that assignment to **after** it: on the AL route, `xRec` now
holds the *pre-stamp* values during `OnInsert` / `OnModify` rather than the post-stamp ones.

That is the faithful direction, not a regression. On a real tier the AutoIncrement value and
the system fields are contributed by the storage layer, which sits below both entry points —
so a before-image snapshotted above the storage layer cannot contain them. The previous
placement made `xRec` carry values real BC would not have put there.

**Measured, not argued:** the full corpus at `34fd028e` (corpus PR #379's head) went from
`3545 pass / 3 fail` to `3548 pass / 0 fail` across this change, with no test moving in the
other direction. That corpus includes the suites that pin `xRec` on both routes — codeunit
60179 (`OnModify_xRec_MirrorsRecValues_WhenCalledFromCode`) and codeunit 60235
(`Record_Modify_FromPage_xRecHoldsPreviousValue`) — and both stayed green.

The honest limit: no corpus test asserts a **system field or an AutoIncrement value read off
`xRec`**, so the corpus confirms that nothing observable regressed rather than that the new
`xRec` content is what a service tier answers. Settling that needs a corpus test that reads
`xRec.SystemCreatedAt` inside `OnInsert`, which does not exist upstream today.
