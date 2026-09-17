// BuiltInCancelIsNotADiscardTests — issue #4295.
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
// ABSENCE, which no return value exposes: the built-in action's Invoke() must not clear those
// two flags on any path, directly or through anything it calls. A regression that reinstates
// the discard — as a helper call or inlined into Invoke — restores the exact defect while every
// unit-level return value stays the same, so the flag stores are read out of the IL.
//
// The three positive assertions are the population floor. Without them a renamed field or a
// gutted Invoke() would make the scan find nothing to complain about and pass having measured
// nothing (guards-need-a-third-state.md).
//
// The other half of this fix's dependency is already pinned elsewhere: Cancel now relies on
// Dispose() to write what it left pending, and TestPageModalClosePartFlushTests
// .DisposeMustFlushBothPartsAndItsOwnRow fails if either of Dispose()'s two flush calls goes.
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

    private static TypeDefinition LiveNavTestPage()
    {
        // Any type from the runner assembly locates the image; LiveNavTestPage is internal and
        // RecordingBuiltInAction is a private nested type, so both are read through Cecil
        // rather than through the type system.
        var path = typeof(AlRunner.TestExecutor).Assembly.Location;
        var module = ModuleDefinition.ReadModule(path);
        return module.GetTypes().Single(t => t.FullName == "AlRunner.LiveNavTestPage");
    }

    private static MethodDefinition BuiltInActionInvoke()
    {
        var nested = LiveNavTestPage().NestedTypes.Single(t => t.Name == "RecordingBuiltInAction");
        return nested.Methods.Single(m => m.Name == "Invoke" && m.Parameters.Count == 0);
    }

    private static bool CallsMethodNamed(MethodDefinition method, string calleeName)
        => method.Body.Instructions.Any(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            && i.Operand is MethodReference callee && callee.Name == calleeName);

    // Every method Invoke() calls that is declared on LiveNavTestPage itself, plus Invoke().
    // One level is the right depth: FlushRow() delegates to FlushPendingNewRow/FlushPendingModify,
    // which clear their own flag ON ENTRY and then write the row — legitimate, and deliberately
    // out of range. A discard reinstated as a helper would be called from Invoke() directly and
    // so lands inside this set.
    private static IEnumerable<MethodDefinition> InvokeAndItsDirectHostCallees()
    {
        var invoke = BuiltInActionInvoke();
        yield return invoke;

        var host = LiveNavTestPage();
        var seen = new HashSet<string>();
        foreach (var instruction in invoke.Body.Instructions)
        {
            if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt) continue;
            if (instruction.Operand is not MethodReference callee) continue;
            if (callee.DeclaringType?.FullName != host.FullName) continue;
            if (!seen.Add(callee.FullName)) continue;

            var resolved = host.Methods.FirstOrDefault(m => m.FullName == callee.FullName);
            if (resolved is { HasBody: true }) yield return resolved;
        }
    }

    // THE REGRESSION ROW. Reinstating the discard — however it is spelled — fails here.
    [Fact]
    public void BuiltInActionInvoke_NeverClearsTheHostPendingWriteFlags()
    {
        var offenders = new List<string>();

        foreach (var method in InvokeAndItsDirectHostCallees())
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode != OpCodes.Stfld) continue;
                if (instruction.Operand is not FieldReference field) continue;
                if (!PendingWriteFlags.Contains(field.Name)) continue;
                offenders.Add($"{method.Name} stores into {field.Name}");
            }

        Assert.True(offenders.Count == 0,
            "The built-in OK/Cancel action must not clear the host page's pending-write flags: "
            + "BC's Cancel is not a discard (corpus 60535, issue #4295), and a cleared flag makes "
            + "Dispose()'s FlushRow() write nothing. Offending stores: "
            + string.Join("; ", offenders));
    }

    // FLOOR 1. A renamed or deleted flag would make the scan above search for nothing and pass.
    [Fact]
    public void TheHostPendingWriteFlagsStillExistUnderTheNamesTheScanReads()
    {
        var fields = LiveNavTestPage().Fields.Select(f => f.Name).ToHashSet();

        foreach (var flag in PendingWriteFlags)
            Assert.True(fields.Contains(flag),
                $"LiveNavTestPage no longer declares '{flag}', so "
                + nameof(BuiltInActionInvoke_NeverClearsTheHostPendingWriteFlags)
                + " is scanning for a field that cannot appear and would pass having measured "
                + "nothing. Rename it in PendingWriteFlags too.");
    }

    // FLOOR 2. An Invoke() that no longer reaches the flush, or no longer attempts the close,
    // is a different method than the one the absence above is claimed about.
    [Fact]
    public void BuiltInActionInvoke_StillFlushesTheRowAndAttemptsTheClose()
    {
        var invoke = BuiltInActionInvoke();

        Assert.True(CallsMethodNamed(invoke, "FlushRow"),
            "Invoke() must still flush the host row for the confirming built-ins — OK's row is "
            + "written here and by nothing else before the test reads the table (#3640).");
        Assert.True(CallsMethodNamed(invoke, "FlushParts"),
            "Invoke() must still flush the parts before the host row on OK: a header OnModify "
            + "reads the part's lines (#4146, corpus 60760).");
        Assert.True(CallsMethodNamed(invoke, "AttemptHandlerDrivenClose"),
            "Invoke() must still make the close attempt a built-in press makes on BC (#3593).");
    }
}
