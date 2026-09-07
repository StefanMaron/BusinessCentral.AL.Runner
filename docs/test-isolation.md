# Test isolation in `AlRunner.Tests`

xunit runs each test class as its own collection and runs collections **in parallel** by
default (`parallelizeTestCollections=true` — see `xunit.runner.json`). Anything process-wide
that a test mutates and then reads back is therefore shared with whatever else is running at
that moment.

The repository fences these with `[CollectionDefinition(Name, DisableParallelization = true)]`
collections, and pins each fence with a guard test that fails the build when a class forgets to
join. Two such areas exist today.

| shared state | collection | guard |
|---|---|---|
| the AL parse statics on `RecordPatches` (`_parsedTables`, …) | `RecordPatchesSerialCollection` | `ParserStaticsIsolationGuardTests` (#1712) |
| the process-wide `Console.Out` / `Console.Error` | any non-parallelizable collection | `ConsoleSwapIsolationGuardTests` (#2913) |

Both guards are stopgaps that make forgetting loud. The real fixes — returning parse results
instead of publishing them (#1712), and an injectable log sink instead of touching `Console`
(#2913, option 2) — remove the shared state rather than fencing it, and are still open.

## Console swapping

A test that swaps `Console.Error` for a `StringWriter`, runs something, reads the sink and
restores in a `finally` owns that writer across a window. Two classes doing it at once clobber
each other: one class's `finally` restores the real console while the other is still writing to
its sink, so the line lands in the wrong place. The symptom is output that appears to have been
swallowed — which reads exactly like a filter bug, and did: #2874 was diagnosed twice before the
cause was found, because the failing assertion was about an exempt tag that had nothing to do
with the change that surfaced it.

It is load-dependent. CI runners pick a different degree of parallelism than a dev box, so it
surfaces on some legs and not others — the same signature as #1696.

### The inventory

Measured on `AlRunner.Tests` at the time of #2913, by scanning for `Console.SetOut` /
`Console.SetError` / `Console.SetIn` calls. Sixteen source files hold at least one; the issue's
own table listed five, because six more classes had been added in the interim — which is the
argument for a guard rather than a list.

**Already fenced when #2913 was filed:**

| class | collection | why that one |
|---|---|---|
| `LogUserFacingTagsTests` | `ConsoleFilterSerialCollection` | calls `Log.Install()` |
| `LoudDiagnosisReachesTheUserTests` | `ConsoleFilterSerialCollection` | calls `Log.Install()` |
| `CorruptSidecarLoudnessTests` | `ConsoleFilterSerialCollection` | calls `Log.Install()` |
| `DepsIsADistinctCategoryTests` | `ConsoleFilterSerialCollection` | calls `Log.Install()` |
| `ProvisionGapLogTests` | `RecordPatchesSerialCollection` | `ProvisionGapLog` is process-global |
| `BcAppRegistrationEpochInvalidationTests` | `RecordPatchesSerialCollection` | parse statics |
| `DependencyMetadataMemoInvalidationTests` | `RecordPatchesSerialCollection` | parse statics |
| `DependencySymbolReadFailureTests` | `RecordPatchesSerialCollection` | parse statics |
| `PermissionSetSymbolReadFailureTests` | `RecordPatchesSerialCollection` | parse statics |
| `DependencyPageMetadataXmlTests` | `CacheRootsSerialCollection` | cache roots |
| `TestDataLazyLoadPolicyTests` | `BcCompilerSharedReferenceCollection` | shared references |

Every one of those collections sets `DisableParallelization = true`, so all eleven were already
safe — including the seven that are in a collection for a reason unrelated to the console.

**Unfenced, and fixed by #2913** — all six joined `ConsoleFilterSerialCollection`:

| class | swap sites | what it captures |
|---|---|---|
| `AlCallStackCaptureNoFallbackTests` | 123, 150 | stderr, asserted |
| `CacheKeyUnhashableDependencyTests` | 252, 255 | stderr, asserted |
| `HotPathHookCostTests` | 61-62, 67-68 | stdout+stderr, asserted empty |
| `InstallTriggerAsyncObservationTests` | 108, 116 | stderr, asserted |
| `PhaseLogTests` | 402, 407 | stderr, asserted |
| `WatchSourceTests` | 70, 80, 254, 267 | stderr, asserted |

The last four of the six were not in the issue's table: `CacheKeyUnhashableDependencyTests` and
`InstallTriggerAsyncObservationTests` were added to the suite after #2874 was written, and
`InstallTriggerAsyncObservationTests` swaps from a private helper method rather than from a test
body.

### Why "any non-parallelizable collection" and not a named list

#2913 raised an objection to fencing: a class can carry only one `[Collection]`, and
`ProvisionGapLogTests` needs `RecordPatchesSerialCollection` for `ProvisionGapLog`'s
process-global state, so it "cannot simply join the console one". The issue concluded from this
that fencing would require merging `ConsoleFilterSerialCollection` into
`RecordPatchesSerialCollection` (its option 1), at the cost of parallelism across ~21 already
serial classes.

That does not follow. What a console swapper needs is **exclusive scheduling**, which is exactly
what `DisableParallelization = true` means — not membership of one particular collection. xunit
runs every non-parallelizable collection serially, one at a time, so a class in *any* of them
holds the console alone for its duration. Eleven of the seventeen swapping classes were already
safe on that basis before this change.

So `ConsoleSwapIsolationGuardTests` derives the accepted set by reading the
`DisableParallelization` flag off every `[CollectionDefinition]` in the assembly as data. No
collections were merged, no test lost a fixture it depends on, and no allowlist needs
maintaining. It also means the flag cannot be flipped off quietly: doing so fails the guard for
every class that was relying on that collection, rather than reopening the race silently.

`ParserStaticsIsolationGuardTests` hardcodes two collection names and pins their flag with
`EverySerialCollection_ReallyDisablesParallelization`. Deriving the set is the same idea with
nothing left to pin — the two guards are otherwise deliberately the same shape, and the
source-scan plumbing is shared by copy.

### What the guard does not catch

The detector scans source, because what makes a class dangerous is a *call* and reflection
cannot answer "does this type's body call this method" without reading IL. Reflection is used
for the half it is exact about: which types are test classes, and what `[Collection]` each
carries.

- A swap performed by a helper in a **different file** on the test class's behalf. The scan is
  per file, so the call has to be in the file that declares the class.
- A swap reached through reflection, or an indirection that never spells the call.
- A test that spawns the runner as a **subprocess** and reads its stdout. Correctly not caught —
  each subprocess has its own `Console` and cannot race this process's.

The per-file granularity has one visible consequence: a file holding two classes attributes the
swap to both. `ProvisionGapSummaryTests.cs` held `ProvisionGapSummaryTests` (no swaps) alongside
`ProvisionGapLogTests` (eight), so the guard flagged the first for the second's calls. #2913
split `ProvisionGapLogTests` into its own file rather than adding an exemption, so the file
boundary matches reality and the answer is exact for both. Prefer that fix to an exemption when
it comes up again.

### Rejected designs

- **Merge the two collections** (#2913 option 1). Rejected: unnecessary once "fenced" is defined
  by the flag rather than by a name, and it would serialise ~21 classes that have no console
  involvement.
- **Drop the swap and take the noise.** #2874 did this for `CorruptSidecarGapSummaryTests`,
  correctly, because that class only redirected to silence output it never asserted on. It is
  not general: five of the six classes fixed here read the sink back and assert on it, so
  dropping the swap would delete the assertion. Still the better change where it applies.
- **A timing or repetition test.** A race that reproduces under load on CI and not on a dev box
  cannot be proven absent by a loop that passed. The structural assertion holds the invariant
  instead: it fails at test time when a class swaps the console without a fence, whatever the
  scheduling happened to be.
