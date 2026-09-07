// InstrumentationCounterPairSnapshotTests — the proving tests for #3169, the #3025 shape at
// two more sites: a diagnostic that prints several counters read one at a time can print a
// combination that never existed. Here the pairs have a real invariant to violate — the
// second number is a subset of the first (BC MethodLoad events ⊆ MethodLoad events; calls
// that did work ⊆ calls made) — so an inversion is provably impossible at any single instant
// and provably possible from two loads.
//
// The behavioural tests are hammers, not forced interleavings: there is no seam that pins the
// reader between two loads. Against the pre-fix code they reproduced within a second, every
// run (163 of 511,095 listener snapshots; 56,845 of 1,937,753 dap snapshots inverted). After
// the fix a snapshot is one load of one value, so there is no interleaving left to hit —
// green is a proof, red was an observation.
//
// The IL guards pin the read half the way TestDataProvisionerTallyAtomicityTests does for
// #3025: the dump line and the listener snapshot take each pair through exactly one
// CountPair.Read, and the three subsystems keep no standalone integer counter a future edit
// could print alongside it.
using AlRunner.Infrastructure;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class InstrumentationCounterPairSnapshotTests
{
    // ───────────────────────────────────────────────────────── behavioural hammers ──

    private static (long Inversions, long Reads) Hammer(
        Action bump, Func<(long Total, long Part)> read, int milliseconds = 1500)
    {
        using var stop = new CancellationTokenSource(milliseconds);
        var writers = Enumerable.Range(0, Math.Max(2, Environment.ProcessorCount - 1))
            .Select(_ => new Thread(() => { while (!stop.IsCancellationRequested) bump(); }))
            .ToArray();
        foreach (var w in writers) w.Start();
        long inversions = 0, reads = 0;
        while (!stop.IsCancellationRequested)
        {
            var (total, part) = read();
            reads++;
            if (part > total) inversions++;
        }
        foreach (var w in writers) w.Join();
        return (inversions, reads);
    }

    /// <summary>The mechanism itself: writers bump Total then Part; a reader must never see
    /// Part ahead of Total. Mutating Read back to two loads of two fields fails this.</summary>
    [Fact]
    public void CountPair_ReadNeverReportsPartAboveTotal()
    {
        var pair = new CountPair();
        var (inversions, reads) = Hammer(
            () => { pair.IncrementTotal(); pair.IncrementPart(); },
            () => { var s = pair.Read(); return (s.Total, s.Part); });
        Assert.True(reads > 0);
        Assert.True(inversions == 0, $"{inversions} of {reads} reads reported Part > Total");
    }

    /// <summary>Both halves land at once and the returned snapshot is the post-add state, so
    /// the JIT callback's verbose-log cap reads the BC count it just produced.</summary>
    [Fact]
    public void CountPair_AddPublishesBothHalvesTogether()
    {
        var pair = new CountPair();
        Assert.Equal(new CountPair.Snapshot(1, 0), pair.Add(1, 0));
        Assert.Equal(new CountPair.Snapshot(2, 1), pair.Add(1, 1));
        Assert.Equal(new CountPair.Snapshot(2, 4), pair.Add(0, 3));
        pair.ClearPart();
        Assert.Equal(new CountPair.Snapshot(2, 0), pair.Read());
    }

    /// <summary>Site 1: the Spike4 ProcessExit summary reads while the EventPipe callback
    /// thread is still counting; it must never print more BC MethodLoad events than
    /// MethodLoad events in total.</summary>
    [Fact]
    public void MethodLoadTallies_NeverReportMoreBcEventsThanEventsInTotal()
    {
        // Constructing the listener subscribes it to the runtime's real JIT events, so genuine
        // non-BC MethodLoads are counted alongside the hammer's BC ones — Total > Bc is expected.
        using var listener = new EventPipeJitListener();
        var (inversions, reads) = Hammer(
            () => listener.CountMethodLoad(isBc: true),
            () => { var s = listener.SnapshotCounters(); return (s.Total, s.Bc); });
        Assert.True(reads > 0);
        Assert.True(inversions == 0,
            $"{inversions} of {reads} snapshots reported more BC MethodLoad events than MethodLoad events in total");
        var final = listener.SnapshotCounters();
        Assert.True(final.Bc > 0 && final.Bc <= final.Total, $"expected 0 < Bc <= Total, got {final}");
    }

    /// <summary>Site 2, the clearest pair on the dump line: OnStmtHit increments the call
    /// count, passes the Enabled gate, then increments the work count. Driven through the real
    /// hook with int.MaxValue, which returns right after the work increment and before the
    /// scope is touched, so a null scope is safe.</summary>
    [Fact]
    public void DapCounters_NeverReportMoreWorkThanCalls()
    {
        var was = AlDapSession.Enabled;
        AlDapSession.Enabled = true;
        try
        {
            var (inversions, reads) = Hammer(
                () => AlDapSession.OnStmtHit(null!, int.MaxValue),
                () => { var s = AlDapSession.Counts.Read(); return (s.Total, s.Part); });
            Assert.True(reads > 0);
            Assert.True(inversions == 0,
                $"{inversions} of {reads} snapshots reported WorkPerformedCount > CallCount");
        }
        finally { AlDapSession.Enabled = was; }
    }

    /// <summary>The halves are one packed word, not two independent counters, so they cannot
    /// disagree below the carry boundary — the property the class doc claims. Seeded to one
    /// short of 2^32 in Part with three Adds rather than driven there one increment at a time
    /// (4.29e9 calls is not a unit test), then walked across: Part rolls to 0 and the carry
    /// lands in Total. A design with two separate 32-bit counters would leave Total at 0.</summary>
    [Fact]
    public void CountPair_CarryOutOfPartPropagatesIntoTotal_NotAnIndependentWrap()
    {
        const long PartCapacity = 1L << 32;
        var pair = new CountPair();

        // One short of the boundary: Part holds the full 32-bit range, Total is untouched.
        Assert.Equal(new CountPair.Snapshot(0, PartCapacity - 1), pair.Add(0, PartCapacity - 1));

        // The increment that crosses it. Part does NOT wrap in isolation.
        var crossed = pair.IncrementPart();
        Assert.Equal(new CountPair.Snapshot(1, 0), crossed);
        Assert.Equal(crossed, pair.Read());

        // ClearPart leaves the carried Total alone, so the borrowed count is not recoverable
        // by resetting the low half — this is a one-way trade, not a transient skew.
        pair.ClearPart();
        Assert.Equal(new CountPair.Snapshot(1, 0), pair.Read());
    }

    /// <summary>The negative direction of the same mechanism: below the boundary the two
    /// halves are strictly independent, which is why every reachable count is correct. A
    /// large Part and a large Total coexist with no interference at all.</summary>
    [Fact]
    public void CountPair_BelowTheCarryBoundary_TheHalvesDoNotInterfere()
    {
        var pair = new CountPair();
        Assert.Equal(new CountPair.Snapshot(0, uint.MaxValue), pair.Add(0, uint.MaxValue - 0));
        pair.ClearPart();

        // Total counts far past anything an AL run reaches while Part stays exact.
        Assert.Equal(new CountPair.Snapshot(1_000_000, 999_999), pair.Add(1_000_000, 999_999));
        Assert.Equal(new CountPair.Snapshot(1_000_001, 1_000_000), pair.Add(1, 1));
        Assert.True(pair.Read().Part <= pair.Read().Total);
    }

    // ────────────────────────────────────────────────────────────────── IL guards ──

    private static AssemblyDefinition Assembly()
        => AssemblyDefinition.ReadAssembly(typeof(CountPair).Assembly.Location);

    private static IEnumerable<TypeDefinition> WithNested(TypeDefinition t)
        => new[] { t }.Concat(t.NestedTypes.SelectMany(WithNested));

    private static IEnumerable<MethodDefinition> AllMethods(TypeDefinition type)
        => WithNested(type).SelectMany(t => t.Methods).Where(m => m.HasBody);

    private static readonly string[] DumpLineSubsystems =
        { typeof(AlCoverageTracker).FullName!, typeof(AlValueCapture).FullName!, typeof(AlDapSession).FullName! };

    private static bool IsCountPairRead(Instruction i)
        => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference mr
           && mr.DeclaringType.FullName == typeof(CountPair).FullName && mr.Name == nameof(CountPair.Read);

    /// <summary>The AL_RUNNER_DUMP_INSTRUMENTATION_COUNTERS line takes each subsystem's pair
    /// through exactly one CountPair.Read and touches nothing else on those types — no
    /// counter field, no Collect(), no dictionary-derived bool that a second load could
    /// contradict. Located by its own literal, so it can live in whatever method top-level
    /// statements compile to.</summary>
    [Fact]
    public void TheDumpLine_ReadsEachPairExactlyOnceAndNothingElse()
    {
        using var asm = Assembly();
        var dumpMethods = asm.MainModule.Types.SelectMany(AllMethods)
            .Where(m => m.Body.Instructions.Any(i =>
                i.OpCode == OpCodes.Ldstr && i.Operand is string s && s.Contains("[instrumentation-counters]")))
            .ToArray();
        Assert.True(dumpMethods.Length == 1,
            $"expected one method to print [instrumentation-counters], found {dumpMethods.Length}: "
            + string.Join(", ", dumpMethods.Select(m => m.FullName)));
        var all = dumpMethods[0].Body.Instructions;
        // Top-level statements compile into one very large method that elsewhere enables
        // coverage and collects it for --coverage; bound the scan to the dump block itself,
        // from the env-var literal to the WriteLine after the line's own literal.
        int start = all.Select((i, n) => (i, n)).First(x =>
            x.i.OpCode == OpCodes.Ldstr && (string)x.i.Operand == "AL_RUNNER_DUMP_INSTRUMENTATION_COUNTERS").n;
        int lineAt = all.Select((i, n) => (i, n)).First(x =>
            x.i.OpCode == OpCodes.Ldstr && ((string)x.i.Operand).Contains("[instrumentation-counters]")).n;
        int end = all.Select((i, n) => (i, n)).First(x => x.n > lineAt
            && x.i.Operand is MethodReference { Name: "WriteLine" }).n;
        Assert.True(start < lineAt && lineAt < end, $"dump block not bounded: {start} < {lineAt} < {end}");
        var body = all.Skip(start).Take(end - start + 1).ToArray();

        Assert.Equal(DumpLineSubsystems.Length, body.Count(IsCountPairRead));

        var otherTouches = body
            .Where(i => i.Operand is MemberReference { DeclaringType: not null } mr
                        && DumpLineSubsystems.Contains(mr.DeclaringType.FullName)
                        && mr.Name != "Counts")
            .Select(i => $"{i.OpCode} {((MemberReference)i.Operand).FullName}")
            .Distinct()
            .ToArray();
        Assert.True(otherTouches.Length == 0,
            "the dump line reads instrumentation state other than through Counts.Read(): "
            + string.Join("; ", otherTouches) + " (#3169 — a second load can contradict the first)");
    }

    /// <summary>The three subsystems hold no standalone integer counter: the pair is the only
    /// tally, so there is nothing a future dump-line edit could print next to it from a
    /// separate load.</summary>
    [Fact]
    public void NoDumpLineSubsystem_KeepsAStandaloneIntegerCounter()
    {
        using var asm = Assembly();
        var strays = DumpLineSubsystems
            .Select(n => asm.MainModule.GetType(n)!)
            .SelectMany(t => t.Fields.Where(f => f.IsStatic
                && (f.FieldType.FullName == typeof(long).FullName || f.FieldType.FullName == typeof(int).FullName)
                && f.Name.Contains("Count", StringComparison.Ordinal)))
            .Select(f => f.FullName)
            .ToArray();
        Assert.True(strays.Length == 0,
            "standalone counter field(s) next to the CountPair: " + string.Join(", ", strays));
    }

    /// <summary>Site 1's read half: SnapshotCounters loads exactly one field of the listener
    /// and it is the CountPair. The code this replaced loaded two int fields.</summary>
    [Fact]
    public void SnapshotCounters_LoadsTheListenerStateExactlyOnce()
    {
        using var asm = Assembly();
        var listener = asm.MainModule.GetType(typeof(EventPipeJitListener).FullName);
        Assert.True(listener != null);
        var snapshot = listener!.Methods.Single(m => m.Name == nameof(EventPipeJitListener.SnapshotCounters));
        var loads = snapshot.Body.Instructions
            .Where(i => (i.OpCode == OpCodes.Ldfld || i.OpCode == OpCodes.Ldflda) && i.Operand is FieldReference)
            .Select(i => (FieldReference)i.Operand)
            .ToArray();
        Assert.Single(loads);
        Assert.Equal(typeof(CountPair).FullName, loads[0].FieldType.FullName);
        Assert.Equal(1, snapshot.Body.Instructions.Count(IsCountPairRead));
    }
}
