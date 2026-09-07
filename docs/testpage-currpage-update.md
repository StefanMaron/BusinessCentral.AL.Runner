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

Only `Update` means "re-load the form". Arming on `RecordSaved` as well would refresh after every
`CurrPage.SaveRecord`, which no measurement covers.

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
