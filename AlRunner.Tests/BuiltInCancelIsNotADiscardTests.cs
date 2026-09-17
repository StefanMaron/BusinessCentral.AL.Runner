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
    // (method, field) rather than by method, so a flush clearing the OTHER flag — which would
    // drop a write nothing in that method is about to perform — is still an offender.
    // Both pairs are asserted to be OCCUPIED below: an exemption whose store has gone is a
    // licence left lying around for a future discard to be written under.
    private static readonly (string Method, string Field)[] FlushMayClearItsOwnFlag =
    {
        ("FlushPendingNewRow", "_pendingNewRow"),
        ("FlushPendingModify", "_pendingModify"),
    };

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
    private static List<MethodDefinition> FlagWritableUniverse(TypeDefinition host)
    {
        var methods = new List<MethodDefinition>();
        var pending = new Stack<TypeDefinition>();
        pending.Push(host);
        while (pending.Count > 0)
        {
            var type = pending.Pop();
            methods.AddRange(type.Methods);
            foreach (var nested in type.NestedTypes) pending.Push(nested);
        }
        return methods;
    }

    /// <summary>
    /// Everything Invoke() can reach within that universe, transitively — 12 of the 111 methods
    /// as measured on #4302's fix, so the walk is over a single type's own call graph and costs
    /// milliseconds, not a repository-wide closure.
    /// <para>Trap: follow <c>ldftn</c>/<c>ldvirtftn</c> as well as the call opcodes. A lambda is
    /// reached through a delegate whose declaring type is <c>System.Func</c>, which this walk
    /// stops at, so dropping those two would let a body inside a generated closure type sit in
    /// the universe unvisited.</para>
    /// </summary>
    private static Dictionary<string, MethodDefinition> ClosureFromInvoke(TypeDefinition host)
    {
        var universe = FlagWritableUniverse(host)
            .Where(m => m.HasBody)
            .GroupBy(m => m.FullName)
            .ToDictionary(g => g.Key, g => g.First());

        var reached = new Dictionary<string, MethodDefinition>();
        var queue = new Queue<MethodDefinition>();
        var invoke = BuiltInActionInvoke(host);
        reached[invoke.FullName] = invoke;
        queue.Enqueue(invoke);

        while (queue.Count > 0)
            foreach (var instruction in queue.Dequeue().Body.Instructions)
            {
                var op = instruction.OpCode;
                if (op != OpCodes.Call && op != OpCodes.Callvirt && op != OpCodes.Newobj
                    && op != OpCodes.Ldftn && op != OpCodes.Ldvirtftn) continue;
                if (instruction.Operand is not MethodReference callee) continue;
                if (!universe.TryGetValue(callee.FullName, out var resolved)) continue;
                if (reached.ContainsKey(resolved.FullName)) continue;
                reached[resolved.FullName] = resolved;
                queue.Enqueue(resolved);
            }

        return reached;
    }

    /// <summary>
    /// Every store to a pending-write flag Invoke() can reach, as (method, field, clears). A
    /// store is a CLEAR when the value pushed is literal <c>false</c>, a SET when it is literal
    /// <c>true</c> — measured, not assumed: Roslyn emits ldc.i4.0/ldc.i4.1 before each of the
    /// four stores in this type today. Anything else is unclassifiable from IL alone and is
    /// reported as a clear, because an unknown must not resolve toward the success state
    /// (guards-need-a-third-state.md).
    /// </summary>
    private static List<(string Method, string Field, bool Clears)> FlagStoresUnderInvoke()
    {
        var host = LiveNavTestPage(Module());
        var stores = new List<(string, string, bool)>();

        foreach (var method in ClosureFromInvoke(host).Values.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            Instruction previous = null;
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode == OpCodes.Stfld
                    && instruction.Operand is FieldReference field
                    && PendingWriteFlags.Contains(field.Name))
                    stores.Add((method.Name, field.Name, previous?.OpCode != OpCodes.Ldc_I4_1));
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
            .Where(s => s.Clears && !FlushMayClearItsOwnFlag.Contains((s.Method, s.Field)))
            .Select(s => $"{s.Method} clears {s.Field}")
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

        foreach (var (method, field) in FlushMayClearItsOwnFlag)
            Assert.True(clears.Any(c => c.Method == method && c.Field == field),
                $"'{method}' no longer clears '{field}' anywhere Invoke() can reach. Either the "
                + "walk no longer reaches it — in which case "
                + nameof(NothingInvokeCanReachClearsTheHostPendingWriteFlags)
                + " is scanning a collapsed closure and passes having measured nothing — or the "
                + "clear has moved, and the exemption must move with it rather than stay behind "
                + "as a licence. Clears actually found: "
                + (clears.Count == 0 ? "(none)" : string.Join("; ", clears.Select(c => $"{c.Method}->{c.Field}"))));
    }
}
