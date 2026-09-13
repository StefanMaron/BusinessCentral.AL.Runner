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
  uses leaves the buffer **clean for a subsequent write** — it does *not* claim the page stays
  usable in every respect. See the next section: on 27.x it does not.

## Closing the page afterwards is version-split, and the corpus says so

The claim above — that the buffer is restored — is green on all eight cloud legs. **Closing
the page after a refused write is not uniform**, and an earlier revision of the corpus arm
overstated it by folding the two together.

Measured, corpus run
[`34328827788`](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/actions/runs/34328827788):

| legs | a refused write, then a **successful** one, then `Close()` |
|---|---|
| 28.0, 28.1, 28.2, 28.3, 28.4 | closes cleanly |
| 27.0, 27.3, 27.5 | raises `The record that you tried to open is not available. The page will close or show the next record.` |

Three of eight, unanimous within the 27.x family and deterministic.

**What the failure was not.** The `Assert.AreEqual` on the trace passed on all eight. Two
independent signals say so: the stack frame is `Test Runner - Mgt.RunTests` with **no `Assert`
codeunit frame** (a real mismatch shows one, and shows the expected/actual pair), and the
message is a UI message rather than a value comparison. The leg summary reads
`3211 total, 3210 passed, 1 failed`, so the suite reached the test phase and the other arms
executed.

**What discriminates it.** All three of the failed-write arms do `asserterror` then `Close()`,
and two of them pass on those same three legs. The only structural difference in the failing
one was the extra **successful write following the refused one**. So the split is not "a
refused write leaves the page unusable on 27.x" — that is refuted by the two passing arms on
the same legs — but the narrower "a refused write *followed by a successful one* leaves 27.x
unable to close the page".

**How the corpus states it now.** Two arms rather than one:

- `AWriteAfterARefusedOneTracesFromTheRestoredBuffer` asserts the trace and **does not close
  the page** — green on all eight.
- `APageIsStillClosableAfterARefusedWrite` closes the page after a refusal with no second
  write — green on all eight.

The version split itself is recorded in a comment at the arm, with the run id, and is
deliberately **not** asserted with a version branch: a test that branches on the platform
version to pick an expected value records a split rather than testing anything. Neither
version's answer is asserted as the correct one.

**Not settled: whether the 27.x behaviour is BC or the Linux image.** The corpus tier is
`MsDyn365Bc.On.Linux`, which patches BC, and the message is a client/server resource string
that is not in `Microsoft.Dynamics.Nav.Ncl.dll` — so it cannot be read with the decompiler
from the runner side. The route to settle it is the `run-nightly-windows` label on the corpus
PR, per `.claude/rules/ask-the-corpus-before-claiming-bc-behavior.md`. Until that runs, the
table above is what the tier answered and nothing here claims more than that.

**Nothing in the runner changed for this.** The runner's restore is green on both families;
this section documents a corpus-side scoping decision, not a runner behaviour.

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

Two tests pin the two spellings, and they are deliberately **asymmetric**:

- `TheRecBoundWritePathSnapshotsInline` reads `LiveNavTestField.Write`'s own body **only** and
  requires a direct `Snapshot` call, plus the absence of `RunRestoringOnRefusal`.
- `ThePageVariableWritePathWrapsItsWrite` searches the setter, its nested closure types and
  any lifted local function, because that setter legitimately hands a lambda to
  `RunRecordingRefusal` and so makes no direct call at all.

The asymmetry is the claim: one path must call inline, the other may call through a closure.

**Why the narrow arm is narrow.** A first version searched nested types for both, and a
reviewer found the hole: a **local function** does not compile into a nested type. Measured on
net8.0 — a lambda body becomes `<>c.<M>b__1_0` inside a nested type, a local function becomes
`<M>g__Local|0_0` as a method on the *declaring* type. So re-wrapping `Write`'s body in a
local function would have kept the old test green while hiding the ordering markers, leaving
the hazard caught only by the ordering test failing for a reason that does not name it.
Verified by applying exactly that rewrite: the old shape passed, the current one fails.

## Insert on focus

When a started new row (`OpenNew()`, `New()`, or a draft line a write promoted) is written to
the table is decided by focus, not by the key being complete (issue #4062).

**What BC does.** Read from the client, `Microsoft.Dynamics.Nav.Client.UI` 28.4:

- `AutoInsertPattern` is bound only when the page's `DelayedInsert` is false.
- `OnActiveControlChangedOnDraftRow` inserts the draft row (`TransactionManager.Save(row,
  SaveDraft)`, no changed-values gate) when focus moves from a field control to a field or group
  control that is **not a key control**, the form is editable and has no validation errors.
- `ActiveControlChanged` ignores a change that also changed the row, unless the page is bound
  to a single entity (a Card). So on a repeater, focus arriving on a new line from another line
  does not insert.
- `TestPageProxy.ActivateControl` does nothing for the control that already has focus, even
  after the cursor moved to another row, so focus stays on the old row until a different
  control takes it.
- `TestFieldProxy.Value`'s setter calls `Activate()` before it writes, so `SetValue` on a
  non-key control inserts first and writes second. Reading `Value` does not activate.
- `TestPageProxy`'s constructor activates the form's initial control
  (`InitialActiveControlStrategy`: first visible editable QuickEntry field, then first editable
  key field in key order, then first editable field; a repeater skips the QuickEntry step).

**What measured it.** Corpus codeunit 60576 "TPBK Tests" (StefanMaron/BusinessCentral.AL.Language.Tests#340):
a blank-key Card row is numbered by `OnInsert` right after the first non-key `SetValue`, and
`OnInsert` sees that control still blank; `Activate()` alone inserts on a non-key control and
not on the key; `DelayedInsert = true` waits for `Close()`; a List behaves the same, and after
`New()` a List inserts the second line only once focus moves between two of that line's
controls (the first revision of that test assumed one move was enough; three legs said no). It also agrees with the two older claims: typing
only the key of a Card inserts nothing (60844), and a List insert sees the next control blank
(60636).

**How the runner does it.** `LiveNavTestPage.ActivateControl`, called from
`LiveNavTestField.Activate()` and at the start of `LiveNavTestField.Write`;
`FocusInitialControl` at the end of `RunnerTestPageState.MarkOpened`; a row change is
`InsertEmptyRow` bumping `_rowEpoch`. `AlRunner.Tests/TestPageInsertOnFocusTests.cs` has one AL
test per rule, and removing any one rule reds only its test.

**Not modelled.**

- **Parts.** A linked part keeps #3441's key-complete insert (`InsertOnCompletePrimaryKey`).
  In BC a control in a part moves focus across forms (`HostedForm_ActiveControlChanged`
  inserts the *host's* draft row), which the runner does not track.
- **Page-variable controls** do not count as field controls either way; no corpus test
  measures them.
- **Actions.** BC's `TestActionProxy.Invoke` activates the action control too, so a field
  control activated right after an action has no field control to come from and does not
  insert. The runner does not move focus on an action.
- BC skips activating the control that already has focus per TestPage *session*
  (`session.FocusedControl`); the runner skips it per page.

## Sister documents

- `.claude/rules/ask-the-corpus-before-claiming-bc-behavior.md` — why the tier's eight legs
  outrank reading the AL
- `.claude/rules/bc-behavior-tests-go-upstream.md` — why the proving arms live in the corpus
  and only the mechanism is pinned here
