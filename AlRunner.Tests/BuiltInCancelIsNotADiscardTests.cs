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
    /// land on (#4311): a universe method that is <c>virtual</c>, of the same arity, answering to
    /// that name — directly, or through an explicit interface implementation, whose own name is
    /// mangled (<c>Ns.IFoo.Go</c>) and so never matches directly. Always a subset of the universe,
    /// so #4302's compiler-verified accessibility bound is untouched.
    /// See docs/closure-walk-indirect-dispatch.md#scope-the-accessibility-bound-is-untouched.
    /// <para>Trap: read off the <see cref="MethodReference"/>, never its definition — it must
    /// still answer for a callee that will not resolve. And do not narrow it by the type
    /// hierarchy: that can only remove candidates, removes none today, and buys an unmeasurable.</para>
    /// </summary>
    private static IEnumerable<MethodDefinition> IndirectTargetsInUniverse(
        List<MethodDefinition> universeMethods, MethodReference callee)
        => universeMethods.Where(m =>
            m.IsVirtual && m.HasBody
            && m.Parameters.Count == callee.Parameters.Count
            && (m.Name == callee.Name || m.Overrides.Any(o => o.Name == callee.Name)));

    /// <summary>
    /// Whether a call site invokes a delegate, whose target is whatever <c>ldftn</c> built it and
    /// is therefore not a property of this instruction at all. A declaring type that will not
    /// resolve answers <c>true</c>: an unknown must not resolve toward the followable direction.
    /// </summary>
    private static bool IsDelegateInvocation(MethodReference callee)
    {
        if (callee.Name != "Invoke") return false;
        TypeDefinition declaring;
        try { declaring = callee.DeclaringType.Resolve(); }
        catch (AssemblyResolutionException) { return true; }
        return declaring is null || declaring.BaseType?.FullName == "System.MulticastDelegate";
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

                // INDIRECT DISPATCH (#4311). A virtual or interface dispatch is FOLLOWED to the
                // universe methods it could land on; a delegate invocation is REFUSED, because
                // its target is not a property of the call site. Neither may be the silent
                // `continue` that let a discard behind both read as an absence of one. `call` is
                // excluded deliberately — it is statically bound, so the resolution below is the
                // whole answer for it. docs/closure-walk-indirect-dispatch.md#what-the-fix-does
                //
                // Trap, for the next person who tries to make the delegate case followable:
                // enqueueing every address-taken universe method was measured and REDS INNOCENT
                // CODE — ActivateControl(Int32) is address-taken and legitimately clears
                // _pendingNewRow from outside the closure. Signature matching does not rescue it.
                if (op == OpCodes.Callvirt || op == OpCodes.Ldvirtftn)
                {
                    if (IsDelegateInvocation(callee))
                        unfollowable.Add($"{caller.Name} -> {callee.FullName}");
                    else
                        foreach (var target in IndirectTargetsInUniverse(universeMethods, callee))
                            Reach(target);
                }

                // Unwrap a generic instantiation to the type that declares the member, but
                // NOT an array/pointer/byref: GetElementType() happily turns `Foo[,]::Set` into
                // `Foo`, so an array of an in-universe type read as in-universe and its
                // MethodDefinition-less members then REFUSED below — a genuinely absent thing
                // (an array holds no code) spelled as unmeasurable.
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
                if (!universe.Contains(resolved.DeclaringType.FullName)) continue;

                // Bodiless by construction — an interface member, an abstract override, an extern.
                // There is nothing to walk FROM here; what actually runs was enqueued above by
                // IndirectTargetsInUniverse, which is why this skip is no longer the hole #4311
                // reported.
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
            public void Go() { _fixture._pendingNewRow = false; }
        }

        // Deliberately unreachable from every entry point below, so the ldftn that builds the
        // delegate sits OUTSIDE the closure and a walk that only follows the ldftn instructions
        // it meets never reaches the lambda's body. That is #4311's second shape.
        private Action _seeded;
        private void Seed() { _seeded = () => { _pendingNewRow = false; }; }

        internal void EntryThroughInterface() { IIndirect i = new ThroughInterface(this); i.Go(); }
        internal void EntryThroughDelegate() { _seeded?.Invoke(); }
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
