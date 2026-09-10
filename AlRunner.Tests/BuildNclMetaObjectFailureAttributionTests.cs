// BuildNclMetaObjectFailureAttributionTests — issue #3776.
//
// WHAT IS BEING PROVED
//   #3590 fixed BuildNCLMetaTable: its `catch (Exception)` turned every construction failure
//   into the same answer as "this table does not exist". The four object-kind siblings carried
//   the identical shape, so a deliberate refusal naming what could not be read — a moved BC
//   member, a dependency whose symbols would not read to completion — came back as a null and
//   surfaced several layers later as somebody else's NRE or NavMetadataNotFoundException.
//
//   Each builder now classifies through its own `*CatchMayAbsorb` predicate. Sections 1 and 2
//   drive those predicates directly; section 3 pins the structural claim they rest on.
//
// THE DECISION THIS FILE PINS, AND WHY IT IS NOT "RETHROW EVERYTHING"
//   guards-need-a-third-state.md: a genuinely ABSENT thing must stay a pass; only an
//   UNMEASURABLE one becomes the third state. Every caller of these five builders reaches them
//   through a `GetOrAdd` and takes a real not-found branch on null (the callers are enumerated
//   in the PR body), so a blanket rethrow would trade a false green for a false red.
//
//   Each builder already draws that line OUTSIDE its try, which is what section 3 measures from
//   IL rather than from reading the source.
//
// WHY RUNNER-LOCAL AND NOT THE CORPUS
//   No AL statement can make a BC member move or corrupt a .app's symbols, so a service tier has
//   nothing to adjudicate: the subject is which exceptions the runner's own catch may absorb.
//   Same reasoning as BuildNclMetaTableFailureAttributionTests (#3590).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class BuildNclMetaObjectFailureAttributionTests
{
    // The five builders this file covers, each with the predicate its catch filters on and the
    // failure-line helper it writes through. #3776 named the first three; Form/Report/Query/
    // XmlPort share one file and were identical in shape, so the same edit covers all four
    // (batch-sibling-issues-by-file.md).
    public static TheoryData<string, string, string> Builders => new()
    {
        { "BuildNCLMetaForm",         "BuildNclMetaFormCatchMayAbsorb",    "BuildNclMetaFormFailureLine" },
        { "BuildNCLMetaReport",       "BuildNclMetaReportCatchMayAbsorb",  "BuildNclMetaReportFailureLine" },
        { "BuildNCLMetaQuery",        "BuildNclMetaQueryCatchMayAbsorb",   "BuildNclMetaQueryFailureLine" },
        { "BuildNCLMetaXmlPort",      "BuildNclMetaXmlPortCatchMayAbsorb", "BuildNclMetaXmlPortFailureLine" },
        { "BuildRealNCLMetaQueryCore", "BuildRealNclMetaQueryCatchMayAbsorb", "BuildRealNclMetaQueryFailureLine" },
    };

    private static MethodInfo Method(string name)
    {
        var m = typeof(RecordPatches).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(m != null, $"RecordPatches.{name} not found");
        return m!;
    }

    private static bool MayAbsorb(string predicate, Exception ex)
        => (bool)Method(predicate).Invoke(null, new object?[] { ex })!;

    private static string FailureLine(string helper, int objectId, Exception ex)
        => (string)Method(helper).Invoke(null, new object?[] { objectId, ex })!;

    // ══ 1. WHAT MUST TEAR THROUGH ════════════════════════════════════════════════════════
    //
    // Each type here exists to name what could not be read. Absorbing one re-creates the silent
    // default it was raised to replace.

    [Theory]
    [MemberData(nameof(Builders))]
    public void AShapeGap_IsNotAbsorbed_SoTheMemberThatMovedReachesTheDeveloper(
        string builder, string predicate, string _)
    {
        var gap = new BcShapeGapException(
            $"{builder} metadata", "NCLMetaApplicationObject.CreateEmpty", "member not found — BC moved it");

        Assert.False(MayAbsorb(predicate, gap),
            $"{builder}'s catch must not absorb a BcShapeGapException.");
    }

    [Theory]
    [MemberData(nameof(Builders))]
    public void AShapeGapArrivingWrapped_IsNotAbsorbed_BecauseEveryBuilderCallsBcThroughReflection(
        string builder, string predicate, string _)
    {
        // Every construction step in all five builders is a MethodBase.Invoke or a reflective
        // ctor, so a refusal raised underneath arrives inside a TargetInvocationException. An
        // `is` test would miss exactly the cases the filter exists for — this is the subtlest
        // half of #3590 and it carries over unchanged.
        var wrapped = new TargetInvocationException(
            new BcShapeGapException($"{builder} metadata", "NCLMetaApplicationObject.CreateEmpty", "moved"));

        Assert.False(MayAbsorb(predicate, wrapped),
            $"{builder}'s filter must walk the inner chain, not test with `is`.");
    }

    [Theory]
    [MemberData(nameof(Builders))]
    public void ASymbolReadRefusal_IsNotAbsorbed_BecauseACorruptDependencyIsNotAMissingObject(
        string builder, string predicate, string _)
    {
        var read = new BcAppSymbolReadException(
            "/tmp/Some.app", "object symbols", new OutOfMemoryException("truncated"));

        Assert.False(MayAbsorb(predicate, read), $"{builder}: a symbol-read refusal must tear through.");
    }

    [Theory]
    [MemberData(nameof(Builders))]
    public void ASymbolReadRefusalArrivingWrapped_IsNotAbsorbed_ForTheSameReason(
        string builder, string predicate, string _)
    {
        var wrapped = new TargetInvocationException(
            new BcAppSymbolReadException("/tmp/Some.app", "object symbols",
                new OutOfMemoryException("truncated")));

        Assert.False(MayAbsorb(predicate, wrapped), $"{builder}: wrapped symbol-read refusal must tear through.");
    }

    [Theory]
    [MemberData(nameof(Builders))]
    public void ATypedOutOfScopeRefusal_IsNotAbsorbed_MatchingTheNeighbouringCatchsContract(
        string builder, string predicate, string _)
    {
        var oos = new RunnerOutOfScopeException(
            $"{builder} construction", "not-yet-implemented — see docs/scope.md");

        Assert.False(MayAbsorb(predicate, oos), $"{builder}: a typed out-of-scope refusal must tear through.");
    }

    // ══ 2. WHAT MUST STILL BE ABSORBED ═══════════════════════════════════════════════════
    //
    // Without these, the change is a blanket rethrow and every caller that legitimately asks
    // about an object that cannot exist starts failing the run instead of taking its not-found
    // branch. That is the false-red half of guards-need-a-third-state.md's constraint.

    [Theory]
    [MemberData(nameof(Builders))]
    public void AnOrdinaryConstructionFailure_IsStillAbsorbed_SoNoCallerLosesItsNotFoundBranch(
        string builder, string predicate, string _)
    {
        Assert.True(MayAbsorb(predicate, new InvalidOperationException("ctor arity changed")), builder);
        Assert.True(MayAbsorb(predicate, new NullReferenceException()), builder);
        Assert.True(MayAbsorb(predicate, new TargetInvocationException(new ArgumentException("bad arg"))), builder);
    }

    [Theory]
    [MemberData(nameof(Builders))]
    public void AnUntypedOutOfScopeLookalike_IsStillAbsorbed_BecauseItIsNotOneOfOurs(
        string builder, string predicate, string _)
    {
        // A BC exception whose MESSAGE merely happens to carry the out-of-scope convention is
        // not a runner refusal. #3048 drew this line explicitly: "Typed only".
        var lookalike = new InvalidOperationException(
            OutOfScopeMessage.Prefix + "something that only looks like ours");

        Assert.True(MayAbsorb(predicate, lookalike), builder);
    }

    // ══ 3. THE ABSENT CASES NEVER REACH THE CATCH ════════════════════════════════════════
    //
    // The claim that makes section 1 safe, measured per builder from IL rather than assumed from
    // the source. A refactor that moves an existence check inside the try must fail here.
    //
    // WHAT THIS ASSERTS, AND WHAT IT DELIBERATELY DOES NOT
    //
    //   Asserted: the protected region begins at a nonzero offset and does not reach the method's
    //   entry point, and the unprotected prologue is big enough to hold the existence checks. So
    //   the checks run OUTSIDE the handler's reach, and an absent object returns without the
    //   filter ever seeing it.
    //
    //   NOT asserted: which IL offset any particular early return branches to. Two attempts at
    //   that both measured codegen rather than structure, and the second was unsound:
    //
    //   1. #3590's version asserts a `ret` byte appears before the first try offset. Roslyn gives
    //      four of these five builders one shared epilogue and branches every early return to it,
    //      so no `ret` is emitted before the try at all. Measured: the scan holds only for
    //      BuildNCLMetaTable and BuildNCLMetaXmlPort.
    //   2. This test's first version scanned the pre-try bytes for branch opcodes and required one
    //      to target past the handler. Without a real opcode-length table a forward byte scan
    //      cannot tell an opcode from an operand, and mis-framing yields garbage targets — on one
    //      box BuildNCLMetaForm produced targets of 755630105 and 755630118, which satisfied
    //      "past the handler" and made the check PASS on nonsense, while the same assertion failed
    //      honestly on another box. It was wrong in both directions, so it is gone.
    //
    //   A sound version needs the full opcode table (or Cecil), and a behavioural arm driving a
    //   real absent object through needs the BC engine, which would make this skip on boxes that
    //   lack it — a non-vacuity guard that silently does not run is the defect it exists to catch.
    //   The offsets below are source-determined, always run, and are what actually carries the
    //   safety property.

    [Theory]
    [MemberData(nameof(Builders))]
    public void TheAbsentReturns_SitOutsideTheTry_SoNoMissingObjectCanReachTheFilter(
        string builder, string _, string __)
    {
        var body = Method(builder).GetMethodBody()!;
        var clauses = body.ExceptionHandlingClauses;

        // Non-vacuity, part 1: there is a handler at all. Without this the two offset assertions
        // below would be trivially satisfiable by a method that lost its try entirely.
        Assert.True(clauses.Count > 0,
            $"{builder} has no exception handler at all — this test would otherwise measure nothing.");

        var tryOffset = clauses.Select(c => c.TryOffset).Min();
        var clause = clauses.First(c => c.TryOffset == tryOffset);
        var il = body.GetILAsByteArray()!;

        // The property: the try starts after the existence checks, so nothing absent reaches the
        // catch filter. A try at offset 0 means a check has moved inside it.
        Assert.True(tryOffset > 0,
            $"{builder}'s try must start after its absent-object early returns; a try at offset 0 "
            + "means a genuinely missing object now reaches the catch filter and can be rethrown "
            + "— the false-red half of guards-need-a-third-state.md's constraint.");

        // Non-vacuity, part 2: the unprotected prologue is substantial, not a stray byte or two.
        // Every existence check in these builders is at least a dictionary/set lookup plus a
        // branch, so a prologue this small would mean the checks are no longer there. 16 bytes is
        // well under the smallest real value (55) and well over a degenerate one.
        Assert.True(tryOffset >= 16,
            $"{builder}: only {tryOffset} IL bytes precede the try. The existence checks are not "
            + "where this test thinks they are, so the assertion above is not measuring them.");

        // And the handler does not reach back over the prologue.
        Assert.True(clause.HandlerOffset > tryOffset,
            $"{builder}: the handler starts at {clause.HandlerOffset}, at or before the try at "
            + $"{tryOffset} — the protected region covers the existence checks.");

        Assert.True(il.Length > tryOffset,
            $"{builder}: IL is shorter than its own try offset; the body could not be read.");
    }

    // ══ 4. THE FAILURE IS VISIBLE AT DEFAULT VERBOSITY ═══════════════════════════════════
    //
    // The second half of #3590, and the half that differs between the object kinds. The
    // form/report/query/xmlport writes were `[RecordPatches] ...`, which Log.FilteredWriter
    // (AlRunner/Log.cs) drops unless --verbose. BuildRealNCLMetaQuery's was worse: it went to
    // QLog, which writes nothing at all unless AL_RUNNER_QDIAG=1. Both are the same defect —
    // an absorbed failure the developer cannot see — so every builder's line must survive the
    // default filter.

    [Theory]
    [MemberData(nameof(Builders))]
    public void TheAbsorbedFailuresLogLine_NamesTheObjectAndTheCause_AndSurvivesTheDefaultFilter(
        string builder, string _, string helper)
    {
        var line = FailureLine(helper, 50100, new InvalidOperationException("ctor arity changed"));

        Assert.Contains("50100", line, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), line, StringComparison.Ordinal);
        Assert.Contains("ctor arity changed", line, StringComparison.Ordinal);

        // Not dropped at default verbosity: Log.cs's ComponentTag matches a LEADING bracketed
        // tag, so the line must not start with one.
        Assert.False(line.StartsWith("[", StringComparison.Ordinal),
            $"{builder}'s failure line starts with a component tag and is filtered out by default.");
    }

    [Theory]
    [MemberData(nameof(Builders))]
    public void TheFailureLine_UnwrapsAReflectionWrapper_SoTheCauseIsNamedRatherThanTheWrapper(
        string builder, string _, string helper)
    {
        var line = FailureLine(helper, 50100, new TargetInvocationException(new ArgumentException("bad id")));

        Assert.Contains(nameof(ArgumentException), line, StringComparison.Ordinal);
        Assert.Contains("bad id", line, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(TargetInvocationException), line, StringComparison.Ordinal);
        Assert.False(line.StartsWith("[", StringComparison.Ordinal), builder);
    }

    [Theory]
    [MemberData(nameof(Builders))]
    public void TheFailureLine_NamesItsOwnObjectKind_SoFiveNearIdenticalLinesStayDistinguishable(
        string builder, string _, string helper)
    {
        // The five builders differ only in object kind, and the whole point of the line is to
        // say WHICH lookup failed. A line naming the wrong kind is as unattributable as none.
        var kind = builder switch
        {
            "BuildNCLMetaForm" => "page",
            "BuildNCLMetaReport" => "report",
            "BuildNCLMetaXmlPort" => "xmlport",
            _ => "query",
        };

        var line = FailureLine(helper, 50100, new InvalidOperationException("boom"));
        Assert.Contains(kind, line, StringComparison.OrdinalIgnoreCase);
    }

    // ══ 5. THE REFLECTION PIN: name, arity, uniqueness ═══════════════════════════════════

    [Theory]
    [MemberData(nameof(Builders))]
    public void BothHelpersAreUniqueByName_AndTakeWhatTheseArmsDriveThemWith(
        string _, string predicate, string helper)
    {
        var declared = typeof(RecordPatches)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .ToList();

        var filter = declared.Where(x => x.Name == predicate).ToList();
        Assert.Single(filter);
        Assert.Equal(new[] { typeof(Exception) },
            filter[0].GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.Equal(typeof(bool), filter[0].ReturnType);

        var line = declared.Where(x => x.Name == helper).ToList();
        Assert.Single(line);
        Assert.Equal(new[] { typeof(int), typeof(Exception) },
            line[0].GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.Equal(typeof(string), line[0].ReturnType);
    }

    // ══ 6. THE CACHE CANNOT HIDE A REFUSAL ═══════════════════════════════════════════════
    //
    // BuildRealNCLMetaQuery is `_realMetaQueryCache.GetOrAdd(id, _ => Core(...))`, and every
    // other builder is reached through a GetOrAdd at its call site. This pins the property that
    // makes section 1 reach the caller on a warm run too: ConcurrentDictionary.GetOrAdd writes
    // NO entry when the factory throws, so a torn-through refusal is raised again on the next
    // call rather than being answered from a cached null. Measured, not assumed — the same
    // question local-test-scope.md's cache-sensitivity note asks.

    [Fact]
    public void GetOrAdd_CachesNothingWhenTheFactoryThrows_SoARefusalIsNotHiddenByAWarmRun()
    {
        var cache = new System.Collections.Concurrent.ConcurrentDictionary<int, object?>();
        var calls = 0;

        for (var i = 0; i < 2; i++)
        {
            Assert.Throws<BcShapeGapException>(() => cache.GetOrAdd(42, _ =>
            {
                calls++;
                throw new BcShapeGapException("q", "m", "moved");
            }));
        }

        Assert.Equal(2, calls);            // the factory ran BOTH times
        Assert.False(cache.ContainsKey(42)); // and nothing was written
    }
}
