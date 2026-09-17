# The closure walk and indirect dispatch

`AlRunner.Tests/BuiltInCancelIsNotADiscardTests` proves an **absence**: nothing the built-in
OK/Cancel action's `Invoke()` can reach may clear `LiveNavTestPage._pendingNewRow` or
`_pendingModify` without writing the row the flag stands for (#4295, #4302). It does that by
walking the transitive call graph out of `Invoke()` and reading `stfld` out of the IL.

An absence is only as good as the walk behind it. This page holds the census, the termination
and scope arguments, and the alternative that was measured and rejected — the code carries the
claim and a pointer here.

## The hole this closes (#4311)

The walk followed a call only when it could resolve the callee to a body. Two shapes escape that,
and both were **green** against the unfixed guard — the discard was simply invisible:

| shape | what the IL names | why the walk lost it |
|---|---|---|
| interface dispatch | `callvirt IRvD::Go()` | the interface method is **bodiless**, so `if (!resolved.HasBody) continue;` dropped it and the implementation was never enqueued |
| delegate invocation | `callvirt System.Action::Invoke()` | `System.Action` is outside the universe, so it was skipped — and the lambda's `ldftn` sits in a method the closure never reaches |

The issue was filed about the first. The second was found in re-review of #4308 and has no
interface in it, so a fix scoped to interfaces would have closed the measured case and left the
mechanism open. **The property worth asserting is the general one: no call site inside the
closure is silently ignored.**

## What the fix does

Two different answers, because the two shapes are not equally decidable.

- **A virtual or interface dispatch is FOLLOWED.** Its possible in-universe targets are
  enumerated from the universe's own method table — every universe method that is `virtual`
  (every interface implementation and every override is; nothing else can be dispatched to),
  takes the same number of arguments, and answers to that name, directly or through an explicit
  interface implementation's `Overrides` entry.
- **A delegate invocation or a `calli` is REFUSED**, reported on the walk's `Unfollowable` list,
  which `TheWalkFollowedEveryCallSiteInsideTheClosure` asserts is empty. Its target is not a
  property of the call site at all.

### Termination

The candidate set is always a subset of the universe's methods, and `Reach` enqueues a method
only when it is not already in `reached`, keyed on `FullName`. So every method is enqueued at
most once and the universe is finite — an implementation that dispatches through a second
interface simply enqueues more universe methods, bounded by the universe's method count. The
widening cannot make the walk diverge.

### Scope: the accessibility bound is untouched

#4302's universe is `LiveNavTestPage` plus its nested types, to any depth, and that bound is
verified by the **compiler**: both flags are `private`, so an `stfld` of them from anywhere else
fails with `CS0122`. Enumerating implementations does **not** escape it, because the candidate
set is drawn from the universe and nowhere else. This change widens the *reachability relation
inside* the universe; it never widens the universe. An implementation living outside it cannot
hold the store, so not walking it loses nothing.

## Census

Measured on `127ceac` (the runner's own `AlRunner.dll`, read with Mono.Cecil).

| | before (#4308) | after |
|---|---|---|
| universe types | 5 | 5 |
| universe methods | 111 | 111 |
| closure from `Invoke()` | 15 | **16** |
| unresolved in universe | 0 | 0 |
| unfollowable | — | **0** |

The single addition is `LiveNavTestPage::Dispose()`, pulled in by the `IDisposable::Dispose()`
call the `foreach` in `FlushParts` emits. `LiveNavTestPage` inherits `IDisposable` through
`MockITestPage`, so it is a legitimate candidate receiver and the walk cannot prove otherwise
without dataflow. It holds no store to either flag, so the widening reds nothing.

Indirect-dispatch sites inside the closure, distinct by resolved member: **16 interface** (15
`System.Numerics` constrained generic-math members reached from `CalculateClientAutoKey` and its
`g__Step` local function, plus `System.IDisposable::Dispose`) and **2 virtual**
(`NavValue::get_ClientObject`, `NavRecord::ModifyAsync`). Of every one of those, the only
in-universe target any name-and-arity match produces is `Dispose()`. There are **no** delegate
invocations and **no** `calli` in the closure, which is why the refusal is silent today.

## The over-approximation that was rejected, and why

The tempting symmetric answer for the delegate case is: enqueue every universe method whose
address is taken anywhere in the universe. **Measured, and it reds innocent code.**

The universe has 7 address-taken methods, and two of them are `MarkEdited()` and
**`ActivateControl(Int32)`** — and `ActivateControl` legitimately clears `_pendingNewRow` (it
clears the flag, inserts the pending row, and restores the flag if the insert fails). It sits
outside the closure, which is exactly why the closure exists rather than a whole-universe scan.
Pulling it in on the first delegate invocation anyone adds would fail the regression row with an
offender that is not a defect.

Signature compatibility does not rescue it either: `ActivateControl(Int32)` is assignable to
`Action<int>` and `MarkEdited()` to `Action`, so the commonest delegate types match anyway.

So the delegate case gets the third state instead (`guards-need-a-third-state.md`). It is the
honest answer: the walk cannot tell, and the guard says so rather than reporting the absence it
did not establish.

**The stated limit**: the refusal fires even when the matching `ldftn` is visible inside the
closure, because pairing a delegate creation with the site that invokes it needs dataflow this
walk does not do. That direction is the safe one — it over-refuses rather than under-reports —
and today it costs nothing, since the closure holds no delegate invocation at all.

## The narrowing that was deliberately not applied

Candidates could be filtered further by asking which universe types actually implement the
interface or derive from the declaring class. It is not applied, for three measured reasons: it
can only *remove* candidates, so it can only weaken the guard; it removes **none** today, since
the one match (`Dispose`) is on a type that really does inherit `IDisposable`; and it needs a
base/interface resolution that can fail, which would introduce a new unmeasurable in exchange
for nothing.

## Cost

The walk costs about **2.4 ms** warm (the same `TypeDefinition` re-walked ten times), against
roughly 1 ms before. The first call is dominated by Cecil's lazy body loading — 77 ms after,
63 ms before — and the whole test class runs in **372 ms** for 9 tests, against 251 ms for 5.

## Re-deriving any of this

Every figure above comes from Cecil over `AlRunner.dll`, so a temporary `[Fact]` in the test
class that calls `ClosureFromInvoke` and prints `Reached`, `UnresolvedInUniverse` and
`Unfollowable` reproduces it in one run. Nothing here is read off a cached artifact.
