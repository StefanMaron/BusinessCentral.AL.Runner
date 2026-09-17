// BuiltInCancelIsNotADiscardTests — issues #4295, #4302, #4311.
//
// The defect: LiveNavTestPage's built-in Cancel/LookupCancel action called
// DiscardPendingNewRow(), which cleared the HOST page's _pendingNewRow and _pendingModify
// flags. The page's ordinary close flush (Dispose -> FlushParts(); FlushRow()) then found
// nothing pending on the host and wrote nothing, so a field typed into the host before Cancel
// silently reverted.
//
// The BC-behaviour claim — that Cancel keeps a pending host field change — is NOT asserted
// here. It is measured on a real service tier by corpus codeunit 60535 "PCN Tests"
// (StefanMaron/BusinessCentral.AL.Language.Tests#378), green on all eight cloud legs, 27.0
// through 28.4. Codeunit 60844 "TRT Tests" measures the same absence of a rollback for a dirty
// new row reached through Close(). Duplicating either claim here would only prove the runner
// agrees with itself.
//
// What IS asserted here is the runner-internal mechanism that makes it true, and it is an
// ABSENCE, which no return value exposes: nothing the built-in action's Invoke() can reach may
// clear those two flags without writing the row they stand for. A regression that reinstates
// the discard restores the exact defect while every unit-level return value stays the same, so
// the flag stores are read out of the IL.
//
// The other half of this fix's dependency is already pinned elsewhere: Cancel now relies on
// Dispose() to write what it left pending, and TestPageModalClosePartFlushTests
// .DisposeMustFlushBothPartsAndItsOwnRow fails if either of Dispose()'s two flush calls goes.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class BuiltInCancelIsNotADiscardTests
{
    // The two flags that decide whether the close flush writes the host's own row. Clearing
    // either without writing is the defect.
    private static readonly string[] PendingWriteFlags = { "_pendingNewRow", "_pendingModify" };

    // The ONLY legitimate clears anywhere under Invoke(): each flush method clears its OWN flag
    // on entry and then writes that row, so the flag is consumed rather than discarded. Keyed by
    // (signature, field), on both halves deliberately — by FIELD so a flush clearing the OTHER
    // flag is still an offender, and by SIGNATURE rather than name so the exemption covers the
    // parameterless flush alone. Trap: a bare-name key licenses every OVERLOAD of that name, and
    // `FlushPendingNewRow(bool)` clearing without writing is a discard the guard would then miss.
    // Both pairs are asserted to be OCCUPIED below: an exemption whose store has gone is a
    // licence left lying around for a future discard to be written under.
    private static readonly (string Signature, string Field)[] FlushMayClearItsOwnFlag =
    {
        ("FlushPendingNewRow()", "_pendingNewRow"),
        ("FlushPendingModify()", "_pendingModify"),
    };

    private static string Signature(MethodDefinition m)
        => $"{m.Name}({string.Join(",", m.Parameters.Select(p => p.ParameterType.Name))})";

    private static ModuleDefinition Module()
        => ModuleDefinition.ReadModule(typeof(AlRunner.TestExecutor).Assembly.Location);

    private static TypeDefinition LiveNavTestPage(ModuleDefinition module)
        // LiveNavTestPage is internal and RecordingBuiltInAction is a private nested type, so
        // both are read through Cecil rather than through the type system.
        => module.GetTypes().Single(t => t.FullName == "AlRunner.LiveNavTestPage");

    private static MethodDefinition BuiltInActionInvoke(TypeDefinition host)
        => host.NestedTypes.Single(t => t.Name == "RecordingBuiltInAction")
               .Methods.Single(m => m.Name == "Invoke" && m.Parameters.Count == 0);

    private static bool CallsMethodNamed(MethodDefinition method, string calleeName)
        => method.Body.Instructions.Any(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            && i.Operand is MethodReference callee && callee.Name == calleeName);

    /// <summary>
    /// Every method that could contain a store to those flags at all: LiveNavTestPage and the
    /// types nested inside it, to any depth. That bound is C# accessibility, not a guess about
    /// where a regression would be written — the flags are <c>private</c> on LiveNavTestPage, so
    /// this set is exactly the code the compiler will accept an <c>stfld</c> of them from
    /// (#4302). Nested to any depth because a lambda or an async rewrite moves a method body
    /// into a generated type nested inside its declarer.
    /// </summary>
    private static HashSet<string> FlagWritableUniverse(TypeDefinition host)
        => UniverseTypes(host).Select(t => t.FullName).ToHashSet();

    /// <summary>
    /// The same set as <see cref="FlagWritableUniverse"/>, as types rather than names, so the
    /// walk can ask which of their methods an indirect dispatch could land on. One traversal
    /// with two projections: a second copy of the nested-type walk is a second thing to keep in
    /// step with the first.
    /// </summary>
    private static List<TypeDefinition> UniverseTypes(TypeDefinition host)
    {
        var types = new List<TypeDefinition>();
        var pending = new Stack<TypeDefinition>();
        pending.Push(host);
        while (pending.Count > 0)
        {
            var type = pending.Pop();
            types.Add(type);
            foreach (var nested in type.NestedTypes) pending.Push(nested);
        }
        return types;
    }

    /// <summary>
    /// The in-universe methods an indirectly-dispatched call to <paramref name="callee"/> could
    /// land on (#4311): a universe method of the same arity answering to that name — directly, or
    /// through an explicit interface implementation, whose own name is mangled (<c>Ns.IFoo.Go</c>)
    /// and so never matches directly. Always a subset of the universe, so #4302's
    /// compiler-verified accessibility bound is untouched.
    /// See docs/closure-walk-indirect-dispatch.md#scope-the-accessibility-bound-is-untouched.
    /// <para><c>IsStatic</c> is not redundant beside <c>IsVirtual</c>: a static abstract interface
    /// member's implementation is <c>static</c> and NOT virtual, so a virtual-only filter finds no
    /// candidate for the 57 <c>constrained. call</c> sites in the closure.</para>
    /// <para>Trap: this matches on NAME AND ARITY and asks nothing about interfaces, so an
    /// ordinary static helper or operator overload of the right shape IS enqueued at a site it
    /// could never run at — the 57 call <c>get_Zero/0</c>, <c>op_Checked*/2</c>, <c>Min/2</c>,
    /// <c>Max/2</c>, <c>CreateChecked/1</c>, and a nested type with a plain <c>operator +</c> was
    /// measured being enqueued and its store reported as an offender. Harmless only because
    /// <strong>0</strong> universe statics match any of the 58 today; re-measure rather than
    /// reasoning from "nothing here implements System.Numerics", which is not what this asks
    /// (#4320 review).</para>
    /// <para>Trap: read off the <see cref="MethodReference"/>, never its definition — it must
    /// still answer for a callee that will not resolve. And do not narrow it by the type
    /// hierarchy: that can only remove candidates, removes none today, and buys an unmeasurable.</para>
    /// </summary>
    private static IEnumerable<MethodDefinition> IndirectTargetsInUniverse(
        List<MethodDefinition> universeMethods, MethodReference callee)
        => universeMethods.Where(m =>
            (m.IsVirtual || m.IsStatic) && m.HasBody
            && m.Parameters.Count == callee.Parameters.Count
            && (m.Name == callee.Name || m.Overrides.Any(o => o.Name == callee.Name)));

    /// <summary>
    /// The universe types this call names only as a GENERIC ARGUMENT OF THE METHOD. Control can
    /// re-enter the universe through a member of one that the instruction does not name — an
    /// async method's state machine handed to <c>AsyncTaskMethodBuilder.Start&lt;T&gt;</c> is the
    /// live shape, and it is a generic <em>method</em> argument.
    /// <para>Two narrowings, and BOTH are load-bearing against false REDs on ordinary C# over a
    /// type nested in the host (#4320 review). The DECLARING type's generic arguments are not
    /// read at all: that refused <c>new List&lt;Row&gt;()</c> (3) and
    /// <c>EqualityComparer&lt;Row&gt;.Default.Equals</c> (2). And the argument type must declare
    /// something out-of-universe code could actually dispatch INTO — a body that is
    /// <c>virtual</c> or <c>static</c>, the same set <see cref="IndirectTargetsInUniverse"/>
    /// treats as reachable. Without that second test <c>Enumerable.Count&lt;Row&gt;</c> is
    /// refused (1), because it IS a generic method call and the first narrowing does not touch
    /// it. A plain data type has no such member, so nothing can re-enter through it.</para>
    /// <para>The async state machine passes both: <c>Start&lt;TStateMachine&gt;</c> is a generic
    /// method, and the state machine declares <c>MoveNext</c> — virtual, with a body, holding the
    /// store. <c>AGenericLocalOverAUniverseTypeIsNotRefused</c> anchors the absence, which is the
    /// only anchor a narrowing can have.</para>
    /// </summary>
    private static IEnumerable<string> InUniverseGenericArguments(
        HashSet<string> universe, List<TypeDefinition> universeTypes, MethodReference callee)
        => callee is not GenericInstanceMethod method
            ? Enumerable.Empty<string>()
            : method.GenericArguments.Select(a => a.FullName).Where(universe.Contains)
                .Where(name => universeTypes.Single(t => t.FullName == name).Methods
                    .Any(m => m.HasBody && (m.IsVirtual || m.IsStatic)))
                .Distinct();

    /// <summary>
    /// Whether a call site invokes a delegate, whose target is whatever <c>ldftn</c> built it and
    /// is therefore not a property of this instruction at all. A declaring type that will not
    /// resolve answers <c>true</c>: an unknown must not resolve toward the followable direction.
    /// <para>Trap: the name alone does not decide it. An ordinary interface may declare
    /// <c>Invoke()</c> — <c>RecordingBuiltInAction.Invoke()</c> is itself an arity-0 <c>Invoke</c>
    /// — and refusing that would report an unmeasurable where the walk can see the implementation
    /// perfectly well. The declaring type is what discriminates, and both halves are pinned
    /// (#4320 review).</para>
    /// </summary>
    private static bool IsDelegateInvocation(MethodReference callee)
    {
        if (callee.Name != "Invoke" && callee.Name != "DynamicInvoke") return false;
        TypeDefinition declaring;
        try { declaring = callee.DeclaringType.Resolve(); }
        catch (AssemblyResolutionException) { return true; }
        if (declaring is null) return true;
        // DynamicInvoke is declared on Delegate itself; Invoke on the closed delegate type.
        return declaring.BaseType?.FullName == "System.MulticastDelegate"
            || declaring.FullName is "System.Delegate" or "System.MulticastDelegate";
    }

    /// <summary>
    /// What Invoke() reaches within that universe, transitively — 16 of the type's 111 methods,
    /// so the walk is one type's own call graph and costs milliseconds rather than a
    /// repository-wide closure. It reports two kinds of hole beside the methods it reached, and
    /// both are failures of measurement rather than absences of stores: a callee it could not
    /// RESOLVE, and a call site it could not FOLLOW. See docs/closure-walk-indirect-dispatch.md
    /// for the census behind those figures and the termination argument.
    /// <para>Trap: resolve through <see cref="MethodReference.Resolve"/> — a call to a GENERIC
    /// method carries the instantiated name (<c>CalculateClientAutoKey&lt;System.Int32&gt;</c>), so
    /// matching FullName against the definitions dropped six real references here and made a
    /// generic discard helper invisible at depth 1 (#4302 review). Follow ldftn/ldvirtftn too: a
    /// lambda is reached through a delegate whose declaring type this walk stops at.</para>
    /// </summary>
    private static Walk ClosureFromInvoke(TypeDefinition host)
        => ClosureFrom(host, BuiltInActionInvoke(host));

    private readonly record struct Walk(
        Dictionary<string, MethodDefinition> Reached,
        List<string> UnresolvedInUniverse,
        List<string> Unfollowable);

    private static Walk ClosureFrom(TypeDefinition host, MethodDefinition entry)
    {
        var universeTypes = UniverseTypes(host);
        var universe = universeTypes.Select(t => t.FullName).ToHashSet();
        var universeMethods = universeTypes.SelectMany(t => t.Methods).ToList();
        var reached = new Dictionary<string, MethodDefinition>();
        var unresolved = new List<string>();
        var unfollowable = new List<string>();
        var queue = new Queue<MethodDefinition>();
        reached[entry.FullName] = entry;
        queue.Enqueue(entry);

        void Reach(MethodDefinition target)
        {
            if (reached.ContainsKey(target.FullName)) return;
            reached[target.FullName] = target;
            queue.Enqueue(target);
        }

        while (queue.Count > 0)
        {
            var caller = queue.Dequeue();
            foreach (var instruction in caller.Body.Instructions)
            {
                var op = instruction.OpCode;

                // calli's operand is a CallSite, not a MethodReference, so it would fall out of
                // the cast below and read as "not a call at all". It is the opposite: a call
                // through a function pointer, whose target this walk cannot name.
                if (op == OpCodes.Calli)
                {
                    unfollowable.Add($"{caller.Name} -> calli {instruction.Operand}");
                    continue;
                }
                if (op != OpCodes.Call && op != OpCodes.Callvirt && op != OpCodes.Newobj
                    && op != OpCodes.Ldftn && op != OpCodes.Ldvirtftn) continue;
                if (instruction.Operand is not MethodReference callee) continue;

                // INDIRECT DISPATCH (#4311). A dispatch whose target is chosen at run time is
                // FOLLOWED to the universe methods it could land on; a delegate invocation is
                // REFUSED, because its target is not a property of the call site. Neither may be
                // the silent `continue` that let a discard behind both read as an absence of one.
                // docs/closure-walk-indirect-dispatch.md#what-the-fix-does
                //
                // A `constrained.` prefix makes the NEXT instruction indirect whichever opcode it
                // is: on a type parameter the target is chosen when T is substituted, so a
                // `constrained. call` to a static abstract interface member is dispatched exactly
                // as a callvirt is. Reading only the opcode accounted for 1 of the closure's 58
                // constrained sites and silently ignored the other 57 (#4320 review).
                //
                // Trap, for the next person who tries to make the delegate case followable:
                // enqueueing every address-taken universe method was measured and REDS INNOCENT
                // CODE — ActivateControl(Int32) is address-taken and legitimately clears
                // _pendingNewRow from outside the closure. Signature matching does not rescue it,
                // and note ActivateControl is not virtual, so the follow path cannot reach it.
                // The delegate refusal is deliberately NOT gated on the opcode. `Invoke()` on a
                // closed delegate type is a callvirt, but `Delegate.DynamicInvoke` is non-virtual
                // and `?.` has already null-checked the receiver, so Roslyn emits a plain `call`
                // — measured off the fixture's IL, not assumed. Gating on the opcode let that one
                // through silently.
                var constrained = instruction.Previous?.OpCode == OpCodes.Constrained;
                if (IsDelegateInvocation(callee))
                    unfollowable.Add($"{caller.Name} -> {callee.FullName}");
                else if (op == OpCodes.Callvirt || op == OpCodes.Ldvirtftn
                         || (op == OpCodes.Call && constrained))
                    foreach (var target in IndirectTargetsInUniverse(universeMethods, callee))
                        Reach(target);

                // Unwrap a generic instantiation to the type that declares the member. The
                // TypeSpecification clause below is belt-and-braces and is DEAD as written:
                // measured, Cecil spells the suffix into FullName (`AlRunner.LiveNavTestPage[]`,
                // `…&`, `…*`, `… modreq(…)`), so universe.Contains already excludes every one
                // without it. Only PinnedType/SentinelType share the bare FullName, and neither
                // can be a method reference's declaring type in C#-emitted IL; the closure holds
                // 0 non-generic TypeSpecification declaring types today. Kept rather than
                // deleted because it costs nothing and would become load-bearing if that FullName
                // spelling ever changed. docs/closure-walk-indirect-dispatch.md#the-typespecification-clause-is-dead-as-written
                var declaring = callee.DeclaringType is GenericInstanceType generic
                    ? generic.ElementType
                    : callee.DeclaringType;
                var declaredInUniverse =
                    declaring is not TypeSpecification && universe.Contains(declaring.FullName);

                // Resolve() answers null for a missing MEMBER but THROWS for a missing ASSEMBLY,
                // so the unreadable-System.*/BC case the skip below exists for arrives as an
                // exception, not a null. Unwrapped, it would fail the run with a Cecil stack
                // trace instead of skipping. Measured: 118 out-of-universe callees resolve and
                // none is unresolvable today, so this catch has never fired — which is exactly
                // why it must be here rather than inferred from the branch never being seen.
                MethodDefinition resolved;
                try { resolved = callee.Resolve(); }
                catch (AssemblyResolutionException) { resolved = null; }

                // Three outcomes, and only the first two are legitimate passes. A callee OUTSIDE
                // the universe cannot hold an stfld of a private field of this type, so skipping
                // an unresolvable one is the "genuinely absent stays a pass" constraint — refusing
                // on every unreadable System.*/BC reference would trade a false green for a false
                // red. A callee INSIDE it that will not resolve is code we are obliged to scan and
                // could not (guards-need-a-third-state.md).
                if (resolved is null)
                {
                    if (declaredInUniverse) unresolved.Add($"{caller.Name} -> {callee.FullName}");
                    continue;
                }
                if (!universe.Contains(resolved.DeclaringType.FullName))
                {
                    // An out-of-universe callee cannot itself hold the stfld, so skipping it is
                    // the "genuinely absent stays a pass" constraint — EXCEPT when it names a
                    // universe type as a GENERIC ARGUMENT, which means control can re-enter the
                    // universe through a member of that type this instruction does not name.
                    // The live shape is an async method: the body moves to a compiler-generated
                    // state machine nested in the declarer (so in the universe, and holding the
                    // stfld), entered by `call AsyncTaskMethodBuilder::Start<TStateMachine>` —
                    // out of universe, and a plain `call`. Refused rather than followed: which
                    // member runs is not on the instruction (#4320 review).
                    foreach (var argument in InUniverseGenericArguments(universe, universeTypes, callee))
                        unfollowable.Add($"{caller.Name} -> {callee.Name}<{argument}>");
                    continue;
                }

                // Bodiless by construction — an interface member, an abstract override, an extern.
                // There is nothing to walk FROM here; what actually runs was enqueued above by
                // IndirectTargetsInUniverse, which is why this skip is no longer the hole #4311
                // reported.
                //
                // THE POPULATION, because this skip is only safe as long as it holds. The closure
                // carries 58 indirect-dispatch sites: 57 `constrained. call` to System.Numerics
                // static-abstract members from CalculateClientAutoKey and its g__Step local
                // function, and 1 `constrained. callvirt` to IDisposable::Dispose from FlushParts'
                // foreach. All 58 are now enqueued-or-refused rather than dropped here, and none
                // resolves to an implementation INSIDE the universe, so none can hold the stfld.
                // **Re-measure that before relying on it** — it is a fact about today's closure,
                // not a property of the design. docs/closure-walk-indirect-dispatch.md#census
                if (!resolved.HasBody) continue;

                Reach(resolved);
            }
        }

        return new Walk(reached, unresolved, unfollowable);
    }

    /// <summary>
    /// Every store to a pending-write flag Invoke() can reach, as (signature, field, clears). A
    /// store is a CLEAR when the value pushed is literal <c>false</c>, a SET when it is literal
    /// <c>true</c> — measured, not assumed: Roslyn emits ldc.i4.0/ldc.i4.1 before each of the
    /// seven stores in this type today. Anything else is unclassifiable from IL alone and is
    /// reported as a clear, because an unknown must not resolve toward the success state.
    /// </summary>
    private static List<(string Signature, string Field, bool Clears)> FlagStoresUnderInvoke()
    {
        var host = LiveNavTestPage(Module());
        return FlagStores(ClosureFromInvoke(host));
    }

    private static List<(string Signature, string Field, bool Clears)> FlagStores(Walk walk)
    {
        var stores = new List<(string, string, bool)>();

        foreach (var method in walk.Reached.Values
                     .OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            Instruction previous = null;
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode == OpCodes.Stfld
                    && instruction.Operand is FieldReference field
                    && PendingWriteFlags.Contains(field.Name))
                    stores.Add((Signature(method), field.Name, previous?.OpCode != OpCodes.Ldc_I4_1));
                previous = instruction;
            }
        }

        return stores;
    }

    // THE REGRESSION ROW. Reinstating the discard — however it is spelled, and at whatever call
    // depth below Invoke() — fails here.
    [Fact]
    public void NothingInvokeCanReachClearsTheHostPendingWriteFlags()
    {
        var offenders = FlagStoresUnderInvoke()
            .Where(s => s.Clears && !FlushMayClearItsOwnFlag.Contains((s.Signature, s.Field)))
            .Select(s => $"{s.Signature} clears {s.Field}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Nothing reachable from the built-in OK/Cancel action may clear the host page's "
            + "pending-write flags: BC's Cancel is not a discard (corpus 60535, issue #4295), and "
            + "a cleared flag makes Dispose()'s FlushRow() write nothing. Only the two flush "
            + "methods may clear the flag they are about to write. Offending stores: "
            + string.Join("; ", offenders));
    }

    // FLOOR 1. A renamed or deleted flag would make the scan above search for nothing and pass.
    [Fact]
    public void TheHostPendingWriteFlagsStillExistUnderTheNamesTheScanReads()
    {
        var fields = LiveNavTestPage(Module()).Fields.Select(f => f.Name).ToHashSet();

        foreach (var flag in PendingWriteFlags)
            Assert.True(fields.Contains(flag),
                $"LiveNavTestPage no longer declares '{flag}', so "
                + nameof(NothingInvokeCanReachClearsTheHostPendingWriteFlags)
                + " is scanning for a field that cannot appear and would pass having measured "
                + "nothing. Rename it in PendingWriteFlags too.");
    }

    // FLOOR 2. An Invoke() that no longer reaches the flush, or no longer attempts the close,
    // is a different method than the one the absence above is claimed about.
    [Fact]
    public void BuiltInActionInvoke_StillFlushesTheRowAndAttemptsTheClose()
    {
        var invoke = BuiltInActionInvoke(LiveNavTestPage(Module()));

        Assert.True(CallsMethodNamed(invoke, "FlushRow"),
            "Invoke() must still flush the host row for the confirming built-ins — OK's row is "
            + "written here and by nothing else before the test reads the table (#3640).");
        Assert.True(CallsMethodNamed(invoke, "FlushParts"),
            "Invoke() must still flush the parts before the host row on OK: a header OnModify "
            + "reads the part's lines (#4146, corpus 60760).");
        Assert.True(CallsMethodNamed(invoke, "AttemptHandlerDrivenClose"),
            "Invoke() must still make the close attempt a built-in press makes on BC (#3593).");
    }

    // FLOOR 3, and the one the depth bound needs. Both exemptions must still be OCCUPIED by a
    // real clear inside the closure. That fails in the two ways the regression row cannot see:
    // a traversal that stopped short — every exempt clear sits at depth 2, one level past where
    // the old scan ended, so an unreachable or unresolvable callee empties the closure and the
    // row above passes having measured nothing (#4302) — and an exemption whose clear has moved
    // away, which stops being a description of the code and becomes a standing licence.
    [Fact]
    public void EveryFlushExemptionIsStillOccupiedByARealClearInsideTheClosure()
    {
        var clears = FlagStoresUnderInvoke().Where(s => s.Clears).ToList();

        foreach (var (signature, field) in FlushMayClearItsOwnFlag)
            Assert.True(clears.Any(c => c.Signature == signature && c.Field == field),
                $"'{signature}' no longer clears '{field}' anywhere Invoke() can reach. Either the "
                + "walk no longer reaches it — in which case "
                + nameof(NothingInvokeCanReachClearsTheHostPendingWriteFlags)
                + " is scanning a collapsed closure and passes having measured nothing — or the "
                + "clear has moved, and the exemption must move with it rather than stay behind "
                + "as a licence. Clears actually found: "
                + (clears.Count == 0 ? "(none)" : string.Join("; ", clears.Select(c => $"{c.Signature}->{c.Field}"))));
    }

    // FLOOR 5, the second refusal (#4311). A call site the walk could not FOLLOW is a different
    // failure from one it could not RESOLVE: the callee is readable, and where control actually
    // goes is simply not a property of the instruction. That is "could not tell", and the silent
    // `continue` it replaced is what made an indirect discard read as an absence of one.
    [Fact]
    public void TheWalkFollowedEveryCallSiteInsideTheClosure()
    {
        var unfollowable = ClosureFromInvoke(LiveNavTestPage(Module())).Unfollowable;

        Assert.True(unfollowable.Count == 0,
            "The closure walk met a call whose target is not a property of the call site — a "
            + "delegate invocation or a calli — so "
            + nameof(NothingInvokeCanReachClearsTheHostPendingWriteFlags)
            + " did not read whatever runs there and cannot claim it holds no discard. Resolving "
            + "it needs the ldftn that built the delegate, which may sit anywhere; enqueueing "
            + "every address-taken universe method instead was measured and reds innocent code "
            + "(ActivateControl is address-taken and legitimately clears _pendingNewRow). Either "
            + "make the call direct, or extend the walk. Unfollowable: "
            + string.Join("; ", unfollowable.Distinct()));
    }

    // A stand-in carrying the two indirect shapes, so the walk's behaviour on each is asserted
    // rather than inferred from LiveNavTestPage happening to contain none of them today. It is a
    // fixture for the WALK, not a model of the page: nothing here claims anything about BC.
    private sealed class IndirectDispatchFixture
    {
        private bool _pendingNewRow;

        private interface IIndirect { void Go(); }

        private sealed class ThroughInterface : IIndirect
        {
            private readonly IndirectDispatchFixture _fixture;
            public ThroughInterface(IndirectDispatchFixture fixture) { _fixture = fixture; }
            public void Go() { _fixture._pendingNewRow = false; }   // implicit: matches on Name
        }

        // EXPLICIT implementation. The compiler names it `…IExplicit.Step`, so only the Overrides
        // match finds it — the clause whose justification was previously untested because every
        // fixture implementation was implicit. Its own interface, so the two cannot cross-match.
        private interface IExplicit { void Step(); }

        private sealed class ThroughExplicitInterface : IExplicit
        {
            private readonly IndirectDispatchFixture _fixture;
            public ThroughExplicitInterface(IndirectDispatchFixture fixture) { _fixture = fixture; }
            void IExplicit.Step() { _fixture._pendingNewRow = false; }
        }

        // A STATIC ABSTRACT interface member. The call site is `constrained. call`, not callvirt,
        // and the implementation is static rather than virtual — the two properties that between
        // them hid 57 sites in the production closure.
        private interface IStaticIndirect { static abstract void Jump(IndirectDispatchFixture f); }

        private sealed class ThroughStaticAbstract : IStaticIndirect
        {
            public static void Jump(IndirectDispatchFixture f) { f._pendingNewRow = false; }
        }

        private static void DispatchStatic<T>(IndirectDispatchFixture f) where T : IStaticIndirect
            => T.Jump(f);

        // An `Invoke()` that is NOT a delegate's, so the refusal must not fire on the name alone.
        private interface IAction { void Invoke(); }

        private sealed class NotADelegate : IAction
        {
            private readonly IndirectDispatchFixture _fixture;
            public NotADelegate(IndirectDispatchFixture fixture) { _fixture = fixture; }
            public void Invoke() { _fixture._pendingNewRow = false; }
        }

        // Ordinary C# over types nested in the host, covering BOTH narrowings — each line is
        // refused if one of them is dropped, and the two are not interchangeable:
        //   * Row is a plain data type, so the DISPATCHABLE test suppresses it. It reaches the
        //     refusal through `Enumerable.Count<Row>`, a generic METHOD, which dropping the
        //     declaring-type branch does not touch.
        //   * ThroughInterface has a virtual body, so the dispatchable test does NOT suppress it.
        //     It reaches the refusal only through `List<ThroughInterface>`'s DECLARING type, so it
        //     is refused exactly when that branch is (wrongly) read.
        private sealed class Row { public int Value; }

        internal int EntryWithGenericLocals()
        {
            var rows = new List<Row> { new Row { Value = 1 } };
            var same = EqualityComparer<Row>.Default.Equals(rows[0], rows[0]);
            var impls = new List<ThroughInterface> { new ThroughInterface(this) };
            return rows.Count + Enumerable.Count(rows) + (same ? 1 : 0) + impls.Count;
        }

        // Deliberately unreachable from every entry point below, so the ldftn that builds the
        // delegate sits OUTSIDE the closure and a walk that only follows the ldftn instructions
        // it meets never reaches the lambda's body. That is #4311's second shape.
        private Action _seeded;
        private void Seed() { _seeded = () => { _pendingNewRow = false; }; }

        internal void EntryThroughInterface() { IIndirect i = new ThroughInterface(this); i.Go(); }
        internal void EntryThroughExplicitInterface()
        { IExplicit e = new ThroughExplicitInterface(this); e.Step(); }
        internal void EntryThroughStaticAbstract() { DispatchStatic<ThroughStaticAbstract>(this); }
        // A delegate over an INTERFACE-typed receiver emits ldvirtftn rather than ldftn; returned
        // so the delegate creation cannot be optimised away.
        internal Action EntryThroughVirtualFtn()
        { IIndirect i = new ThroughInterface(this); return i.Go; }
        internal void EntryThroughNonDelegateInvoke()
        { IAction a = new NotADelegate(this); a.Invoke(); }
        internal void EntryThroughDelegate() { _seeded?.Invoke(); }
        internal void EntryThroughDynamicInvoke() { _seeded?.DynamicInvoke(); }
        // The body moves to a compiler-generated state machine nested in this type, so the store
        // is IN the universe while the call that starts it is a plain out-of-universe `call`.
        internal async Task EntryAsync() { await Task.Yield(); _pendingNewRow = false; }
        internal void EntryDirect() { Observe(_pendingNewRow); Seed(); }
        private static void Observe(bool _) { }
    }

    private static TypeDefinition Fixture()
        => ModuleDefinition.ReadModule(typeof(BuiltInCancelIsNotADiscardTests).Assembly.Location)
           .GetTypes().Single(t => t.Name == nameof(IndirectDispatchFixture));

    private static Walk WalkFixture(string entryName)
    {
        var fixture = Fixture();
        return ClosureFrom(fixture, fixture.Methods.Single(m => m.Name == entryName));
    }

    // THE FIX, first half. An interface callvirt is followed to the implementations that could
    // run, so a store behind one is seen. Without it this is GREEN having measured nothing.
    [Fact]
    public void AStoreBehindAnInterfaceDispatchIsVisibleToTheWalk()
    {
        var stores = FlagStores(WalkFixture(nameof(IndirectDispatchFixture.EntryThroughInterface)));

        Assert.Contains(("Go()", "_pendingNewRow", true), stores);
    }

    // Anchors the Overrides clause: an explicit implementation's own name is `…IExplicit.Step`,
    // so nothing but that clause matches it. Dropping the clause reds here and nowhere else.
    [Fact]
    public void AStoreBehindAnExplicitInterfaceImplementationIsVisibleToTheWalk()
    {
        var stores = FlagStores(
            WalkFixture(nameof(IndirectDispatchFixture.EntryThroughExplicitInterface)));

        Assert.Contains(stores, s => s.Signature.EndsWith("Step()") && s.Clears
                                     && s.Field == "_pendingNewRow");
    }

    // Anchors BOTH halves of the constrained-call fix: reading the `constrained.` prefix rather
    // than the opcode, and admitting a STATIC candidate. 57 sites of this shape sit in the
    // production closure, where they were neither followed nor refused (#4320 review).
    [Fact]
    public void AStoreBehindAStaticAbstractInterfaceMemberIsVisibleToTheWalk()
    {
        var walk = WalkFixture(nameof(IndirectDispatchFixture.EntryThroughStaticAbstract));

        Assert.Contains(FlagStores(walk),
            s => s.Signature.StartsWith("Jump(") && s.Clears && s.Field == "_pendingNewRow");
        Assert.Empty(walk.Unfollowable);
    }

    // Anchors Ldvirtftn in the indirect branch. A delegate over an interface-typed receiver takes
    // the address virtually, so the operand names the bodiless interface method and only the
    // indirect branch can reach the implementation.
    [Fact]
    public void AStoreBehindAVirtualFunctionPointerIsVisibleToTheWalk()
    {
        var stores = FlagStores(WalkFixture(nameof(IndirectDispatchFixture.EntryThroughVirtualFtn)));

        Assert.Contains(("Go()", "_pendingNewRow", true), stores);
    }

    // The refusal must key on the declaring TYPE, not on the name. RecordingBuiltInAction.Invoke()
    // is itself an arity-0 Invoke, so a name-only test would refuse the guard's own entry point.
    [Fact]
    public void ANonDelegateInvokeIsFollowedRatherThanRefused()
    {
        var walk = WalkFixture(nameof(IndirectDispatchFixture.EntryThroughNonDelegateInvoke));

        Assert.Empty(walk.Unfollowable);
        Assert.Contains(("Invoke()", "_pendingNewRow", true), FlagStores(walk));
    }

    // THE FIX, second half. A delegate invocation is refused, not skipped. Asserted on the
    // refusal list rather than on the store list, because the point is precisely that the store
    // is NOT visible and must therefore not be reported as absent.
    [Fact]
    public void ADelegateInvocationIsRefusedRatherThanReadAsHoldingNoStore()
    {
        var walk = WalkFixture(nameof(IndirectDispatchFixture.EntryThroughDelegate));

        Assert.Empty(FlagStores(walk));
        Assert.Contains(walk.Unfollowable, u => u.Contains("EntryThroughDelegate"));
    }

    // DynamicInvoke is the same refusal by a different member name, and it is declared on
    // System.Delegate itself rather than on the closed delegate type — so the declaring-type test
    // needs both spellings.
    [Fact]
    public void ADynamicInvokeIsRefusedLikeAnyOtherDelegateInvocation()
    {
        var walk = WalkFixture(nameof(IndirectDispatchFixture.EntryThroughDynamicInvoke));

        Assert.Empty(FlagStores(walk));
        Assert.Contains(walk.Unfollowable, u => u.Contains("DynamicInvoke"));
    }

    // An async method's store lands in a compiler-generated state machine nested in the declarer,
    // so it is IN the universe — while the call that starts it is an out-of-universe plain `call`
    // and reaches no indirect branch at all. Neither followed nor refused was the silent skip
    // #4311 is about, one shape over (#4320 review).
    [Fact]
    public void AnAsyncStateMachineIsRefusedRatherThanSilentlySkipped()
    {
        var walk = WalkFixture(nameof(IndirectDispatchFixture.EntryAsync));

        Assert.Empty(FlagStores(walk));
        Assert.Contains(walk.Unfollowable, u => u.Contains("EntryAsync"));
    }

    // The anchor for a DELETED clause, which is the only kind of anchor an absence can have.
    // Reading the DECLARING type's generic arguments as well refused all three of these shapes
    // (3 + 1 + 2 refusals) and FLOOR 5 made that a RED on correct code. Re-add that line and this
    // reds; nothing else does, because none of these is a generic METHOD call (#4320 review).
    [Fact]
    public void AGenericLocalOverAUniverseTypeIsNotRefused()
    {
        var walk = WalkFixture(nameof(IndirectDispatchFixture.EntryWithGenericLocals));

        Assert.Empty(walk.Unfollowable);
        Assert.Empty(walk.UnresolvedInUniverse);
    }

    // The discrimination the two above need to be worth anything: the refusal is keyed on the
    // shape, not raised for every call. An entry that dispatches directly refuses nothing — even
    // though it calls Seed(), whose lambda the walk then reaches through an ordinary ldftn.
    [Fact]
    public void ADirectCallIsNeitherRefusedNorTreatedAsIndirect()
    {
        var walk = WalkFixture(nameof(IndirectDispatchFixture.EntryDirect));

        Assert.Empty(walk.Unfollowable);
        Assert.Empty(walk.UnresolvedInUniverse);
        // Matched on a substring because the lambda's name carries a compiler-allocated ordinal
        // (<Seed>b__3_0), which moves whenever a member is added above it.
        Assert.Contains(FlagStores(walk),
            s => s.Signature.Contains("Seed") && s.Field == "_pendingNewRow" && s.Clears);
    }

    // FLOOR 4, the refusal. An in-universe callee the walk could not resolve is a method it was
    // obliged to read and did not — "could not tell", which must not be spelled as the success
    // direction the way a silent `continue` spells it. This is the floor that would have caught
    // the walk's own regression: keying the lookup on FullName dropped six real generic
    // references from ClientAutoKeyValue and hid a generic discard helper at depth 1 (#4302
    // review). Deliberately scoped to the universe — an unreadable System.*/BC reference is a
    // legitimate absence and stays a pass (guards-need-a-third-state.md).
    [Fact]
    public void TheWalkResolvedEveryCalleeDeclaredInsideTheUniverse()
    {
        var unresolved = ClosureFromInvoke(LiveNavTestPage(Module())).UnresolvedInUniverse;

        Assert.True(unresolved.Count == 0,
            "The closure walk could not resolve callee(s) declared on LiveNavTestPage or a type "
            + "nested in it, so "
            + nameof(NothingInvokeCanReachClearsTheHostPendingWriteFlags)
            + " did not read them and cannot claim they hold no discard. Unresolved: "
            + string.Join("; ", unresolved.Distinct()));
    }


}
