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
| `SetValue` whose `OnValidate` calls `CurrPage.Update(false)` | one extra `HostAGCR`, same position |
| `SetValue` with no `CurrPage.Update` anywhere | no extra `HostAGCR` |
| action whose `OnAction` calls `CurrPage.Update(true)` | `HostAGR;ActionBegin;ActionEnd;HostAGR;HostAGR;HostAGCR;SetHeader;` |

Two things follow, and both are why the fix takes the shape it does:

1. **The refresh lands after the calling trigger returns**, never between `ValidateBegin` and
   `ValidateEnd`. That is the depth counter in `EndTrigger`.
2. **BC raises the pair, `OnAfterGetRecord` then `OnAfterGetCurrRecord`** — which is exactly what
   `RunnerPageInstance.RaiseOnAfterGetRecord` already does.

## What is deliberately not reproduced

- **The action arm's extra `OnAfterGetRecord` firings.** BC produced three `HostAGR` around the
  action against the runner's one. They are client-side row-load churn; the runner raises the
  pair once. Nothing in the corpus pins the `OnAfterGetRecord` count, and pinning BC's would
  assert client behaviour the runner has no equivalent of.
- **A `CurrPage.Update` issued from inside the refresh itself.** The subscriber drops a request
  raised while a refresh is running. Realising it would arm the flag again from inside the
  refresh and refresh again without bound — a hang. No measurement covers that shape.

## What measures it

Upstream corpus, `tests/al-language/pageupdate/`, codeunit 60496 `ALT Page Update Test` — four
tests, green on a real service tier. The control arm (`NoCurrPageUpdate_…`) is the one that makes
the others a statement about `CurrPage.Update`: its page differs only in the absence of the call.

Runner-side, `AlRunner.Tests/CurrPageUpdateRefreshTests.cs` pins the runner's own mechanism.
