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

Moved to the funnels by #4142, and **again by #5509 to the storage layer below the triggers**
(next section):

| helper | #4142 | now |
|---|---|---|
| `BcRuntime.AssignAutoIncrement`, `StampSystemFieldsOnInsert` | `NavRecord.InsertAsync(DataError,bool,bool,bool)` | called by `WriteStorageFieldsOnInsert`, prepended to `RecordImplementation.InsertRecordAsync`; `InsertAsync(4)` carries `NoteInsertFunnel` |
| `BcRuntime.StampSystemFieldsOnModify` | `NavRecord.ModifyAsync(DataError,bool,bool,bool)` | called by `WriteStorageFieldsOnModify`, prepended to `RecordImplementation.ModifyRecordAsync`; `ModifyAsync(4)` carries `NoteModifyFunnel` (the Feature Key read-only guard, then the mark) |

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

## Written below the triggers (#5509)

In BC the AutoIncrement key and the four system stamps are contributed by the storage layer under
`NavRecord.InsertAsync(4)` / `ModifyAsync(4)`: both call `RecordImplementation.InsertRecordAsync` /
`ModifyRecordAsync` only after the `OnBeforeInsert/Modify` subscribers, the table trigger and
`NavGlobalTriggers` (decompiled on Ncl 28.4.53241.54346). So inside `OnInsert` `Rec."Entry No."` is `0`
and the stamps are default, and inside `OnModify` `Rec.SystemModifiedAt` is still the stored value;
`OnAfterInsert/Modify` subscribers and the caller see the written ones. The corpus measured it on
ten cloud legs and the Windows container (codeunit 67812, `Rec` side:
`XRecStamp_Insert_RecSystemStampsNotYetWrittenInOnInsert`,
`XRecStamp_Insert_RecAutoIncrementKeyNotYetAssignedInOnInsert`,
`XRecStamp_Modify_RecModifiedAtNotYetRestampedInOnModify`).

The #4142 placement ran the writers at the *start* of the funnel, ahead of all of that. Now:

```
InsertAsync(4)   : NoteInsertFunnel(this)                       <- marks the record
   ... OnBeforeInsert subscribers, OnInsert, NavGlobalTriggers ...
InsertRecordAsync: WriteStorageFieldsOnInsert(parentRecord)     <- consumes the mark, writes key + stamps
(ModifyAsync(4) / ModifyRecordAsync the same with NoteModifyFunnel / WriteStorageFieldsOnModify)
```

Page saves reach the same two methods (`InsertAsync(4)` directly, `ModifyAsync(3)` forwarding to
`ModifyAsync(4)`), so #4142's page arms still hold. The mark exists because
`NavForm.SyncTempTableWithSourceTableAsync` calls `InsertRecordAsync` / `ModifyRecordAsync` directly,
on a page's temp copy of a row that already carries its source's stamps; writing there would overwrite
them. No corpus or `tests/runner-extras` test reaches that route (checked by logging every unmarked
write across both suites: none), so the gate is pinned by `StorageLayerFieldWriteGateTests` rather than
by an AL arm. The mark is keyed by record identity and consumed once, so a funnel call that throws
before its storage write leaves a stale mark that only matters if the same `NavRecord` object is then
fed to the direct route.

Not touched: `Rename` (already stamped at `RenameRecordAsync`, storage level), and temporary
records, which still get stamps (and no AutoIncrement assignment; `AssignAutoIncrement` returns for
them) on the funnel route exactly as before, now after the trigger.

## A key the AL code assigned itself raises the counter (#5513)

`AssignAutoIncrement` runs after `OnInsert` (above), so a key that the caller, the table's `OnInsert` or an
`OnBeforeInsertEvent` subscriber put into the AutoIncrement field reaches it as a non-zero value. It used to leave
that alone and never move the counter, so the next zero-key `Insert` handed out a value the table already held.
The corpus (codeunit 67822, `AutoInc_*`, measured on the cloud legs of corpus PR 566) shows the effect: an explicit
key above the counter raises it, so the next zero-key `Insert` gets explicit + 1; a key at or below the counter,
a negative key, a refused duplicate and a `DeleteAll` leave it alone. The helper now does
`counter = max(counter, explicit key)` (`RaiseAutoIncrementCounterTo`), seeding a counter that has not started from
the table's stored high-water mark first. An explicit key refused by an error in `OnInsert` never reaches the
helper, and one refused on a unique secondary key has raised the counter by then, both as in BC.

An `Insert` refused by an error in `OnInsert`, after an earlier successful `Insert` and with no `Truncate` in the
transaction, does not roll the counter back, in BC (corpus 67822, ten cloud legs and the Windows container) or here; the
rows written since the last `Commit` do roll back. Measured for that shape only: a `Truncate` in the same transaction is
the exception, below.

## A refused Insert keeps no key; a Rename may not change one (#5518, #5517)

BC's SQL layer hands the assigned key back in the result buffer, and `RecordImplementation.InsertRecordAsync` adopts that
buffer only when the Insert lands (decompiled, Ncl 28.4). The runner stamps the key before the write
(`WriteStorageFieldsOnInsert`), so `BcRuntime.InsertRecordCompleted(bool, NavRecord)` is called from both exits of the
`InsertRecordAsync` state machine (before `builder.SetResult` and before `builder.SetException`, a Cecil hook in
`NclCecilRewrite.Records.cs` that refuses on any other count) and puts the field back to what it was when the Insert was
refused, by `false` or by an exception. The counter value stays consumed. A key the AL code assigned itself is not reset.
Corpus 67822 `AutoInc_FailedInsertOnUniqueKey_*` (Boolean receiver) and 67826
`AutoInc_UniqueKeyRefusedInsert_UnderAsserterror_*` (error thrown).

BC's SQL layer also refuses an UPDATE of an AutoIncrement column; `RecordImplementationHelper.MapException` turns its
error code (18023796) into `You cannot change the value of an AutoIncrement field.` plus the Field, Table and
Identification lines. `RecordPatches.GuardRenameOfAutoIncrementField` is prepended to `RenameRecordAsync` and raises
the same exception, through BC's own `NavCSideException.CreateAutoIncrementFieldError`, when the AutoIncrement field's
value differs between the record and the renamed clone. Corpus 67822 `AutoInc_Rename_OfTheAutoIncrementKey_IsRefused`.

## A Truncate is part of the transaction (#5522)

The measured shapes fit a `Truncate` whose effect on the counter rolls back with the transaction, and whose removed rows
come back with it. The mechanism behind that is not known. Corpus 67826 (ten cloud legs; the probe is corpus PR 567)
measured, with an `Insert` refused by `OnInsert` and caught by `asserterror` as the rollback:

| shape | next zero-key `Insert` after the rollback |
|---|---|
| `Truncate(true)`, one Insert, refused Insert | the key the first Insert had (the table's rows: only the new one) |
| the same with two Inserts before | again the first key |
| rows `1..3` committed, `Truncate(true)`, one Insert, refused Insert | `4`; the three rows are back |
| `Truncate(true)`, `Commit`, one Insert, refused Insert | the next value: it is consumed |
| `DeleteAll`, one Insert, refused Insert | the next value; the first row is rolled back |
| rows committed, `Truncate(false)`, one Insert, refused Insert | the value that Insert had; the committed rows are back |

`DataProvider_TruncateAsync` (`HelperShims.cs`) therefore snapshots the table for the rollback and journals the counter
(`RecordPatches.NoteIdentityRestart`) before it clears the store, for a stored table only, never a temporary record.
`RollbackToCommitPoint` puts each journaled counter back and `MarkCommitPoint` forgets the journal. The rows' clear is
`ClearProviderInPlace`, the provider's own "no rows yet" state; the earlier in-place `Clear()` of each tree left a
`FindSet` one row short of `Count()` in a test following a passing one (#5516).

## A failed guarded Codeunit.Run rolls the counter back too (#5527)

A guarded `Codeunit.Run` (`if Codeunit.Run(...) then`, or its result captured) that fails is rolled back as a whole,
and a Truncate inside it comes back with it. Corpus 67840 `AutoInc_GuardedRun_*` (corpus PR 568; ten cloud legs):
rows `1..3` committed, then a failing run that does `Truncate(true)`, `Truncate(false)` or `DeleteAll`, inserts and
errors:

| inside the failed run | the caller's next zero-key `Insert` |
|---|---|
| `Truncate(true)`, Insert, Error | `4`: the three rows are back and the counter stands where it did before the Truncate |
| `Truncate(false)`, Insert, Error | `4` (the failed run's Insert had taken `4`) |
| `DeleteAll`, Insert, Error; or Insert, Error | `5`: the value the failed run took is consumed |
| the first row again, with row 3 deleted and committed before the run | `4`, not `3`: the counter, not the rows' highest key |
| nested: an inner failing run truncated, the outer inserts and succeeds / fails | `5` either way: the outer's Insert took `4` after the inner restore |
| nested: an inner run that succeeds after `Truncate(true)` and Insert, then the outer inserts and fails | `3`: the inner run's row `1` stays, the outer's Insert (`2`) is rolled back but its value is consumed |

`PushTransactionWorldScope` therefore opens a counter journal beside the scope's row snapshots; a Truncate's
`NoteIdentityRestart` enters the top-level journal and every open scope's. `PopTransactionWorldScope(restore: true)`
puts the scope's counters back and drops the entries the scope created from the levels around it (no Truncate happened
there any more), `restore: false` forgets the tables at every level, and `MarkExplicitCommitPoint` clears each scope's
journal, all as the row snapshots are handled.

## The type of the counter (#5521)

A BigInteger AutoIncrement field takes the whole counter (`BcRuntime.AutoIncrementBoxedValue`; the `(int)` cast wrapped
5000000001 to 705032705, corpus 67826 `AutoInc_BigIntegerKey_*`). An Integer field cannot pass 2147483647 in BC: the
first `Insert` past it ends the transaction (`Transaction was rolled back by the server.`) and the test fails, on all ten
cloud legs, so no passing corpus test can pin it. The runner refuses by name (`docs/scope.md` §4) instead of storing a
wrapped, negative key. Residual: `asserterror` catches that refusal (#2871), so a test whose only statement under
`asserterror` is the overflowing Insert passes here and fails on BC.

## What a page DELETE does was not measured

`NavForm.SaveRecordAsync` has no delete branch, so nothing here says whether a page-driven
delete reaches `ALDeleteAsync` or `DeleteAsync` directly. #4142's body says the same — "I did
not check the delete path". It no longer matters for the User cascade: since #4766
`OnAfterUserDelete` sits on `RecordImplementation.DeleteRecordAsync`, whose only caller is
`NavRecord.DeleteAsync(4)` (bc284 `find_callers`), below both routes and after the
OnBeforeDelete subscribers and OnDelete triggers, where BC runs its own arm (corpus 61210).

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
`AlRunner.Tests/PageSaveDataLayerPrependBindingTests.ModifyRecordAsync_StampsTheModifiedPair_BelowTheTriggers`,
which reads the rewritten IL.

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
