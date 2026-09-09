# The TestPage write buffer: what a refused write leaves behind

Issue [#3640](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/3640).
Implementation: `AlRunner/Patches/TestPageWriteBuffer.cs`, called from both write paths in
`AlRunner/Patches/MockTestPage.cs`.

## The claim, and what measured it

Real BC **discards** the in-memory `Rec` mutations a page-driven write's triggers made, once
that write raises. A still-open page reads the values it held before the write, not the
partially-applied ones.

This is a service-tier measurement, not a derivation. Corpus PR
[#305](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/305) originally
carried an arm asserting the **opposite** — the runner's behaviour. All eight cloud legs
failed it, unanimously and deterministically (run `34319509704`, 7 PASS / 1 FAIL on each of
27.0, 27.3, 27.5, 28.0, 28.1, 28.2, 28.3, 28.4):

```
FAIL AnErrorInOnBeforeValidatePreventsTheBaseTriggerAndOnAfterValidate
     Assert.AreEqual failed. Expected:<before;> (Text). Actual:<> (Text).
```

| | `Card.Trace.Value()` after `asserterror Card.Name.SetValue('stop')` |
|---|---|
| real BC, 8/8 cloud legs | `''` — the mutation is gone |
| AL Runner, before #3640 | `'before;'` — the mutation survives |

The arm was removed from #305 rather than weakened, and the fixture's
`if Rec.Name = 'stop' then Error(...)` hook was left in deliberately so that stating the claim
would mean adding an arm rather than re-shaping the fixture. Corpus PR
[#309](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/309) is that arm.

## What the tier measured, and what it did not

Stated separately because the gap is where a future editor is most likely to over-reach.

**Measured:**

- A `Rec`-bound control whose pageextension `modify()` `OnBeforeValidate` mutates `Rec` and
  then raises. The mutation is not visible afterwards.
- The success path of the same control, on the same eight legs, reads
  `'before;page;after;'` — so trigger dispatch and ordering are right, and the divergence is
  isolated to what survives a *failed* write.

**Not measured, and therefore not asserted anywhere:**

- What a failed write leaves in a **page global** (a `PageVariableTestField` binding). The
  runner unwinds `Rec` on that path too, on the ground that a page-driven write is a
  page-driven write whichever way the control that started it is bound — but the page
  variable itself is left alone, because inventing an answer for it is exactly what
  `ask-the-corpus-before-claiming-bc-behavior.md` forbids.
- Whether a failed write also unwinds the **new-row promotion** — the key stamping that
  `_onBeforeEdit` performs before the value is validated (#2923). The runner snapshots
  *after* that step, so the promotion survives a refusal. That is a deliberate choice about
  an unmeasured question, not a claim.
- Whether the discard is a restore or a re-read from the database. Observationally the corpus
  cannot distinguish them for a row that was never written; the runner restores, and corpus
  arm `AWriteAfterARefusedOneTracesFromTheRestoredBuffer` pins that whichever mechanism BC
  uses leaves the page usable with a clean buffer.

## How the runner does it

`NavRecord.CopyRecord` is `internal`, so the snapshot is by value, field by field, through
BC's own public `GetFieldValue` / `SetFieldValue` pair. That pair is symmetric over
`recordImplementation` (minus media-info decoration on the read), and a `NavValue` is
immutable — `SetFieldValue` stores the instance and `GetFieldValue` hands the same one back —
so holding the reference is a faithful snapshot.

Only **Normal, active, non-timestamp** fields are snapshotted:

| excluded | why |
|---|---|
| FlowField | computed from other rows; writing one back stores a cached calculation as though it had been stored |
| FlowFilter | a filter, not a value |
| inactive / obsoleted | `GetFieldValue` answers `GetDefaultNavValue` for these, so restoring writes that default over the buffer |
| `FieldIndex == 0` | the row timestamp; `SetFieldValue` routes it to `SetRecordTimestamp`, not to the field store |

Two failure modes are handled by keeping today's behaviour rather than refusing:

- **The buffer cannot be read at all** (a stale or closed record — BC's `GetFieldValue`
  raises). The write runs unwrapped. Not unwinding is a smaller error than refusing a write
  BC allows.
- **One field refuses restoration.** Restoration is per field, so the rest of the row still
  goes back. A partially-restored buffer is the state this rule exists to remove, and it
  would be harder to diagnose than the un-restored one.

## Why `LiveNavTestField.Write` snapshots inline

`TestPageWriteBuffer` offers two spellings, and the two-part one exists for a single caller.

`AlRunner.Tests/TestPageNewRowLinePromotionTests` reads `LiveNavTestField.Write`'s **IL** to
pin the #2923 ordering: `_onBeforeEdit` before `ALValidateAsync`, `_onEdited` after it.
Wrapping that method's body in a lambda moves all three markers into a compiler-generated
closure, and the ordering test then finds none of them and fails for the wrong reason — which
is what happened while implementing #3640, before the shape was changed back.

So that one caller uses `TestPageWriteBuffer.Snapshot(record)` plus its own `catch`; every
other caller uses `RunRestoringOnRefusal`, which cannot get the try/catch wrong.
`TestPageWriteBufferTests.BothWritePathsUnwindTheirBuffer` pins both spellings in IL, so a
later tidy-up that collapses them fails there rather than silently making the ordering test
vacuous.

## Sister documents

- `.claude/rules/ask-the-corpus-before-claiming-bc-behavior.md` — why the tier's eight legs
  outrank reading the AL
- `.claude/rules/bc-behavior-tests-go-upstream.md` — why the proving arms live in the corpus
  and only the mechanism is pinned here
