# `CurrPage.Update()` under a TestPage

Issue #3373. What BC does, what the runner does about it, and what is deliberately not
reproduced.

## The mechanism

`NavForm.UpdateAsync(bool)` calls `UpdateCoreAsync`, which is two steps:

```csharp
if (saveRecord) { await SaveRecordAsync(); updateType |= NavFormUpdateTypes.RecordSaved; }
if (this.UpdateRequest != null) this.UpdateRequest(this, new UpdateRequestEventArgs(updateType));
```

Both run unmodified in the runner. The first is why `CurrPage.Update(true)` writes the row here
correctly and always has. The second goes nowhere: on a real service tier the client subscribes
to `UpdateRequest` and answers it by re-loading the current row, which is what raises the page's
`OnAfterGetRecord` and `OnAfterGetCurrRecord`. Headless there was no subscriber.

`AlRunner/Patches/RunnerPageInstance.PendingUpdate.cs` subscribes in the client's place. The
request arms a flag; the flag is realised when the outermost AL trigger returns.

## Why only `NavFormUpdateTypes.Update` arms it

BC raises the same event from two places with different flags:

| raiser | flags |
|---|---|
| `SaveRecordAsync` | `RecordSaved` |
| `UpdateCoreAsync` | `Update`, plus `RecordSaved` when `saveRecord`, plus `UpdateParent` when `UpdatePropagation == Both` |

Only `Update` means "re-load the form". Arming the REFRESH on `RecordSaved` as well would re-run
the page's triggers after every `CurrPage.SaveRecord`, which no measurement covers.

`RecordSaved` is not ignored, though: it drives the before-image half of what the client does
after a save, and nothing else. `RunnerPageInstance.RefreshBeforeImageAfterSave` takes
`OldRecord.ALAssign(SourceTable)` — the tail of `AfterGetCurrRecordAsync`, which a real client
reaches by re-reading the row — without raising a single trigger. Issue #3440: without it a
second write in one page session reported the value from before the first write as its `xRec`.

## The measured trigger orders

BC 28.4.53241.0, in a container, on a Card whose FactBox column is fed only from the host's
`OnAfterGetCurrRecord`. `HostAGR` = `OnAfterGetRecord`, `HostAGCR` = `OnAfterGetCurrRecord`,
`SetHeader` = the procedure the host calls on the part. The trace is reset immediately before
the mechanism, so what follows is the delta.

| arm | BC 28.4 |
|---|---|
| `SetValue` whose `OnValidate` calls `CurrPage.Update(true)` | `ValidateBegin;ValidateEnd;HostAGR;HostAGCR;SetHeader;` |
| `SetValue` whose `OnValidate` calls `CurrPage.Update(false)` | `ValidateBegin;ValidateEnd;HostAGR;HostAGCR;SetHeader;` — the same pair |
| `SetValue` with no `CurrPage.Update` anywhere | no extra `HostAGCR` |
| action whose `OnAction` calls `CurrPage.Update(true)` | `HostAGR;ActionBegin;ActionEnd;HostAGR;HostAGR;HostAGCR;SetHeader;` |

Two things follow, and both are why the fix takes the shape it does:

1. **The refresh lands after the calling trigger returns**, never between `ValidateBegin` and
   `ValidateEnd`. That is the depth counter in `EndTrigger`.
2. **BC raises the pair, `OnAfterGetRecord` then `OnAfterGetCurrRecord`** — which is exactly what
   `RunnerPageInstance.RaiseOnAfterGetRecord` already does. The `Update(false)` row was measured
   separately, with `OnAfterGetRecord` instrumented, precisely because the first pass through
   these arms recorded only `OnAfterGetCurrRecord` and so could not have told the two apart:
   `saveRecord` changes whether the row is written, not which triggers the refresh raises.

## A row the table does not hold gets no refresh triggers

Issues #4698, #4712 and #4727. When the refresh is realised and the table does not hold the
page's current row, `EndTrigger` raises neither `OnAfterGetRecord` nor `OnAfterGetCurrRecord`.
For an unsaved new row, the row already got its `OnAfterGetCurrRecord` when it became current
(`LiveNavTestPage.NewRowBecameCurrent`), and there is no stored row to re-read. For a deleted
row, BC re-reads nothing for it either (next section).

The shape is Base Application's "User Card" (page 9807): its pageextension 9807 calls
`CurrPage.Update(false)` from `OnAfterGetCurrRecord`, and its `OnAfterGetRecord` runs
`Rec.TestField("User Name")`, which the blank row `OpenNew` starts fails.

`LiveNavTestPage` answers the question through `RunnerPageInstance.IsCurrentRowNotStored`: a
key lookup does not find the row. That keeps a trigger that inserts the row itself and then
calls `CurrPage.Update` (Customer Card's insert from a template) on the full pair. Until #4727
the answer also required the row to be a pending insert; corpus 67300 showed a deleted row gets
no refresh triggers either, so that conjunct went. A temporary source table cannot use that lookup, because
the stored table never holds a temporary row, so its row is asked of its own buffer through
BC's `NavRecord.HasBeenInserted`, whose temporary branch is `ExistsAsync(ALRecordId)` (#4712).

Measured by corpus codeunit 60893 "ALT Page Update New Row Test" and, for a temporary source,
corpus codeunit 60872 "ALT Page Update Temp New Test". On corpus PR #434's first run, all nine
cloud legs read the temporary-source `OpenNew` trace as `AGCR;`: one `OnAfterGetCurrRecord`, and
nothing from the refresh. 60893's `OpenNew_TraceIsOneOnAfterGetCurrRecord` asks the same of a
normal source. Runner-side: `AlRunner.Tests/CurrPageUpdateNewRowTests.cs`.

`CurrPage.Update(false)` from a field's `OnValidate` on a DelayedInsert row that is still
unsaved takes the same guard, and BC agrees: corpus codeunit 67300 "ALT Page Update Gone Test"
reads the trace as `Validate;` and finds no row saved, with the key set and without, on every
cloud leg; the same page on a stored row reads `Validate;AGR:A;AGCR:A;` (#4727).

## A Card whose row is gone closes when an action returns

Issue #4727. After an action on a Card returns and the Card's stored row is no longer in the
table, BC's client closes the page: every later call on the TestPage variable, `Close()`
included, raises "The TestPage is not open." It does not matter whether the action called
`CurrPage.Update`, or whether the action or the test deleted the row. A List in the same
shape moves to a neighbouring row instead; see the next section.

What BC raises, on every cloud leg: `AGR:A;ActionBegin;ActionEnd;AGR:B;ClosePage;` with a
neighbour `B`, `AGR:A;ActionBegin;ActionEnd;ClosePage;` without one. So `OnClosePage` runs, and
nothing runs for the deleted row after the action, `OnAfterGetCurrRecord` included. The
`OnAfterGetRecord` calls are the client re-reading rows, which the runner does not reproduce
(see "What is deliberately not reproduced").

The untouched row `OpenNew` starts is not in the table either, but it is not deleted: an action
on it leaves the Card open, showing the blank row. So does a Card opened with `OpenEdit` that
never showed a stored row: an empty table, or a filter matching nothing.

`LiveNavTestAction.Invoke` calls `LiveNavTestPage.CloseIfCurrentRowDeleted` after the
`OnAction` trigger. When the page is a Card, the row was not a pending new row before the action,
the buffer carries a `SystemId` (it was read from, or inserted into, the table; the blank row of
an empty table or a no-match filter has none), and a key lookup does not find it, that raises
`OnClosePage` and detaches the TestPage
(`MarkDetached`, the same state a `Close()` leaves). `OnQueryClosePage` is not raised: the
measured page declares none. A temporary source is unmeasured and stays open. So is an action
that renames the current row through another record variable: the key lookup misses and the
runner closes the Card; what BC does there is unmeasured.

Measured by corpus codeunit 67300; runner-side: `AlRunner.Tests/TestPageDeletedRowCloseTests.cs`.

## A List whose row is gone moves to a neighbouring row

Issue #4747. In the same shape a List page stays open and moves: to the next row in the page's
key order, else the previous row when the deleted row was the last, else the blank new-row line
when it was the only row. The row moved to raises `OnAfterGetRecord` and `OnAfterGetCurrRecord`;
the deleted row raises neither. `CurrPage.Update(false)` in the action is not what moves it: an
action that only deletes moves the page the same way.

BC's first run read `AGR:A;ActionBegin;ActionEnd;AGR:B;AGR:B;AGCR:B;AGR:B;AGR:B;AGCR:B;` for rows
`A` and `B` with `A` deleted. The corpus pins the row shown and the last trigger, not the count;
the runner raises the pair once.

`LiveNavTestPage.MoveOffDeletedRow` does it, from the same check as the Card close: it finds with
`=><` from the key the buffer still holds, and falls back to `EnterNewRowLine`. Other page types,
and a temporary source, are unmeasured and left where they are.

A List that declares `OnFindRecord` moves through that trigger. Corpus arm
`List_DeletedByAction_OnFindRecord_PicksTheRow` (page 67302: rows `A`, `B`, `C`, the action
deletes `B` and from then on the trigger answers the first row) measured it on every cloud leg,
27.0 through 28.5, in corpus run 36249132626 (corpus head `75a7e80c`), identically:

```
AGR:C;AGR:A;Find:=;AGR:A;AGCR:A;AGR:C;Find:=>;AGR:A;AGR:C;AGR:A;Find:=;AGR:A;AGCR:A;
```

So BC calls `OnFindRecord` three times after the action -- `=` for the gone row, `=>` for the
rows from there on, `=` again for the row it settled on -- and shows `A`, where the default
re-read of a deleted middle row lands on `C`. The arm pins the three `Which` strings in order,
the row shown, and `OnAfterGetCurrRecord` for `A` last; `MoveOffDeletedRow` makes the same three
calls. It does not reproduce the `OnAfterGetRecord` reads of the other rows around them.

A trigger that answers `false` to the first `=` -- the common pass-through
`exit(Rec.Find(Which))` does, on the deleted key -- gets the same `Which` strings. Corpus arms
`List_DeletedByAction_PassThroughFind_*` (the same page's `DeletePassThrough` action; #4760)
measured, on every cloud leg of corpus run 36259501382:

| rows, deleted | `Which` strings after the action | row shown |
|---|---|---|
| `A`, `B`, `C`, delete `B` | `=`, `=>`, `=` | `C` |
| `A`, `B`, delete `B` | `=`, `=>`, `=` | `A` |

So `=>` is asked whatever `=` answered. When it finds nothing either, the page still lands on the
previous row without a further `Find`, and the third `=` is asked for that row.
`MoveOffDeletedRow` steps back with a direct `Find('<')` on the record, not through the trigger
-- a page that also declares `OnNextRecord` is unmeasured there. The only-row arm pins what
happens with no row left: `=` and `=>`, then the blank line.

Measured by corpus codeunit 67300's `List_DeletedByAction_*` arms; runner-side: the same test file.

## What is deliberately not reproduced

- **The action arm's extra `OnAfterGetRecord` firings.** BC produced three `HostAGR` around the
  action against the runner's one. They are client-side row-load churn; the runner raises the
  pair once. Nothing in the corpus pins the `OnAfterGetRecord` count, and pinning BC's would
  assert client behaviour the runner has no equivalent of.
- **`NavFormUpdateTypes.UpdateParent`.** BC ORs it in for a page declaring
  `UpdatePropagation = Both`, asking the client to refresh this page's HOST as well. The runner
  has no parent link to walk — a TestPage part reaches its host through the test's own variable,
  not through a field on the page instance. It is read as a no-op rather than refused, because
  the flag always arrives alongside `Update`, so this page's own refresh still happens and only
  the host's does not; throwing would turn a partial answer into no answer for every such page.
  Unmeasured.
- **A request that reaches a page with no trigger of its own on the stack.** This is the same
  divergence as `UpdateParent`, arriving by the other route. A **part** calls `CurrPage.Update`
  and BC propagates the request to its **host**'s form as well; the host has no trigger of its
  own running, so its `_triggerDepth` is 0.

  Such a request is dropped. Carrying it is the one clearly wrong option: the flag would
  survive to the end of the next, unrelated host trigger and raise an `OnAfterGetCurrRecord`
  there, attributed to a `CurrPage.Update` the host never made. Realising it immediately would
  be the faithful alternative, but that is the propagation behaviour above, which nothing has
  measured — so it is left undone rather than guessed at.

  `AlRunner.Tests/CurrPageUpdateRefreshTests`'s part-action arm reproduces this deterministically
  and goes RED (`ValidateBegin;ValidateEnd;HostAGCR;`) with the drop removed. It was first seen
  in a corpus run, on the host/part pair in
  `tests/al-language/.../handlers/TestPageTempPart_*` (pages 60805 and 60806) — but whether the
  host has subscribed by the moment the part fires depends on the host having run a trigger
  first, so that sighting is **not** a repeatable count and the C# arm is what pins the
  behaviour.

  `RunnerPageInstance.TryRaiseExtensionOnlyAction` needs nothing here: it is `static` and runs
  only where no page instance could be built at all, so there is no subscription and no flag.

  Set `AL_RUNNER_TRACE_PAGE_METADATA=1` to have each drop name its page id on stderr.

- **A `CurrPage.Update` issued from inside the refresh itself.** The subscriber drops a request
  raised while a refresh is running. Realising it would arm the flag again from inside the
  refresh and refresh again without bound — a hang. No measurement covers that shape.

## What measures it

Upstream corpus, `tests/al-language/pageupdate/`, codeunit 60496 `ALT Page Update Test` — four
tests, green on a real service tier. It carries no `Update(false)` arm: that row above was
measured in the container, and the corpus PR was already held awaiting unrelated `master`
failures when the measurement landed, so adding one is follow-up rather than a gap in the
claim. The control arm (`NoCurrPageUpdate_…`) is the one that makes
the others a statement about `CurrPage.Update`: its page differs only in the absence of the call.

Runner-side, `AlRunner.Tests/CurrPageUpdateRefreshTests.cs` pins the runner's own mechanism.
