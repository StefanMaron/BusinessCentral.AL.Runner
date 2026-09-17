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
| **static abstract interface member** | `constrained. call IStatic::Jump(…)` | a plain `call`, so no opcode test reached it — **57 sites of this are live in the closure** |
| **`async` state machine** | `call AsyncTaskMethodBuilder::Start<TSm>(…)` | out of universe and a plain `call`, while `TSm::MoveNext` — which holds the store — is IN the universe and is never enqueued |
| **`Delegate.DynamicInvoke`** | `call System.Delegate::DynamicInvoke(…)` | non-virtual, and `?.` null-checks the receiver, so Roslyn emits `call` rather than `callvirt` |

The issue was filed about the first. The second was found in re-review of #4308 and has no
interface in it, so a fix scoped to interfaces would have closed the measured case and left the
mechanism open. **The property worth asserting is the general one: no call site inside the
closure is silently ignored.**

## What the fix does

Two different answers, because the two shapes are not equally decidable.

- **A dispatch whose target is chosen at run time is FOLLOWED.** Its possible in-universe
  targets are enumerated from the universe's own method table — every universe method that is
  `virtual` **or `static`**, takes the same number of arguments, and answers to that name,
  directly or through an explicit interface implementation's `Overrides` entry.
- **A delegate invocation or a `calli` is REFUSED**, reported on the walk's `Unfollowable` list,
  which `TheWalkFollowedEveryCallSiteInsideTheClosure` asserts is empty. Its target is not a
  property of the call site at all.
- **A call that names a universe type as a GENERIC ARGUMENT OF THE METHOD is REFUSED**, which is
  what closes the `async` shape: control re-enters the universe through a member of that type the
  instruction does not name.

  1. **The declaring type's generic arguments are not read at all.** `List<Row>::Add` names `Row`
     only through its declaring type.
  2. **The argument type must declare a body that is `virtual` or `static`** — the same set
     `IndirectTargetsInUniverse` treats as dispatchable. A plain data type has no member
     out-of-universe code could re-enter through.

  **Both are load-bearing, and neither alone gets to zero.** Without them the refusal fires on
  ordinary C# over any type nested in the host, and the `Assert.Empty(Unfollowable)` in
  `AGenericLocalOverAUniverseTypeIsNotRefused` — FLOOR 5's fixture-side analogue — turns every
  such refusal into a **false RED on correct code**. Counted per call site in the fixture entry
  `EntryWithGenericLocals`, re-measured after the declaring-type line came out:

  | shape (call sites) | shipped | narrowing 1 dropped (2 kept) | both dropped |
  |---|---|---|---|
  | `List<Row>` — `.ctor`, `Add`, `get_Item`×2, `get_Count` (5) | 0 | 0 | **5** |
  | `EqualityComparer<Row>` — `get_Default`, `Equals` (2) | 0 | 0 | **2** |
  | `Enumerable.Count<Row>` (1) | 0 | 0 | **1** |
  | `List<ThroughInterface>` — `.ctor`, `Add`, `get_Count` (3) | 0 | **3** | **3** |

  Read the middle column against the last one: it is the **whole** column-2 total, so `Row` is
  suppressed by narrowing 2 rather than by narrowing 1, and only `ThroughInterface` — which has a
  virtual body — is attributable to the declaring-type branch. A review that attributed all of
  these to that branch would have removed it and left the eight `Row` refusals standing.
  Conversely `Enumerable.Count<Row>` **is** a generic *method* call, so narrowing 1 does not
  touch it, and dropping narrowing 2 alone leaves exactly that one refusing.

  The `async` shape passes both tests, which is why it still refuses:
  `AsyncTaskMethodBuilder.Start<TStateMachine>` is a generic method, and the state machine
  declares `MoveNext` — virtual, with a body, holding the store.

  `AGenericLocalOverAUniverseTypeIsNotRefused` anchors the absence, which is the only kind of
  anchor a narrowing can have. It reds when **either** narrowing is dropped in isolation
  (`Failed: 1, Passed: 15` each), which is what makes the two independently pinned rather than
  one riding on the other.

### The opcode is the wrong dial; the `constrained.` prefix is the right one

`callvirt` is not what makes a call indirect. A **`constrained.` prefix on a type parameter**
makes the next instruction indirect whatever its opcode: the target is chosen when `T` is
substituted, so a `constrained. call` to a static abstract interface member dispatches exactly
as a `callvirt` does. Reading only the opcode accounted for **1** of the closure's 58
constrained sites and silently ignored the other **57**.

Two further properties come with it, and both are needed:

- a static abstract member's implementation is **`static` and not `virtual`**, so a
  virtual-only candidate filter finds no target for any of those 57 sites;
- the delegate refusal must **not** be gated on the opcode either — `Invoke()` on a closed
  delegate type is a `callvirt`, but `Delegate.DynamicInvoke` is non-virtual and `?.` has
  already null-checked the receiver, so Roslyn emits a plain `call`. Measured off the fixture's
  IL, not assumed.

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

Measured on the runner's own `AlRunner.dll`, read with Mono.Cecil. Re-derived after the
declaring-type line came out of `InUniverseGenericArguments`; every figure below is from
that re-run, not carried over.

| | before (#4308) | after |
|---|---|---|
| universe types | 5 | 5 |
| universe methods | 111 | 111 |
| closure from `Invoke()` | 15 | **16** |
| unresolved in universe | 0 | 0 |
| unfollowable | — | **0** |

The single addition is `LiveNavTestPage::Dispose()`, pulled in by the `IDisposable::Dispose()`
call the `foreach` in `FlushParts` emits. **That edge is spurious, and the walk could rule it
out** — the `constrained.` prefix one instruction earlier names the receiver exactly as
`Dictionary<int,ITestPart>.ValueCollection.Enumerator`, which is one instruction of context
rather than dataflow. It is admitted because the walk does not read the prefix for
receiver-narrowing, not because the information is unavailable. Harmless: `Dispose()` holds no
store to either flag, so the widening reds nothing.

**Indirect-dispatch sites inside the closure, counted by instruction**, which is the count that
matters because each one is a place the walk either follows or refuses:

| prefix + opcode | sites | where |
|---|---|---|
| `constrained.` + `call` | **57** | `CalculateClientAutoKey` and `<CalculateClientAutoKey>g__Step\|124_0`, all `System.Numerics` static-abstract members |
| `constrained.` + `callvirt` | **1** | `FlushParts` → `IDisposable::Dispose()` |

All 58 are now followed; before this change **1** was and 57 were dropped as plain `call`s. Of
all 58, exactly **one** yields any in-universe candidate: `IDisposable::Dispose()` matches
`LiveNavTestPage::Dispose()`.

**Why the other 57 yield none is not "nothing here implements `System.Numerics`" — the filter
never asks about interfaces.** It matches **name and arity**. The 57 sites call `get_Zero/0`,
`op_Checked*/2`, `Min/2`, `Max/2`, `CreateChecked/1` and `op_Equality/2`, and **0** universe
methods carry those shapes today — measured directly as **0 static candidates across all 58
sites**, out of **18** universe statics with a body that were eligible to match. That is a measurement about *this* universe, not a property of
the design: an ordinary static helper or an operator overload of a matching shape would be
enqueued at a site it can never run at, and a store inside it would be reported as an offender on
the headline row. Constructed and measured — a nested type with a plain `operator +` produced
`STORE (op_Addition(Trap,Trap), _pendingNewRow, True)`. **Re-measure this line rather than
reasoning about interfaces.**

There are **no** delegate invocations, **no** `calli` and **no** in-universe generic *method*
arguments in the closure, which is why all three refusals are silent today (`UNFOLLOWABLE=0`).

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

The walk costs about **2.8 ms** warm (the same `TypeDefinition` re-walked ten times), against
roughly 1 ms before. The first call is dominated by Cecil's lazy body loading — **72.7 ms**
after, 63 ms before — and the whole test class runs in **498 ms** for 15 tests, against 251 ms
for 5. Per test that is ~33 ms after against ~50 ms before.

## The `TypeSpecification` clause is dead as written

`declaring is not TypeSpecification &&` sits in front of `universe.Contains(declaring.FullName)`
and cannot change any answer. Measured by constructing each subclass over `LiveNavTestPage` and
asking whether its `FullName` is in the universe set:

| shape | `FullName` | in universe set? |
|---|---|---|
| `ArrayType` `[]` / `[,]` | `AlRunner.LiveNavTestPage[]` / `…[,]` | **no** |
| `ByReferenceType` / `PointerType` | `…&` / `…*` | **no** |
| `RequiredModifierType` | `… modreq(System.Object)` | **no** |
| `PinnedType` / `SentinelType` | `AlRunner.LiveNavTestPage` | yes |
| `GenericInstanceType` | unwrapped by the ternary before the clause sees it | n/a |

Cecil spells the suffix into `FullName`, so `universe.Contains` already excludes every shape the
clause was written for. Only `PinnedType`/`SentinelType` collide, and neither can be a method
reference's declaring type in C#-emitted IL; the closure holds **0** non-generic
`TypeSpecification` declaring types. The clause's original comment described a
`GetElementType()` call the code does not make — the hazard went away when that changed and the
comment did not follow.

**It is kept rather than deleted**: it costs nothing, and it would become load-bearing if
Cecil's `FullName` spelling ever stopped carrying the suffix. Deleting it is a behaviour change
resting on "cannot occur in C#-emitted IL", which is a bigger argument than the clause is worth.
Pre-existing; unchanged by #4320 except for the comment.

## Re-deriving any of this

Every figure above comes from Cecil over `AlRunner.dll`, so a temporary `[Fact]` in the test
class that calls `ClosureFromInvoke` and prints `Reached`, `UnresolvedInUniverse` and
`Unfollowable` reproduces it in one run. Nothing here is read off a cached artifact.
