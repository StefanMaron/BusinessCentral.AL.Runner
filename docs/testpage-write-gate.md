# The TestPage write gate: when a page-driven Insert or Modify actually happens

A `TestPage` that assigns a control does not necessarily write a row. Business Central decides
at save time, on the **values**, and both halves of the decision — the insert half and the
modify half — run the same comparison. This document records what BC's own code does, what
measured it, and what the runner does to match.

Issue: [#3055](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/3055).
Corpus PR: [#271](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/271).

## <a id="the-gate"></a>What BC does

`NavForm.SaveRecordAsync` is the page-write path. Decompiled from
`Microsoft.Dynamics.Nav.Ncl.dll` (BC 28.3, `NavForm.<SaveRecordAsync>d__43.MoveNext`), the
decision is:

```csharp
bool flag  = !SafeSourceTable.HasBeenInserted;                                   // new row?
bool flag2 = !SafeSourceTable.CompareAllNormalFields(SafeSourceTable.OldRecord,  // values moved?
                                                     fieldsInitializedFromFilters);
if (!flag2)
    flag2 = (!flag & calledFromALCode) && SafeSourceTable.RecordImplementation.HasChangedFields;
if (!flag2)
    break;                       // <- no write at all, and no OnInsertRecord/OnModifyRecord
if (flag) { SplitKey(); RaiseOnInsertRecordAsync(...); InsertAsync(...); }
else      { RaiseOnModifyRecordAsync();               ModifyAsync(...); }
```

Three properties matter and each of them has bitten this repository:

1. **`flag2` gates BOTH branches.** Insert and modify are decided by one comparison, then split.
2. **The gate is evaluated BEFORE the page's own record triggers.** When it finds nothing,
   `RaiseOnInsertRecordAsync` / `RaiseOnModifyRecordAsync` are never reached — so a page whose
   `OnModifyRecord` has a side effect does not get that side effect for a write that is not
   happening.
3. **Both arms of the OR are value comparisons, not "was something assigned".**
   - `CompareAllNormalFields` walks `metaTable.NormalFields`, skips system fields, and compares
     `GetFieldValue` for equality.
   - `RecordImplementation.HasChangedFields` is `mutableRecordBuffer?.HasActualChangedValues()
     ?? false`, and that method is:

     ```csharp
     public bool HasActualChangedValues()
     {
         if (!IsDirty) return false;
         foreach (NCLMetaField modifiedField in GetModifiedFields())
         {
             if (modifiedField.FieldNclType == NavNclType.NavBlob) return true;
             if (!IsChangedValueSameAsOriginalValue(modifiedField.FieldIndex)) return true;
         }
         return false;
     }
     ```

     A field that was assigned its own current value is *modified* but not *changed*, so it does
     not make this return true. (A Blob is the documented exception: it short-circuits to true
     without comparing, because comparing blob contents is not something BC does here.)

Property 3 is what #3055 asked about and what the issue explicitly declined to guess, having
read the two callers but not `HasActualChangedValues`'s body.

### <a id="the-comparison-is-against-the-loaded-row"></a>The comparison is against the loaded row, not against a history of assignments

`OldRecord` is the before-image the form snapshotted when it loaded the row. So the question BC
asks at save time is "do the row's values differ from the ones it was read with", **not** "did
any assignment along the way differ at the moment it happened".

Those two rules disagree on exactly one observable shape — write a different value, then write
the original back:

| what the AL does | "values differ from load" (BC) | "some assignment differed" |
|---|---|---|
| `SetValue('changed')` | writes | writes |
| `SetValue('original')` on a field already `'original'` | no write | no write |
| `SetValue('moved')` then `SetValue('original')` | **no write** | writes |
| `SetValue('original')` then `SetValue('final')` | writes | writes |

Row 3 is the discriminator, and it is covered by
`SetValue_ThereAndBackAgain_RunsNoOnModify` in
`tests/runner-extras/testpage-same-value-modify`.

## <a id="what-the-runner-does"></a>What the runner does

`LiveNavTestPage` in `AlRunner/Patches/MockTestPage.cs` coalesces per row: a control assignment
calls `MarkEdited`, which sets `_pendingNewRow` or `_pendingModify`, and the flush points
(`FlushRow`, called on a cursor move and on close) turn that into at most one write per row.
Both flush halves consult `RowValuesChangedSinceLoad()`, which is
`!record.CompareAllNormalFields(record.OldRecord, null)` — BC's own comparison, on BC's own
record object.

`fieldsInitializedFromFilters` is passed as `null` deliberately. In `CompareAllNormalFields`
that set *forces* a difference rather than excluding one, so passing it would report every
filter-stamped row as changed and save it. Which of the two `SaveRecordAsync` overloads the
close path behaves like was settled by measurement rather than by reading: corpus CU60648
`New_NothingTouched_IsDiscardedWhenTheCardCloses` does `New()` on a part whose linked field IS
in the primary key — so the filter stamp definitely happened — and real BC 27.0 through 28.4
still reports the row gone. That is only possible with
`detectChangeFromFieldsInitializedFromFilters: false`, which is what the no-argument
`SaveRecordAsync()` passes.

### History

The insert half landed first (#2869). The modify half was deliberately left out at that time,
because the OR's second arm made it unclear whether a same-value write was let through, and
`_pendingModify` is set by any assignment. #3055 split that question out rather than guessing at
it; reading `HasActualChangedValues` answered it, and corpus PR #271 put it in front of eight
real service tiers.

## <a id="what-measured-it"></a>What measured it

**Upstream, on real BC** — corpus codeunit 60411 `"SVM Tests"`
(`tests/al-language/handlers/TestPageSameValueSetValue_Tests.al`), corpus PR #271. Three tests,
each asserting a concrete integer count of `OnModify` firings, with a differing-value control
that must read exactly 1 so a zero-firing implementation cannot pass the suite vacuously.

**Surface is unpatched.** The corpus tier installs ~30 numbered patches into BC's assemblies at
startup, and on a UI surface a green can be a measurement of the patch rather than of BC
(`ask-the-corpus-before-claiming-bc-behavior.md`). Checked against
`src/StartupHook/StartupHook.cs` in `StefanMaron/MsDyn365Bc.On.Linux` at the time of writing:
no patch touches `NavForm.SaveRecordAsync`, `CompareAllNormalFields`, `MutableRecordBuffer` or
the record-modify path. The nearest UI patch is #21 (`NavOpenTaskPageAction.ShowForm`), which
concerns opening a page from an action and not the save path.

**Locally, in this repository** — `tests/runner-extras/testpage-same-value-modify`, six tests
on every CI leg. Four of them are controls that pin the instrument itself: a direct
`Modify(true)` must count 1 (so the tally can observe `OnModify` at all), and an open-navigate-close
with no assignment must count 0 (so a 0 elsewhere cannot come from a page that never saves).

Measured before the fix, on the runner: 1 / 1 / 1 for differing, same-value, and
same-then-differing — i.e. the runner coalesced correctly but gated on "was something assigned".
Removing the gate again after the fix re-fails exactly the two same-value tests and leaves the
four controls green.
