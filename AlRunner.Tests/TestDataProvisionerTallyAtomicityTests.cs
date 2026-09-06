// TestDataProvisionerTallyAtomicityTests — the mechanism guard for issues #2997 and #3025.
//
// WHAT IS UNDER TEST, AND WHAT THIS DELIBERATELY DOES NOT CLAIM
//   TestDataProvisioner's --test-data tallies are written from LoadOnDemand and
//   NoteDeferredLoadWrittenOff, which two threads can be inside at once — TestExecutor's
//   InvokeWithTimeout runs every [Test] on its own worker thread and does NOT kill it when the
//   watchdog expires (thread.Join(timeout)), so an abandoned thread keeps hydrating while the
//   bundle loop carries on in the same process (the route #2914 established).
//
//   #2997 was the lost update: `int++` from two threads drops counts. #3025 was the torn SET:
//   six individually-atomic counters read one at a time yield a summary whose numbers were each
//   true and whose combination never was.
//
//   These counters are DIAGNOSTICS. Nothing branches on them: they are the numbers the
//   --test-data summary prints, plus DeferredLoadsWrittenOff, which one test asserts. But
//   --test-data is what a person uses to judge whether a bucket run's failures are defects or
//   missing setup data, so a summary that contradicts itself costs a real investigation.
//
//   Neither an off-by-one under a genuine race nor a specific interleaving is deterministically
//   forceable, so this file does NOT try. It pins the MECHANISM over the compiled IL of the
//   loaded al-runner.dll. The behavioural halves live next door and state their own limits:
//     - TestDataSummarySnapshotTests — a reader and writers over one board, asserting that every
//       summary describes a state that existed (#3025).
//     - TestDataLazyLoadPolicyTests.TheWriteOffTally_IsExactUnderConcurrentWriteOffs — eight
//       threads through the real write-off path, asserting the exact total (#2997).
//
//   The IL is read rather than the source text because there is no reflection surface for "how
//   was this field written", and a source-text scan of this repo has failed under Linux CI
//   before (see InstallBaselineAppendConcurrencyTests, which pins #2914's walkers the same way).
using AlRunner;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestDataProvisionerTallyAtomicityTests
{
    private const string BoardTypeName = "TallyBoard";

    /// <summary>The one remaining standalone static tally. The six summary counts are no longer
    /// separate fields — they are properties of the immutable value TallyBoard swaps (#3025), so
    /// "someone adds a seventh count and writes it unsynchronised" is structurally impossible
    /// for those; a new property rides the same CompareExchange. This one is still a plain
    /// static int because nothing is ever read alongside it.</summary>
    public static TheoryData<string> StandaloneTallies => new() { "_deferredLoadsWrittenOff" };

    private static AssemblyDefinition Assembly()
        => AssemblyDefinition.ReadAssembly(typeof(TestDataProvisioner).Assembly.Location);

    private static TypeDefinition Provisioner(AssemblyDefinition asm)
    {
        var type = asm.MainModule.GetType(typeof(TestDataProvisioner).FullName);
        Assert.True(type != null, "TestDataProvisioner not found in the loaded al-runner.dll");
        return type!;
    }

    private static TypeDefinition Board(TypeDefinition provisioner)
    {
        var board = provisioner.NestedTypes.SingleOrDefault(t => t.Name == BoardTypeName);
        Assert.True(board != null,
            $"{BoardTypeName} is gone from TestDataProvisioner — the --test-data tallies have "
            + "been restructured and this guard no longer covers them (#3025).");
        return board!;
    }

    /// <summary>A type plus everything nested inside it at any depth: a lambda or a closure
    /// writing a tally is still a write to a tally, and `c =&gt; c with { … }` compiles to one.</summary>
    private static IEnumerable<TypeDefinition> WithNested(TypeDefinition t)
        => new[] { t }.Concat(t.NestedTypes.SelectMany(WithNested));

    private static IEnumerable<MethodDefinition> AllMethods(TypeDefinition type)
        => WithNested(type).SelectMany(t => t.Methods).Where(m => m.HasBody);

    private static bool Touches(Instruction i, OpCode op, string field)
        => i.OpCode == op && i.Operand is FieldReference fr && fr.Name == field;

    // ───────────────────────────────────────── the standalone static tally (#2997) ──

    /// <summary>
    /// `x++` and `x += n` on a static field compile to ldsfld/add/stsfld — a read-modify-write
    /// two --test-data hydration threads can interleave — so the ABSENCE of stsfld outside the
    /// reset is the absence of the defect. ResetForTests is the one legitimate plain writer and
    /// is asserted as such below, so this cannot be satisfied by deleting the reset.
    /// </summary>
    [Theory]
    [MemberData(nameof(StandaloneTallies))]
    public void NoMethodButResetForTests_StoresAStandaloneTallyDirectly(string field)
    {
        using var asm = Assembly();
        var offenders = AllMethods(Provisioner(asm))
            .Where(m => m.Name != nameof(TestDataProvisioner.ResetForTests))
            .Where(m => m.Body.Instructions.Any(i => Touches(i, OpCodes.Stsfld, field)))
            .Select(m => m.Name)
            .ToArray();

        Assert.True(offenders.Length == 0,
            $"{field} is written with a plain stsfld by: {string.Join(", ", offenders)}. "
            + "That is a read-modify-write two --test-data hydration threads can interleave "
            + "(#2997); use Interlocked.Increment/Add.");
    }

    /// <summary>
    /// Second half, and the one that makes the first non-vacuous: the tally really is still
    /// counted, and counted atomically. A "fix" that simply deleted every increment would pass
    /// the stsfld check above and fail here.
    /// </summary>
    [Theory]
    [MemberData(nameof(StandaloneTallies))]
    public void EveryStandaloneTally_IsMutatedThroughInterlocked(string field)
    {
        using var asm = Assembly();
        var sites = AllMethods(Provisioner(asm))
            .SelectMany(m => m.Body.Instructions)
            .Count(i => Touches(i, OpCodes.Ldsflda, field) && ConsumedByInterlocked(i));

        Assert.True(sites > 0,
            $"{field} is never passed to System.Threading.Interlocked, so either it is no "
            + "longer counted at all or it is counted by some other unsynchronised means (#2997).");
    }

    /// <summary>
    /// ResetForTests stays a plain store, deliberately: it runs between runs with no other
    /// thread in play, and routing it through Interlocked would suggest a concurrency it does
    /// not have. Asserted so the exemption in the first test is a stated decision rather than a
    /// hole — if the reset ever stops writing this field, the first test goes vacuous and this
    /// one says so.
    /// </summary>
    [Theory]
    [MemberData(nameof(StandaloneTallies))]
    public void ResetForTests_StillClearsEveryStandaloneTally(string field)
    {
        using var asm = Assembly();
        var reset = Provisioner(asm).Methods.Single(m => m.Name == nameof(TestDataProvisioner.ResetForTests));
        Assert.True(reset.Body.Instructions.Any(i => Touches(i, OpCodes.Stsfld, field)),
            $"ResetForTests no longer clears {field}, so a stale count survives into the next run.");
    }

    // ─────────────────────────────────────────── the summary tallies (#2997, #3025) ──

    /// <summary>
    /// The board's state is published only by Interlocked.CompareExchange. A plain stfld outside
    /// the constructor is either a lost update (#2997) or a half-applied set some reader can
    /// observe (#3025); inside the constructor it is neither, because nothing else can reach the
    /// object yet.
    /// </summary>
    [Fact]
    public void TheBoardState_IsPublishedOnlyByCompareExchange()
    {
        using var asm = Assembly();
        var board = Board(Provisioner(asm));
        var stateFields = board.Fields.Where(f => !f.IsStatic).Select(f => f.Name).ToArray();

        Assert.True(stateFields.Length == 1,
            "TallyBoard should hold its counts as ONE value so they can be read as a set "
            + $"(#3025); it now has {stateFields.Length} instance field(s): "
            + string.Join(", ", stateFields));

        var state = stateFields[0];
        var offenders = AllMethods(board)
            .Where(m => m.Name != ".ctor")
            .Where(m => m.Body.Instructions.Any(i => Touches(i, OpCodes.Stfld, state)))
            .Select(m => m.FullName)
            .ToArray();

        Assert.True(offenders.Length == 0,
            $"TallyBoard.{state} is written with a plain stfld by: {string.Join(", ", offenders)}. "
            + "Two --test-data hydration threads can be in there at once (#2997), and a partial "
            + "write is a set a reader can observe mid-update (#3025).");

        var casSites = AllMethods(board)
            .SelectMany(m => m.Body.Instructions)
            .Count(i => Touches(i, OpCodes.Ldflda, state) && ConsumedByInterlocked(i));

        Assert.True(casSites > 0,
            $"TallyBoard.{state} is never passed to System.Threading.Interlocked, so the counts "
            + "are either no longer kept or kept by some other unsynchronised means.");
    }

    /// <summary>
    /// #3025 itself: taking the summary reads the board's state EXACTLY ONCE. Against the code
    /// this replaced, Capture held six separate Volatile.Reads of six separate counters — six
    /// field loads, six chances for another thread's hydration to land in between, and a
    /// reported combination that never existed at any instant. One load is the fix.
    /// </summary>
    [Fact]
    public void TakingTheSummary_ReadsTheBoardStateExactlyOnce()
    {
        using var asm = Assembly();
        var board = Board(Provisioner(asm));
        var capture = board.Methods.SingleOrDefault(m => m.Name == "Capture" && m.HasBody);
        Assert.True(capture != null, "TallyBoard.Capture is gone — the summary is built elsewhere now (#3025).");

        var stateFields = board.Fields.Where(f => !f.IsStatic).Select(f => f.Name).ToHashSet();
        var reads = capture!.Body.Instructions.Count(i =>
            (i.OpCode == OpCodes.Ldfld || i.OpCode == OpCodes.Ldflda)
            && i.Operand is FieldReference fr && stateFields.Contains(fr.Name));

        Assert.Equal(1, reads);
    }

    /// <summary>
    /// The reset swaps the whole board rather than clearing counts one at a time — one reference
    /// store, so a thread still hydrating cannot be handed a half-cleared set. Clearing in place
    /// would be the same defect #3025 fixed, arriving from the other direction.
    /// </summary>
    [Fact]
    public void ResetForTests_ReplacesTheWholeBoard()
    {
        using var asm = Assembly();
        var provisioner = Provisioner(asm);
        var boardField = provisioner.Fields.SingleOrDefault(f => f.IsStatic && f.FieldType.Name == BoardTypeName);
        Assert.True(boardField != null, "TestDataProvisioner no longer holds a TallyBoard (#3025).");

        var reset = provisioner.Methods.Single(m => m.Name == nameof(TestDataProvisioner.ResetForTests));
        var replaced = reset.Body.Instructions.Any(i =>
            Touches(i, OpCodes.Stsfld, boardField!.Name)
            && i.Previous?.OpCode == OpCodes.Newobj);

        Assert.True(replaced,
            $"ResetForTests no longer replaces {boardField!.Name} with a fresh TallyBoard. "
            + "Clearing the counts in place is several stores a concurrent reader can land "
            + "between, which is exactly the skew #3025 removed.");
    }

    /// <summary>
    /// The same shape one level up, found while fixing #3025: the armed plan is a mutable
    /// reference field, and a method that loads it more than once in one expression is testing
    /// or reporting several different plans. The bundle loop re-arms it between app groups and
    /// ResetForTests nulls it, while an abandoned hydration thread is still reading it (#2914),
    /// so `_armed == null ? null : (_armed.Backup, _armed.Company)` could null-reference after a
    /// null check that had just passed, or pair one plan's backup with the next one's company.
    ///
    /// Discovered rather than listed by name, so a new reader of the plan is covered the day it
    /// is written. Assertion is on the field the plan lives in, not on any method list.
    /// </summary>
    [Fact]
    public void NoMethod_ReadsTheArmedPlanMoreThanOnce()
    {
        using var asm = Assembly();
        var provisioner = Provisioner(asm);
        var armed = provisioner.Fields.SingleOrDefault(f => f.IsStatic && f.FieldType.Name == "ArmedPlan");
        Assert.True(armed != null, "TestDataProvisioner no longer holds an ArmedPlan field.");

        var offenders = AllMethods(provisioner)
            .Select(m => (m.FullName, Loads: m.Body.Instructions.Count(i =>
                (i.OpCode == OpCodes.Ldsfld || i.OpCode == OpCodes.Ldsflda)
                && i.Operand is FieldReference fr && fr.Name == armed!.Name)))
            .Where(x => x.Loads > 1)
            .Select(x => $"{x.FullName} ({x.Loads} loads)")
            .ToArray();

        Assert.True(offenders.Length == 0,
            $"{armed!.Name} is loaded more than once by: {string.Join(", ", offenders)}. "
            + "Copy it to a local first — between two loads it can be re-armed for the next app "
            + "group or nulled by the reset (#3025).");
    }

    /// <summary>Interlocked.Increment/Add/CompareExchange take a `ref`, which is ldsflda/ldflda,
    /// and no other shape in this type does. Add and CompareExchange push further arguments
    /// first — and those can be calls (a property getter on the hydration result, `c with { … }`)
    /// — so scan a short window rather than requiring the next instruction. Another field
    /// address appearing first ends the window: that is a different call's argument.</summary>
    private static bool ConsumedByInterlocked(Instruction from)
    {
        for (var next = from.Next; next != null; next = next.Next)
        {
            if (next.OpCode == OpCodes.Ldsflda || next.OpCode == OpCodes.Ldflda) return false;
            if ((next.OpCode == OpCodes.Call || next.OpCode == OpCodes.Callvirt)
                && next.Operand is MethodReference mr
                && mr.DeclaringType.FullName == "System.Threading.Interlocked")
                return true;
            if (next.Offset - from.Offset > 60) return false;   // bounded: one call, not a block
        }
        return false;
    }
}
