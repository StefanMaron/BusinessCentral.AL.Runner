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
for the same reason — it is the precedent this change follows. Since #4701 it and
`OnBeforeUserModify` sit one level lower, on `RecordImplementation.InsertRecordAsync` /
`ModifyRecordAsync`, which the two funnels call after their subscriber and trigger dispatch —
where BC runs its own User system-table arm (corpus 61208).

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

**Measured, not argued.** The full corpus at `34fd028e` (corpus PR #379's head, BC
28.1.49838.53910) was run before the change and twice after against one cache root:

| run | total | failures |
|---|---|---|
| before | 3548 | **3**, all in `Codeunit60562` — the three new page arms |
| after, cold | 3548 | 0 |
| after, warm | 3548 | 0 |

Read from each run's `--out` classification (`all_failures`), not from the summary line. The
total is identical in all three, so nothing disappeared rather than being fixed, and the only
tests that moved are the three that were meant to. Cold and warm agree exactly.

That corpus includes the suites pinning `xRec` on both routes — codeunit 60179
(`OnModify_xRec_MirrorsRecValues_WhenCalledFromCode`) and codeunit 60235
(`Record_Modify_FromPage_xRecHoldsPreviousValue`) — and both stayed green across the move.

### Two limits, stated rather than resolved

**No corpus test reads a system field or an AutoIncrement value off `xRec`.** So the corpus
confirms that nothing observable regressed; it does not confirm that the new `xRec` content is
what a service tier answers. Settling that needs an upstream test reading `xRec.SystemCreatedAt`
inside `OnInsert`, which does not exist today.

**The page-MODIFY arm never went red.** `PageSave_ModifiedRow_MovesModifiedStampAndLeavesCreatedStamp`
passed against the unfixed runner too, because its row was seeded by `Record.Insert` — already
stamped — and BC's datetime granularity makes `SystemModifiedAt >= previous` true whether or not
the modify re-stamped. The corpus therefore adjudicates the page-modify **rule** on real BC but
does not discriminate the runner's modify-funnel binding; what does is
`AlRunner.Tests/PageSaveDataLayerPrependBindingTests.ModifyFunnel_CarriesTheSystemModifiedStamp`,
which reads the rewritten IL and goes red under two independent mutations.

That limit was taken up as #4330 and is **partly resolved**, in corpus PR
[#392](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/392). The section
below records what the attempt measured, because the negative results are the reusable part.

## Why `SystemModifiedAt` cannot pin a page modify (#4330)

Measured on `28.1.49838.53910` against corpus `0e3a448c`. The question was whether the arm above
could be made to discriminate a platform that stamps nothing on the page-modify path. It cannot,
and the five candidates each fail differently — the table is here so the next attempt does not
re-run them.

| candidate | measured | verdict |
|---|---|---|
| strict `>` on `SystemModifiedAt` | seed-insert → page-save gap is **0–1 ms** warm; one formulation failed **3 of 10** full-codeunit runs | flaky |
| filler writes to widen that gap | 40 inserts between the two still left **0–1 ms** | does not work — the cost is in the page machinery either side, not between them |
| two consecutive page saves | collided **9 of 16** times; the second save is the faster one | worse |
| `SystemRowVersion` | advanced on **16 of 16** page saves, clock-free | **does not discriminate** — also advanced **8 of 8** with the system-field modify stamp no-op'd, because `RowVersionPatches` stamps at `TempTableDataProvider.Insert/Modify`, a layer below |
| `SystemModifiedBy` | one session user, unchanged across the modify | cannot move |

So the re-stamp is not observable from that codeunit's shape without depending on tier speed, and
corpus PR #392 drops the claim rather than asserting it vacuously.

### What it found instead: `Assert` compares DateTimes to the MINUTE

`_fixtures/Assert.al`'s `Equal` compares non-numeric variants as
`Format(Left, 0, 2) = Format(Right, 0, 2)`, which for a `DateTime` renders `09/20/26 01:10 PM`.
Measured: a page save **171 ms** after its seed insert compared EQUAL through
`Assert.AreNotEqual`, while the raw AL `<>` on those two values answered true.

That makes the *other* half of the arm weak too — `SystemCreatedAt must NOT change` held even
when it moved. Against a mutant whose modify path re-runs the insert stamp, shifting
`SystemCreatedAt` by 5 ms:

| | that mutant |
|---|---|
| the arm before #392 | **PASS, 3 of 3** |
| the arm in #392, comparing `Format(_, 0, 9)` | **FAIL** |

`Format(_, 0, 9)` is the round-trip form (`2026-09-20T11:10:12.851Z`) and carries milliseconds.
Tracked for the corpus at large as #4439.

**Trap for anyone re-measuring this**: read the row back with `Get` before capturing a system
field. Capturing off the in-memory record after `Insert()` gives a value ~7 ms ahead of the
stored one, which reads as the platform moving `SystemModifiedAt` *backwards* on the next write.
It does not; that was a probe error, deterministic and reproducible across both write routes,
and it survived review of its own numbers because BC's decompiled `PopulateAuditFields` supplied
a plausible mechanism for it (#4439).
