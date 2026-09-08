# When one object fails to emit, what happens to the rest of the module

BC's `Compilation.Emit` is atomic per module: one AL object that cannot be emitted takes the
whole module down. `BcCompiler`'s emit-retry loop works around that by dropping the offending
object and recompiling the survivors. The question this document settles is what the runner
does with that recovered module — issue #3476.

Short answer, since #3476: it decides **per dropped object**, and it runs the survivors only
when every dropped object is one nothing surviving can reach. Whatever it decides, the run
still fails (exit 3), still names what was dropped, and now also counts the tests that did not
run.

## Why the old answer cost 3,555 tests

Before #3476 there was one carve-out — an all-profile exclusion (#2238) — and everything else
emptied the module:

```csharp
sources = Array.Empty<EmittedSource>(); // do not run a module that is missing objects
```

That is a defensible default and its reasoning is sound: a dropped object may be a library
codeunit other tests call, and running without it produces failures that look like runner gaps
but are really "the object is not there".

It is also expensive, because in Microsoft's buckets the dropped objects are test codeunits
whose only caller is the test runner. Measured on the two buckets in #3476, with the missing
DotNet type reproduced on the codeunits the issue names (BC 28.4.53241.53955, no `--test-data`):

| bucket | before | after |
|---|---|---|
| `Tests-Misc` | COMPILE FAIL, **0 tests** | **3,215 tests** — 1,509 pass, 1,680 fail, 20 error, 6 skipped |
| `Tests-Integration` | COMPILE FAIL, **0 tests** | **340 tests** — 80 pass, 232 fail, 0 error, 28 skipped |

Both remain exit 3 and both report as `partial` with the suite error named. The totals match a
control run of the same buckets with nothing broken (3,215 and 340), which is the check that
says nothing was quietly discarded — the tests that did not run are inside the total, as
`skipped`.

## What decides it

`AlRunner/ProgramSupport/ExcludedObjectTriage.cs`, one verdict per dropped object:

| the dropped object | verdict |
|---|---|
| a profile | droppable — #2238, unchanged: no executable AL, no `[Test]` procedures |
| a codeunit with `Subtype = Test` that no surviving source in the module names, by name or by object id | droppable |
| a codeunit with `Subtype = Test` that some surviving source does name | **refused**, naming the files |
| a codeunit without `Subtype = Test` | **refused** — a survivor may call it |
| anything else (table, page, report, …) | **refused** |
| an object whose own source cannot be re-read or parsed | **refused** |

Every object must be droppable for the module to run. One refusal refuses the module, and the
run says which refusal it made — "refused because a survivor names it" and "refused because
nothing identified what was dropped" are different facts, and only the first is about the AL.

## Why a textual scan is enough here, and where it is not

The compiler already does most of this work, and that is the load-bearing part of the argument.

**The retry loop cascades.** A surviving object that names a dropped object no longer binds, so
the next round excludes the referrer too. Measured on
`AlRunner.Tests/Fixtures/EmitExclusion`: adding `Broken: Codeunit "Emit Excl Broken"` to the
healthy codeunit moved the run from 1 excluded object to 2. So by the time the retry succeeds,
the survivor set is already free of unresolved **compile-time** references to anything dropped
— that is a guarantee from BC's own binder, not from a regular expression.

**An object-id reference is not compile-checked, and survives that cascade.** Same fixture with
`Codeunit.Run(60620)` in the healthy codeunit: still 1 excluded object, and the survivor
compiles cleanly while calling a codeunit that is no longer in the module.
`AlRunner.Tests/Fixtures/EmitExclusionReferencedById` pins that case.

So the scan exists to cover the residue the binder cannot: an object id written as an integer
literal. It also re-checks names, which is redundant with the cascade and deliberately kept —
redundancy here costs a refusal, which is the old behaviour, while a gap costs a module that
runs while missing something.

It is textual, so it matches inside comments and string literals too. That is the intended
direction: a false positive refuses a module that might have been safe; a false negative runs
one that is not.

## The cache, and the way this went wrong the first time

Keeping the survivors makes the recovered module compilable, and a compiled module is
cacheable. The AL-output cache stores the assembly and nothing else, so a later HIT skips Emit
— and with it the exclusion branch, the suite error, the skipped results and exit 3. Measured
during #3476: the second run of the same fixture in one `dotnet test` invocation reported
`Tests: 1 total, pass: 1` and **exit 0**, byte-identical to a clean run.

Before #3476 that could not happen, because `sources` was cleared and nothing was ever
compiled to cache. The fix withholds the cache entry for a module that is missing objects
(`cachePath = null`, the same lever #2954 uses for its NOKEY path), so the loss is re-reported
on every run. `EmitExclusionLoudnessTests.ExcludedTestCodeunit_SecondRunOffAWarmCache_StillReportsTheLoss`
pins it with a private cache directory so the pair is a real cold-then-warm run.

The all-profiles path (#2238) does still write a cache entry, so a warm run of a
profile-excluded module loses the informational EMIT-EXCLUDED line. That path is deliberately
not a failure — no exit code and no test result depends on it — so the cost is one message
rather than a wrong verdict, and it is left alone here rather than changed in passing.

## What did not change

- The exclusion is still reported at default verbosity, with the AL diagnostics that
  identified each object (#2207, #2949).
- The run still exits 3 and the bucket still reports as `partial` with its suite error
  repeated verbatim in the summary (#2762).
- `--tdd` is untouched: it keeps the survivors, as it always did, and reports the dropped
  objects' tests as **FAILED**, because a red test is the point of that flag. The default path
  reports them **SKIPPED**, because nothing measured whether they would pass.
- `--server`'s own EMIT-EXCLUDED guard is a separate code path and still refuses outright.
