// BuiltInCancelIsNotADiscardTests — issues #4295, #4302.
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
    {
        var names = new HashSet<string>();
        var pending = new Stack<TypeDefinition>();
        pending.Push(host);
        while (pending.Count > 0)
        {
            var type = pending.Pop();
            names.Add(type.FullName);
            foreach (var nested in type.NestedTypes) pending.Push(nested);
        }
        return names;
    }

    /// <summary>
    /// What Invoke() reaches within that universe by direct and delegate calls, transitively —
    /// 15 of the type's 111 methods, max depth 7, so the walk is one type's own call graph and costs
    /// milliseconds rather than a repository-wide closure. Also reports every in-universe callee
    /// it could NOT resolve, which is a broken measurement rather than an absence of stores.
    /// <para>Trap: resolve through <see cref="MethodReference.Resolve"/> — a call to a GENERIC
    /// method carries the instantiated name (<c>CalculateClientAutoKey&lt;System.Int32&gt;</c>), so
    /// matching FullName against the definitions dropped six real references here and made a
    /// generic discard helper invisible at depth 1 (#4302 review). Follow ldftn/ldvirtftn too: a
    /// lambda is reached through a delegate whose declaring type this walk stops at.</para>
    /// </summary>
    private static (Dictionary<string, MethodDefinition> Reached, List<string> UnresolvedInUniverse)
        ClosureFromInvoke(TypeDefinition host)
    {
        var universe = FlagWritableUniverse(host);
        var reached = new Dictionary<string, MethodDefinition>();
        var unresolved = new List<string>();
        var queue = new Queue<MethodDefinition>();
        var invoke = BuiltInActionInvoke(host);
        reached[invoke.FullName] = invoke;
        queue.Enqueue(invoke);

        while (queue.Count > 0)
        {
            var caller = queue.Dequeue();
            foreach (var instruction in caller.Body.Instructions)
            {
                var op = instruction.OpCode;
                if (op != OpCodes.Call && op != OpCodes.Callvirt && op != OpCodes.Newobj
                    && op != OpCodes.Ldftn && op != OpCodes.Ldvirtftn) continue;
                if (instruction.Operand is not MethodReference callee) continue;

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

                // Known hole, and this is the line it lives on: a call dispatched INDIRECTLY —
                // through an interface, or a delegate whose ldftn sits outside the closure —
                // resolves to a bodiless or unreached method and is dropped here, so the
                // implementation is never walked (#4311). Dormant rather than absent: the closure
                // holds 58 interface-dispatch sites, and none of them resolves to an implementer
                // INSIDE the universe (57 System.Numerics constrained-generic-math calls, one
                // IDisposable.Dispose from FlushParts' foreach), so none can hold the stfld.
                // Re-measure that before relying on it. The occupancy floor covers the case where
                // the indirection lands on the flush path. NOT the accessibility bound's doing:
                // that region the compiler excludes (CS0122); this is reachable code not followed.
                if (!resolved.HasBody) continue;

                if (reached.ContainsKey(resolved.FullName)) continue;
                reached[resolved.FullName] = resolved;
                queue.Enqueue(resolved);
            }
        }

        return (reached, unresolved);
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
        var stores = new List<(string, string, bool)>();

        foreach (var method in ClosureFromInvoke(host).Reached.Values
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
