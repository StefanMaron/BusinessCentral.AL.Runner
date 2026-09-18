# Codeunit manual event binding: who reads it, and from what

Measured while settling [#4289](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/4289).
Every figure here comes from a Mono.Cecil walk of the artifacts on one box, at
**BC 28.1.49838.53910** — the only artifact set that box carried. `Microsoft.Dynamics.Nav.Ncl.dll`
is byte-identical across some BC versions and not others, so treat this as **one** measurement and
re-derive it before quoting it for another version (`CLAUDE.md`, "cite the binaries you measured").

## Who reads IsEventManualBinding

Two call sites in `Microsoft.Dynamics.Nav.Ncl.dll`, and no others:

```
CALLER NavCodeunit::BindSubscription   -> NCLMetaCodeunit::get_IsEventManualBinding [callvirt]
CALLER NavCodeunit::UnBindSubscription -> NCLMetaCodeunit::get_IsEventManualBinding [callvirt]
```

Both reach the meta the same way. `BindSubscription`'s first three instructions are

```
IL_0000: ldarg.0
IL_0001: call     NCLMetaCodeunit NavCodeunit::get_MetaCodeunit()
IL_0006: callvirt System.Boolean NCLMetaCodeunit::get_IsEventManualBinding()
```

so the receiver of the getter is always whatever `NavCodeunit.get_MetaCodeunit` returned on that
same statement.

## Why the `EnsureCodeunitMetaById` stash is not load-bearing for those two

`NavCodeunit.get_MetaCodeunit` is Cecil-owned; the runner's replacement
(`BcRuntime.NavCodeunit_get_MetaCodeunit`) stashes `_metaToClrType[meta] = self.GetType()`
**after** its `GetOrAdd`, so it re-stashes on the cache-HIT branch as well as on the build. That
is the branch PR #4287's body reasoned about: a meta built first by `EnsureCodeunitMetaById` and
then read through `get_MetaCodeunit` is re-stashed by `get_MetaCodeunit` itself, so the stated
consequence — `IsEventManualBinding` answering `false` — cannot arise from that ordering.

The one branch that returns without stashing is the instance-field fast path
(`self.metaCodeunit != null`). Nothing in BC can reach it first:

```
FIELD NavCodeunit::get_MetaCodeunit -> NavCodeunit::metaCodeunit [ldfld]
FIELD NavCodeunit::get_MetaCodeunit -> NavCodeunit::metaCodeunit [stfld]
FIELD NavCodeunit::get_MetaCodeunit -> NavCodeunit::metaCodeunit [ldfld]
```

`metaCodeunit` is touched only inside `get_MetaCodeunit`, whose body the runner replaces
wholesale — so the field is written only by the runner's own helper, on the line after its own
stash.

**What the stash does buy** is `EnsureCodeunitMetaById`'s postcondition: the meta it returns is
complete, with no `NavCodeunit` instance anywhere in the run. That is a runner-internal contract
rather than an AL-observable one, and `AlRunner.Tests/CodeunitManualBindingOptionsTests.cs` pins
it.

## Can the two writers disagree? (`AddOrUpdate` overwrites)

`_metaToClrType.AddOrUpdate` is last-writer-wins, and the two writers resolve the CLR type
differently:

| writer | type stashed |
|---|---|
| `NavCodeunit_get_MetaCodeunit` | `self.GetType()` — the runtime type of the receiver |
| `EnsureCodeunitMetaById` | `FindCodeunitTypePublic(id)`, which matches the **exact** name `Codeunit{id}` |

`CodeunitPatches.MetaCodeunit.cs` derives an id from a receiver named `Codeunit{ID}_xxx`, which
implies a scope/inner class could be the receiver and so the stashed type. Measured on the shipped
**System Application** assembly:

| population | count |
|---|---|
| types (incl. nested) named `Codeunit*` | 533 |
| of those, named exactly `Codeunit<digits>` | 533 |
| of those, carrying `[NavCodeunitOptionsAttribute]` | 533 |
| named `Codeunit<digits>_…` (top-level or nested) | **0** |
| direct `NavCodeunit` subclasses | 518 |
| of those, named exactly `Codeunit<digits>` | **518** |

So in MS-emitted AL output the two writers cannot disagree: every `NavCodeunit` receiver's type is
the same exact-named type `FindCodeunitType` resolves. Where they *could* diverge — a receiver
whose type is not named `Codeunit{id}`, such as the runner's own `NoOpCodeunit` — the
`EnsureCodeunitMetaById` writer is the one holding the attribute, so the overwrite is an
improvement rather than a regression. **Unestablished:** whether any AL compiled by this runner
emits a `Codeunit{N}_…` `NavCodeunit` subclass; nothing in the measured artifacts does.

## NavCodeunitOptions, and the mask that was wrong

```
=== Microsoft.Dynamics.Nav.Runtime.NavCodeunitOptions ===
  SingleInstance     = 1
  EventManualBinding = 2
=== Microsoft.Dynamics.Nav.Runtime.NavCodeunitOptionsAttribute ===
  prop Options : NavCodeunitOptions
  prop IsSingleInstance : Boolean
  prop IsEventManualBinding : Boolean
  ctor (NavCodeunitOptions)
  ctor (NavCodeunitOptions, Int32, CodeunitSubType, Boolean)
```

Both runner readers — `BcRuntime.NCLMetaCodeunit_get_IsEventManualBinding` and
`BcRuntime.IsManualBindingCodeunitType` — preferred the derived `IsEventManualBinding` property
and fell back to `(Convert.ToInt32(Options) & 1) != 0`, commented "EventManualBinding flag = 1".
`1` is `SingleInstance`. The fallback therefore answered the question backwards in both
directions, and it is unreachable while the derived property exists, which is why nothing caught
it. Both now share one decoder that resolves the flag by **member name**.

`Codeunit58` in the System Application is a concrete case the old fallback would have got wrong:
its attribute carries `Options = 1` (`SingleInstance`), which `& 1` reports as manual binding.

## The three ways of not getting an answer

The shared decoder can come away without a flag in four different ways, and only two of
them are answers.

| what is missing | what it means | what the decoder does |
|---|---|---|
| the derived `IsEventManualBinding` property | BC computes the flag some other way, or has dropped the property | fall through to the `Options` enum and read the flag by member name |
| an `EventManualBinding` member on the `Options` enum | the flag genuinely is not declared | `false` — the same answer a codeunit carrying no attribute gets, and what BC's own derived property returns for one that declares nothing |
| `Options` itself | the attribute carries the flag somewhere this code cannot read | **refuse** — `BcShapeGapException`, member `NavCodeunitOptionsAttribute.Options` |
| `Options` reads as `null` | BC kept the member and stopped populating it | **refuse** — same member, message says `read as null` |
| `Options` holds a non-enum | BC re-typed the member | **refuse** — same member, message says `holds a <Type>, which is not an enum` |

The third row is the one that used to be a silent `false`. A `false` there says "not manual
binding" when it means "I could not find out", and the cost is not a missing answer but a wrong
one: every manual-binding codeunit is reported as automatic, and BC's `BindSubscription` /
`UnBindSubscription` unbind its subscribers with nothing said. That is
`guards-need-a-third-state.md` § "A reflection bind that answers null is unmeasurable, not
absent" — a `null` from a reflection lookup means "I could not find it", never "it is not
needed".

The middle row is deliberately *not* a refusal, for the same rule's constraint: a genuinely
absent thing stays a pass, and only an unmeasurable one becomes the third state.

The last two rows were a silent `false` until #4319. They are on the refusing side of
`BcShapeGapException`'s own line, which its file header draws as *"the read could not be
performed. The type/field/property is absent, **or it is present and holding something of a
shape the runner cannot use**"* — `BcShape.RequiredEnumerable` is the worked case beside it, and
`BcShape.RequiredEnum` is the flags sibling this added. What separates them from the
`EventManualBinding`-member row is not how much was read but whether the decode ran to
completion: an enum lacking the member yields a defined answer, a string or a null yields none.

They refuse with different messages on purpose. "BC stopped populating this" and "BC re-typed
this" send a reader to different places, and one message covering both would name neither.

## The option mask, and a flag that does not fit an Int64

BC's own `NavCodeunitOptionsAttribute.get_IsEventManualBinding` is four IL instructions on
28.1.49838.53910 (`Microsoft.Dynamics.Nav.Ncl.dll`, sha256
`49b11d9b541e82959604b68b4ff6b4990fa06db0ffa48dd5b49284cf63507788`):

```
call     NavCodeunitOptions NavCodeunitOptionsAttribute::get_Options()
ldc.i4.2
and
ldc.i4.0
cgt.un          // (Options & 2) != 0
```

So the mask is `(Options & flag) != 0`, **not** `Enum.HasFlag`'s `(Options & flag) == flag`.
The two agree for a single-bit flag — `EventManualBinding = 2` — and diverge for a
multi-bit one, so the decoder spells BC's test rather than the idiomatic .NET one.
`NavCodeunitOptions` is `[Flags]`, `Int32`-backed, `SingleInstance = 1`,
`EventManualBinding = 2`.

The decoder used to evaluate that mask through `Convert.ToInt64` inside a `try` whose `catch`
returned `false`. Measured, on that conversion:

| input | `Convert.ToInt64` | old decoder | correct |
|---|---|---|---|
| `Int32`-backed `EventManualBinding = 2` | `2` | `true` | `true` |
| `UInt64`-backed `EventManualBinding = 0x8000000000000000` | **`OverflowException`** | `false` | **`true`** |
| `UInt64`-backed enum, `Options = SingleInstance` | **`OverflowException`** | `false` | `false` |

`Convert.ToInt64` is the **only** reachable throw on that path: `Enum.Parse` is handed a name
`Enum.GetNames` returned a moment earlier, and cannot fail for one — measured over both enums
above, every name round-trips. So the `catch` was reachable, and its `false` was a wrong answer
about a value that decodes exactly, not a gap to refuse over. The fix is arithmetic, not a
third state: `OptionBits` reinterprets the bits through the underlying type
(`Convert.ToUInt64` for a `ulong`-backed enum, an unchecked widening of `Convert.ToInt64`
otherwise), and the `catch` is gone rather than converted — an untested refusal path is
indistinguishable from a never-fire path (`guards-need-a-third-state.md`).
`AlRunner.Tests/CodeunitManualBindingOptionsTests.cs` pins the pair —
`OptionsEnumWithNoEventManualBindingMember_AnswersFalseRatherThanTestingBitZero` and
`AttributeWithoutAnOptionsMember_RefusesRatherThanAnsweringFalse` — so a change that collapsed
the two into one verdict reds exactly one of them.
