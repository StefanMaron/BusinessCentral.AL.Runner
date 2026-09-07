# The AL recursion ceiling

The runner refuses an AL call chain past a fixed depth, the way BC does. This file records
what BC's number is, where the runner's copy of it comes from, and the measurement showing
that enforcing BC's number does not put the process at risk.

Code: `AlRunner/Patches/MethodScopePatches.cs` (`ResolveMaxRecursionDepth`, the guard in
`NavMethodScopeCtorReplacement`) and the call site in `AlRunner/BcRuntime.cs`.

## What BC does

`Microsoft.Dynamics.Nav.Runtime.NavMethodScope..ctor(NavApplicationObjectBase,
MethodScopeFlags, bool)`, decompiled from `Ncl.dll`, ends with two separate guards:

```csharp
if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
    ThrowStackOverflow("000004O");
StackDepth = checked(navMethodScope.StackDepth + 1);
if (StackDepth > 1000)
    ThrowStackOverflow("000004N");
```

They are not the same check and they raise different resources:

| guard | fires when | resource |
|---|---|---|
| `TryEnsureSufficientExecutionStack` | the CLR stack is genuinely close to exhausted | `000004O` |
| `StackDepth > MaxStackDepth` | the AL call chain is too deep, whatever the stack looks like | `000004N` |

The `1000` in the comparison is the type's own `private const int MaxStackDepth = 1000;`,
inlined by BC's compiler. `NavMethodScope` is the per-AL-frame execution unit, so `StackDepth`
counts AL scopes.

## The measured values

`MaxStackDepth` read out of `Microsoft.Dynamics.Nav.Ncl.dll` in every provisioned artifact
directory, covering all eight supported BC versions (2026-09-07):

| build | `MaxStackDepth` |
|---|---|
| 27.0.38460.53934 | 1000 |
| 27.3.44313.53909 | 1000 |
| 27.5.46862.48827 | 1000 |
| 27.5.46862.53931 | 1000 |
| 28.0.46665.54338 | 1000 |
| 28.1.49838.53910 | 1000 |
| 28.1.49838.54308 | 1000 |
| 28.2.50931.54319 | 1000 |
| 28.3.52162.54347 | 1000 |
| 28.4.53241.54346 | 1000 |

Unanimous, which is why a fallback constant is safe — and why it must not be the only
mechanism. This table is re-derived on every run by
`AlRunner.Tests/NavMethodScopeRecursionCeilingTests.EveryProvisionedBcBuildDeclaresTheFallbackAsItsMaxStackDepth`,
so a Microsoft change fails a test naming the build rather than sitting here going stale.

## Where the runner's number comes from

`ResolveMaxRecursionDepth` reads `MaxStackDepth` off the loaded `NavMethodScope` at startup,
next to the other reflection the ctor replacement needs. Two details it depends on:

- **`MaxStackDepth` is a `const`**, so it has no storage. `FieldInfo.GetValue` is not the
  right call; the value lives in metadata and comes back through `GetRawConstantValue`.
- Being a `const` also means BC's compiler inlined it into the ctor's IL, so the field and
  the number the real ctor compares against cannot disagree for a given build.

If the field is missing, renamed, no longer `Int32`, or non-positive, the resolver keeps
`FallbackMaxRecursionDepth` (1000) rather than throwing. A guard rail that cannot be read is
not a reason to fail the run — but a silent permanent fallback is how the previous wrong
value survived, so `MaxRecursionDepthResolvedFromNcl` records which path ran, and the tests
assert both directions.

## Why the runner enforces the depth counter and not BC's stack check

The runner Cecil-rewrites `NavMethodScope.ThrowStackOverflow` to a plain `ret`
(`AlRunner/Infrastructure/NclCecilRewrite.Runtime.cs`, block 4), because BC's stack check
uses a non-`NavMethodScope` sentinel that false-positives in the headless harness. So BC's
`TryEnsureSufficientExecutionStack` arm does not refuse anything here, and the runner's own
`[ThreadStatic]` counter is what enforces the ceiling.

That raises a fair question: with BC's stack guard disarmed, does enforcing a ceiling of 1000
rather than 500 simply move the failure into a real CLR `StackOverflowException`, which cannot
be caught and takes the process down? Measured, no — with a wide margin.

**Measurement (2026-09-07, BC 28.1 platform apps, `net8.0` Release).** A probe bundle whose
recursive procedure carries 14 locals (5 `Integer`, 5 `Decimal`, 4 `Text[250]`), all assigned
each frame, so the CLR frame is far larger than a bare `exit(1 + Recurse(N - 1))`:

| ceiling in force | AL depth | outcome |
|---|---|---|
| 1000 | 998 | completes, returns 998 |
| 1000 | 999 | refused by the guard (`Maximum recursion depth (1000) exceeded`) |
| 100000 (guard effectively off) | 1200 | completes, returns 1200 |
| 100000 | 2000 | completes, returns 2000 |
| 100000 | 4000 | completes, returns 4000 |

The process reported its run summary normally in every row; nothing died on a signal. At four
times BC's ceiling the CLR stack is still not the binding constraint, so the depth counter is
what refuses, exactly as on BC.

**The two-scope offset.** An AL recursion of depth N consumes N+2 scopes — the test method's
own scope and the root scope sit below it — which is why 998 is the deepest recursion that
completes under a ceiling of 1000. Nested calls consume scopes too: a frame calling
`CreateGuid()`, `Format()` and `CopyStr()` uses more than one scope per AL frame, so the
usable AL depth for such a frame is lower. That is a property of BC's counter, which counts
scopes rather than procedure frames, not of the runner.

## What is not settled here

BC's user-facing text for resource `000004N` is not asserted by the runner. The runner's
message is its own (`Maximum recursion depth (<n>) exceeded`); matching BC's wording is a
separate question, and one only a service tier can answer.
