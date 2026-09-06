// BcShapeGapConventionTests — the convention issue #2946 asked for a decision on.
//
// THE QUESTION
//   Four readers of ONE private BC structure, TempTableDataProvider.primaryTree, raised three
//   different exception types between them:
//
//     RecordPatches.StoredTableCensus.cs (x2), RecordPatches.TransactionSnapshot.cs,
//     RecordPatches.InstallBaseline.cs   -> MissingFieldException, via RequiredField
//     RowVersionPatches.SystemIdIntegrity.cs -> InvalidOperationException naming the member
//     RecordPatches.ObjectMetadataSystemTable.cs -> RunnerOutOfScopeException
//
//   What a caller could catch therefore depended on which reader it happened to reach. Both
//   RunnerOutOfScopeException flavours are claims about SCOPE, and none of these is: the
//   surface is in scope AND implemented, and the runner simply could not read BC's internals.
//
// THE ANSWER, AND WHAT THESE TESTS PIN
//   A third type, BcShapeGapException — see AlRunner/Infrastructure/BcShapeGapException.cs
//   for the full derivation. Three cases have to stay distinguishable, and a live correctness
//   bug turned on exactly that (#2894: under the wrong anchor an AL [TryFunction] trapped a
//   runner shape gap into `false`, the silent default loud-failures.md forbids):
//
//                              [TryFunction]        asserterror       expect-oos absorbs?
//     permanent OOS            traps -> false       catches           yes
//     not-yet-implemented      tears through        catches (#2871)   yes
//     BC shape gap             tears through        TEARS THROUGH     NO
//
//   The control arm matters as much as the claim. Every tear-through assertion here is paired
//   with a PERMANENT refusal that is still trapped, so a test cannot pass by discriminating on
//   exception type alone — "anything that is not a RunnerOutOfScopeException tears through"
//   would satisfy the tear-through half and fail the control.
//
// WHY THESE LIVE IN AlRunner.Tests AND NOT THE UPSTREAM CORPUS
//   Nothing here is a claim about Business Central. No AL statement can move a private BC
//   field or rename a static, so a service tier has nothing to adjudicate — on every BC
//   version the runner supports, TempTableDataProvider.primaryTree exists and none of these
//   refusals is reachable from AL. The subject is the runner's own refusal contract, which
//   .claude/rules/bc-behavior-tests-go-upstream.md classifies as runner-specific. Same
//   reasoning as ObjectMetadataProviderRowProbeTests and VirtualTableRefusalClaimTests.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AlRunner;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class BcShapeGapConventionTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private const string Surface = "Object Metadata (system table 2000000071)";

    private static BcShapeGapException Gap() =>
        new(Surface, "TempTableDataProvider.primaryTree", "field not found — BC's private provider layout moved");

    // ── The two control arms. Neither is a shape gap, and both must keep their behaviour ──

    private static RunnerOutOfScopeException PermanentOos() =>
        new("NavEmail.Send", "email-smtp — no SMTP transport in the runner", "email");

    private static RunnerOutOfScopeException NotYetImplemented() =>
        new("INCLObjectXmlMetadataLoader.GetMetaObjectXmlMetadata",
            "not-yet-implemented — report metadata loader", "todo");

    // ══ 1. The type's own contract ═══════════════════════════════════════════════════════

    [Fact]
    public void Message_NamesTheSurfaceTheMemberAndTheDoc()
    {
        var ex = Gap();

        Assert.Equal(Surface, ex.Surface);
        Assert.Equal("TempTableDataProvider.primaryTree", ex.Member);
        Assert.StartsWith("bc-shape-gap: ", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Object Metadata (system table 2000000071)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("TempTableDataProvider.primaryTree", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith(" — see docs/limitations.md#bc-shape-gaps", ex.Message, StringComparison.Ordinal);
    }

    // The absorption defence, at its root. OutOfScopeMessage.TryParse matches its prefix
    // ANYWHERE in a text blob (it is handed whole message+stack dumps), so a shape-gap message
    // that merely CONTAINED "out-of-scope: " would be recovered as an out-of-scope signal by
    // the reporter and by the expectations manifest.
    [Fact]
    public void Message_IsNotMistakenForAnOutOfScopeSignal()
    {
        var ex = Gap();

        Assert.DoesNotContain("out-of-scope: ", ex.Message, StringComparison.Ordinal);
        Assert.False(OutOfScopeMessage.TryParse(ex.Message, out _));
        Assert.False(OutOfScopeMessage.TryParse(ex.ToString(), out _));
        Assert.Null(OutOfScopeMessage.FromException(ex));
        // Nor via an inner chain: a shape gap wrapped in something else is still not OOS.
        Assert.Null(OutOfScopeMessage.FromException(new InvalidOperationException("wrapped", Gap())));
    }

    // The positive control for the assertion above: a real refusal IS still recognised, so
    // "FromException returns null" cannot pass because the parser stopped working.
    [Fact]
    public void OutOfScopeSignal_IsStillRecognisedForARealRefusal()
    {
        var signal = OutOfScopeMessage.FromException(PermanentOos());

        Assert.NotNull(signal);
        Assert.Equal("NavEmail.Send", signal!.Value.Api);
        Assert.StartsWith("email-smtp", signal.Value.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Find_WalksTheInnerChain_BecauseReflectionAndBcWrapTheRefusal()
    {
        var gap = Gap();

        Assert.Same(gap, BcShapeGapException.Find(gap));
        Assert.Same(gap, BcShapeGapException.Find(new TargetInvocationException(gap)));
        Assert.Same(gap, BcShapeGapException.Find(
            new InvalidOperationException("outer", new TargetInvocationException(gap))));
        Assert.Same(gap, BcShapeGapException.Find(new AggregateException(gap)));

        // Negative: an unrelated chain is not a shape gap, and neither is a refusal.
        Assert.Null(BcShapeGapException.Find(new InvalidOperationException("boom")));
        Assert.Null(BcShapeGapException.Find(PermanentOos()));
        Assert.Null(BcShapeGapException.Find(null));
    }

    // ══ 2. BcShape.RequiredField — the one resolver the readers share ═════════════════════

    [Fact]
    public void RequiredField_ResolvesAnInheritedPrivateField_RatherThanCallingItAbsent()
    {
        // BC's own CrmTableConnection.CrmTestDataProvider derives from TempTableDataProvider
        // (#2725). GetField(NonPublic) does not return a base class's private field, so a
        // plain-GetField resolver would refuse a perfectly readable store.
        var field = InvokeRequiredField(typeof(DerivedProvider), "primaryTree");

        Assert.NotNull(field);
        Assert.Equal(typeof(BaseDeclaresPrimaryTree), field!.DeclaringType);
    }

    [Fact]
    public void RequiredField_RaisesAShapeGapNamingTheMember_WhenTheFieldIsGone()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => InvokeRequiredField(typeof(ProviderWithoutPrimaryTree), "primaryTree"));

        Assert.Contains(nameof(ProviderWithoutPrimaryTree), ex.Member, StringComparison.Ordinal);
        Assert.Contains("primaryTree", ex.Member, StringComparison.Ordinal);
        Assert.Contains("not found", ex.Detail, StringComparison.Ordinal);
    }

    private static FieldInfo? InvokeRequiredField(Type type, string member)
    {
        var t = typeof(RunnerOutOfScopeException).Assembly.GetType("AlRunner.Infrastructure.BcShape")
            ?? throw new InvalidOperationException("test setup: AlRunner.Infrastructure.BcShape not found");
        var m = t.GetMethod("RequiredField", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("test setup: BcShape.RequiredField not found");
        try
        {
            return (FieldInfo?)m.Invoke(null, new object?[] { type, member, Surface, "probe detail" });
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;   // the reflection wrapper is not part of the contract
        }
    }

    // ══ 3. AL cannot swallow it — [TryFunction] ═══════════════════════════════════════════

    [Fact]
    public void TryInvoke_TearsThrough_ForAShapeGap()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => BcRuntime.NavApplicationObjectBase_TryInvoke(null, () => throw Gap()));

        Assert.Equal(Surface, ex.Surface);
    }

    [Fact]
    public async Task TryInvokeAsync_TearsThrough_ForAShapeGap()
    {
        var ex = await Assert.ThrowsAsync<BcShapeGapException>(
            async () => await BcRuntime.NavApplicationObjectBase_TryInvokeAsync(null, () => throw Gap()));

        Assert.Equal(Surface, ex.Surface);
    }

    // Wrapped, because that is how it actually arrives: a refusal raised behind
    // MethodBase.Invoke comes back inside a TargetInvocationException, and BC's own
    // RemapToALExceptionAndThrow can rewrap it. TryInvoke's FIRST clause swallows a trappable
    // NavBaseException, so the shape-gap question has to be asked before that one.
    [Fact]
    public void TryInvoke_TearsThrough_EvenWhenTheGapArrivesWrapped()
    {
        var ex = Record.Exception(() => BcRuntime.NavApplicationObjectBase_TryInvoke(
            null, () => throw new TargetInvocationException(Gap())));

        Assert.NotNull(ex);
        Assert.NotNull(BcShapeGapException.Find(ex));
    }

    // ── CONTROL ARM: a genuinely permanent refusal is STILL trapped ──
    // Without this, "tears through" could be satisfied by a TryInvoke that trapped nothing.
    [Fact]
    public void TryInvoke_StillTraps_APermanentRefusal()
    {
        Assert.False(BcRuntime.NavApplicationObjectBase_TryInvoke(null, () => throw PermanentOos()));
    }

    [Fact]
    public async Task TryInvokeAsync_StillTraps_APermanentRefusal()
    {
        Assert.False(await BcRuntime.NavApplicationObjectBase_TryInvokeAsync(
            null, () => throw PermanentOos()));
    }

    // ══ 4. AL cannot swallow it — asserterror ═════════════════════════════════════════════
    //
    // Derived, not copied from the OOS behaviour. `asserterror Foo()` where Foo hits a shape
    // gap: on real BC, Foo runs and returns, so the asserterror FAILS ("expected an error").
    // A runner that catches the gap makes that asserterror PASS — the opposite of BC's answer,
    // and green. Swallowing does not merely hide a gap here, it inverts a result.

    [Fact]
    public void AssertError_TearsThrough_ForAShapeGap()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => BcRuntime.NavMethodScope_AssertError(null!, () => throw Gap()));

        Assert.Equal(Surface, ex.Surface);
    }

    [Fact]
    public void AssertError_TearsThrough_EvenWhenTheGapArrivesWrapped()
    {
        var ex = Record.Exception(() => BcRuntime.NavMethodScope_AssertError(
            null!, () => throw new TargetInvocationException(Gap())));

        Assert.NotNull(ex);
        Assert.NotNull(BcShapeGapException.Find(ex));
    }

    // ── CONTROL ARM: asserterror still catches BOTH refusal flavours ──
    // Returning normally IS the pass signal — NavMethodScope_AssertError throws
    // NavNCLAssertErrorException when the body did not error. #2871 owns the question of
    // whether that should change; this change does not touch it.
    [Fact]
    public void AssertError_StillCatches_BothRefusalFlavours()
    {
        BcRuntime.NavMethodScope_AssertError(null!, () => throw PermanentOos());
        BcRuntime.NavMethodScope_AssertError(null!, () => throw NotYetImplemented());
    }

    // ── CONTROL ARM: asserterror still FAILS when the body does not throw ──
    // Otherwise "tears through" could be satisfied by an asserterror that rethrows everything.
    [Fact]
    public void AssertError_StillFails_WhenTheBodyDoesNotThrow()
    {
        var ex = Record.Exception(() => BcRuntime.NavMethodScope_AssertError(null!, () => { }));

        Assert.NotNull(ex);
        Assert.Equal("Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLAssertErrorException",
            ex!.GetType().FullName);
    }

    // ══ 5. The manifest may not absorb it ═════════════════════════════════════════════════

    // AlRunner.TestOutcome (the runner's own) shadows AlRunner.Infrastructure.TestOutcome
    // under `using AlRunner;`, so the manifest one is spelled out.
    private static AlRunner.Infrastructure.TestOutcome Failed(Exception ex) =>
        new("Codeunit", "Method", false, ex);

    private static ExpectationEntry Entry(ExpectationMode mode, string? reason = null, string? issue = null) =>
        new(CodeunitId: 60000,
            CodeunitName: "Codeunit",
            Method: "Method",
            Mode: mode,
            Reason: reason,
            Issue: issue,
            Doc: mode == ExpectationMode.ExpectDivergence ? "docs/limitations.md#x" : null,
            Note: null,
            SourceFile: "tests/expectations/known-gaps-probe.json");

    [Fact]
    public void ExpectOos_DoesNotAbsorbAShapeGap_AndSaysWhy()
    {
        var c = ExpectationClassifier.Classify(
            Failed(Gap()), Entry(ExpectationMode.ExpectOos, reason: "email-smtp"));

        Assert.Equal(ExpectationResult.FailManifestDrift, c.Result);
        Assert.NotNull(c.Diagnostic);
        // The message has to say what this actually is. The generic no-signal branch tells the
        // author to "make the throw site raise RunnerOutOfScopeException", which is precisely
        // the wrong advice for a BC-layout gap.
        Assert.Contains(nameof(BcShapeGapException), c.Diagnostic!, StringComparison.Ordinal);
        Assert.Contains("TempTableDataProvider.primaryTree", c.Diagnostic!, StringComparison.Ordinal);
        Assert.DoesNotContain("make the throw site raise RunnerOutOfScopeException",
            c.Diagnostic!, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectDivergence_DoesNotAbsorbAShapeGap()
    {
        var c = ExpectationClassifier.Classify(Failed(Gap()), Entry(ExpectationMode.ExpectDivergence));

        Assert.Equal(ExpectationResult.FailManifestDrift, c.Result);
        Assert.Contains(nameof(BcShapeGapException), c.Diagnostic!, StringComparison.Ordinal);
    }

    // With no entry at all it is a plain failure — NOT the "Unexpected out-of-scope … add an
    // expect-oos entry" drift message, which would send the author to declare it expected.
    [Fact]
    public void NoEntry_IsAPlainFailure_NotAnUndeclaredOosSurface()
    {
        var c = ExpectationClassifier.Classify(Failed(Gap()), null);

        Assert.Equal(ExpectationResult.Fail, c.Result);
        Assert.Null(c.Diagnostic);
    }

    // expect-fail-known-gap DOES absorb it, deliberately: that mode means "must fail, and this
    // open issue tracks the work", which is exactly what a shape gap is once written down.
    [Fact]
    public void ExpectFailKnownGap_StillAbsorbsAShapeGap()
    {
        var c = ExpectationClassifier.Classify(
            Failed(Gap()), Entry(ExpectationMode.ExpectFailKnownGap, issue: "#2946"));

        Assert.Equal(ExpectationResult.PassKnownGap, c.Result);
    }

    // ── CONTROL ARM: expect-oos still absorbs a real refusal ──
    [Fact]
    public void ExpectOos_StillAbsorbs_ARealPermanentRefusal()
    {
        var c = ExpectationClassifier.Classify(
            Failed(PermanentOos()), Entry(ExpectationMode.ExpectOos, reason: "email-smtp"));

        Assert.Equal(ExpectationResult.PassOos, c.Result);
    }

    // ══ 6. The reporter buckets it as itself ══════════════════════════════════════════════

    [Fact]
    public void Reporter_BucketsAShapeGapUnderItsOwnHeading_NotAnIncidentalBcStackFrame()
    {
        var ex = Gap();
        var bucket = ClassifyTest(ex.Message, ex.ToString());

        Assert.Equal($"bc-shape-gap/{Surface}", bucket);
    }

    // ── CONTROL ARM: real refusals and plain failures keep their buckets ──
    [Fact]
    public void Reporter_StillBucketsRefusalsAndPlainFailuresAsBefore()
    {
        var oos = PermanentOos();
        Assert.Equal("out-of-scope/NavEmail.Send", ClassifyTest(oos.Message, oos.ToString()));

        var boom = new InvalidOperationException("runner bug");
        Assert.Equal("runtime/other", ClassifyTest(boom.Message, boom.ToString()));
    }

    private static string ClassifyTest(string message, string full)
    {
        var m = typeof(Reporter).GetMethod("ClassifyTest", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("test setup: Reporter.ClassifyTest(string,string) not found");
        return (string)m.Invoke(null, new object[] { message, full })!;
    }

    // ══ 7. The convention actually reached the readers ════════════════════════════════════
    //
    // The production readers, driven through their real entry points. Without these the type
    // could exist and be perfectly specified while every reader kept raising what it did
    // before — which is the state #2946 was filed about.

    [Fact]
    public void ObjectMetadataRowProbe_RaisesAShapeGap_WhenPrimaryTreeIsGone()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => ProviderHasAnyRow(new ProviderWithoutPrimaryTree()));

        Assert.Equal(Surface, ex.Surface);
        Assert.Contains("primaryTree", ex.Member, StringComparison.Ordinal);
    }

    [Fact]
    public void ObjectMetadataRowProbe_RaisesAShapeGap_WhenPrimaryTreeCannotBeEnumerated()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => ProviderHasAnyRow(new ProviderWithNonEnumerablePrimaryTree()));

        Assert.Contains("primaryTree", ex.Member, StringComparison.Ordinal);
        Assert.Contains("cannot be enumerated", ex.Detail, StringComparison.Ordinal);
    }

    // ── CONTROL ARM: the reader still answers BC's genuine questions ──
    // A fix that threw on every input would satisfy the two assertions above.
    [Fact]
    public void ObjectMetadataRowProbe_StillAnswers_ForAReadableStore()
    {
        Assert.False(ProviderHasAnyRow(new FakeProvider(null)));                       // BC's "no row ever inserted"
        Assert.False(ProviderHasAnyRow(new FakeProvider(new List<object>())));
        Assert.True(ProviderHasAnyRow(new FakeProvider(new List<object> { new() })));
        Assert.True(ProviderHasAnyRow(new DerivedProvider(new List<object> { new() })));  // inherited field, #2725
    }

    [Fact]
    public void RecordPatchesRequiredField_RaisesAShapeGap_NotAMissingFieldException()
    {
        var m = typeof(RecordPatches).GetMethod(
            "RequiredField", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("test setup: RecordPatches.RequiredField not found");

        var ex = Record.Exception(() =>
        {
            try { m.Invoke(null, new object?[] { typeof(ProviderWithoutPrimaryTree), "primaryTree", "probe surface" }); }
            catch (TargetInvocationException tie) when (tie.InnerException != null) { throw tie.InnerException; }
        });

        var gap = Assert.IsType<BcShapeGapException>(ex);
        Assert.Contains("primaryTree", gap.Member, StringComparison.Ordinal);

        // The positive arm: the SAME helper resolves an inherited private field, so the three
        // readers that go through it agree with the two that use PrivateMemberLookup directly.
        var found = (FieldInfo?)m.Invoke(null, new object?[] { typeof(DerivedProvider), "primaryTree", "probe surface" });
        Assert.Equal(typeof(BaseDeclaresPrimaryTree), found!.DeclaringType);
    }

    // ── The source-shape guard: nobody may quietly go back to a third convention ──
    // The five files that read TempTableDataProvider's private structure must not spell any
    // of the three retired conventions at a primaryTree/table read. This is what stops the
    // disagreement from reappearing one file at a time, which is how it arrived.
    [Fact]
    public void NoReaderOfThePrivateProviderStructure_StillRaisesARetiredType()
    {
        string[] files =
        {
            "RecordPatches.StoredTableCensus.cs",
            "RecordPatches.TransactionSnapshot.cs",
            "RecordPatches.InstallBaseline.cs",
            "RecordPatches.ObjectMetadataSystemTable.cs",
            "RowVersionPatches.SystemIdIntegrity.cs",
        };

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var path = Path.Combine(RepoRoot, "AlRunner", "Patches", file);
            Assert.True(File.Exists(path), $"{file} not found at {path} — it was renamed or moved.");

            foreach (var (line, n) in File.ReadLines(path).Select((l, i) => (l, i + 1)))
            {
                // Only THROW sites count; a comment or a catch clause naming the retired type
                // is documentation, and RecordPatches.StoredTableCensus.cs legitimately catches
                // the new type to keep its documented "unknown, never empty" contract.
                if (!line.Contains("throw new MissingFieldException", StringComparison.Ordinal)) continue;
                offenders.Add($"{file}:{n}");
            }
        }

        Assert.True(offenders.Count == 0,
            "these sites still raise a retired convention for a BC-internals read: "
            + string.Join(", ", offenders));
    }

    // The doc target a shape gap points at has to exist, or the message sends readers nowhere.
    [Fact]
    public void TheDocSectionTheMessagePointsAt_Exists()
    {
        var limitations = File.ReadAllText(Path.Combine(RepoRoot, "docs", "limitations.md"));

        Assert.Contains("## BC shape gaps", limitations, StringComparison.OrdinalIgnoreCase);
        // And scope.md must NOT claim them, since a shape gap is not a scope boundary.
        var scope = File.ReadAllText(Path.Combine(RepoRoot, "docs", "scope.md"));
        Assert.DoesNotContain("bc-shape-gap", scope, StringComparison.OrdinalIgnoreCase);
    }

    // ══ Reflected-shape fakes ═════════════════════════════════════════════════════════════

    private static bool ProviderHasAnyRow(object provider)
    {
        var m = typeof(RecordPatches).GetMethod(
            "ProviderHasAnyRow", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("test setup: RecordPatches.ProviderHasAnyRow not found");
        try
        {
            return (bool)m.Invoke(null, new[] { provider })!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }
    }

    private sealed class ProviderWithoutPrimaryTree
    {
    }

#pragma warning disable CS0414   // assigned and read only by the reflection under test
    private sealed class ProviderWithNonEnumerablePrimaryTree
    {
        private readonly object primaryTree = new();
    }

    private sealed class FakeProvider
    {
        private readonly IEnumerable? primaryTree;
        public FakeProvider(IEnumerable? rows) => primaryTree = rows;
    }

    private class BaseDeclaresPrimaryTree
    {
        private readonly IEnumerable? primaryTree;
        protected BaseDeclaresPrimaryTree(IEnumerable? rows) => primaryTree = rows;
    }
#pragma warning restore CS0414

    private sealed class DerivedProvider : BaseDeclaresPrimaryTree
    {
        public DerivedProvider(IEnumerable? rows) : base(rows) { }
    }
}
