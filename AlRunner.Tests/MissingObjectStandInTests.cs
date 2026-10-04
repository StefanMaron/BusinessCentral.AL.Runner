// MissingObjectStandInTests — #5339.
//
// The runner's two stand-ins for "this object does not exist" used to raise a raw CLR exception
// (RecordRef.Open: InvalidOperationException; Codeunit.Run: MissingDependencyCodeunitException).
// BC raises an AL error there (NavMetadataNotFoundException, which NavMethodScope remaps to
// "You tried to invoke the <type> object with the ID <n> ... An object with that ID does not
// exist"), and #4976 made asserterror let a raw CLR exception escape as BC's does. The BC claim
// is measured upstream (corpus codeunits 69900 and 69901: bare asserterror passes, the text,
// and the guarded Codeunit.Run raising instead of returning false).
//
// What is pinned HERE is the runner's own decision, which no service tier can express because
// the state does not exist there: when the run recorded a provisioning gap the runner cannot tell
// a missing object from one declared by a package it could not load, so it keeps the loud refusal
// (a CLR exception asserterror cannot swallow) instead of the clean AL error. Each gap source
// below has its own arm, so one of them cannot stand in for the others.
using System;
using System.IO;
using AlRunner;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Exceptions;
using Xunit;

namespace AlRunner.Tests;

// Loads Ncl types in-process, and ProvisionGapLog is process-global: see BcEngineCollection.cs.
[Collection(BcEngineCollection.Name)]
public sealed class MissingObjectStandInTests
{
    // Outside both no-op ranges, outside every app range this repo declares, and no Codeunit69007 /
    // Record69007 type exists, so every arm below reaches the "nothing declares it" branch.
    private const int MissingId = 69007;

    private readonly BcEngineFixture _engine;

    public MissingObjectStandInTests(BcEngineFixture engine) => _engine = engine;

    private void RequireEngine() => TestArtifacts.SkipIf(!_engine.Ready,
        _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    private static ITreeObject Root() => BcRuntime.RootTreeStub
        ?? throw new InvalidOperationException("BcRuntime.RootTreeStub is null after the engine bootstrap.");

    private static Exception Unwrap(Exception ex)
        => ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;

    private static Exception Resolve(int codeunitId)
        => Unwrap(Assert.ThrowsAny<Exception>(() => new NavCodeunitHandle(Root(), codeunitId).Target));

    private static Exception AssertErrorOutcome(Exception raised)
        => Record.Exception(() => BcRuntime.NavMethodScope_AssertError(null!, () => throw raised))!;

    private static void WithGap(Action setUp, Action body)
    {
        ProvisionGapLog.Reset();
        try { setUp(); body(); }
        finally { ProvisionGapLog.Reset(); }
    }

    // ── a gap-free run: the id is ABSENT, and the error is BC's ──

    [SkippableFact]
    public void Codeunit_AbsentInAGapFreeRun_RaisesBcsMetadataNotFound_WhichAsserterrorCatches()
    {
        RequireEngine();
        ProvisionGapLog.Reset();

        var ex = Resolve(MissingId);

        var notFound = Assert.IsType<NavMetadataNotFoundException>(ex);
        Assert.Contains(MissingId.ToString(), notFound.Message);
        Assert.Null(AssertErrorOutcome(ex));
    }

    [SkippableFact]
    public void Table_AbsentInAGapFreeRun_RaisesBcsMetadataNotFound_WhichAsserterrorCatches()
    {
        RequireEngine();
        ProvisionGapLog.Reset();

        var ex = BcRuntime.MissingTable(MissingId);

        var notFound = Assert.IsType<NavMetadataNotFoundException>(ex);
        Assert.Contains(MissingId.ToString(), notFound.Message);
        Assert.Null(AssertErrorOutcome(ex));
    }

    // ── a run with a gap: the runner cannot tell, so the refusal stays loud and escapes asserterror ──

    public static TheoryData<string> GapSources => new() { "report", "unservable-app", "dependency-gap" };

    private static void RecordGap(string source)
    {
        switch (source)
        {
            case "report": ProvisionGapLog.Report("a dependency this run could not load"); break;
            case "unservable-app": ProvisionGapLog.RegisterUnservableApp("Pub/App", () => new[] { 1 }); break;
            case "dependency-gap": ProvisionGapLog.NoteDependencyGap(); break;
            default: throw new ArgumentOutOfRangeException(nameof(source), source, null);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(GapSources))]
    public void Codeunit_WithAProvisioningGap_KeepsTheTypedRefusal_AsserterrorCannotSwallow(string source)
    {
        RequireEngine();
        WithGap(() => RecordGap(source), () =>
        {
            var ex = Resolve(MissingId);

            Assert.Equal(MissingId, Assert.IsType<MissingDependencyCodeunitException>(ex).CodeunitId);
            Assert.Same(ex, AssertErrorOutcome(ex));
        });
    }

    [SkippableTheory]
    [MemberData(nameof(GapSources))]
    public void Table_WithAProvisioningGap_KeepsALoudClrRefusal_AsserterrorCannotSwallow(string source)
    {
        RequireEngine();
        WithGap(() => RecordGap(source), () =>
        {
            var ex = BcRuntime.MissingTable(MissingId);

            Assert.IsType<InvalidOperationException>(ex);
            Assert.Contains($"table {MissingId}", ex.Message);
            Assert.Contains("provisioning gap", ex.Message);
            Assert.Same(ex, AssertErrorOutcome(ex));
        });
    }

    [SkippableFact]
    public void TheGapSurvivesASnapshotRestore_AndAResetClearsIt()
    {
        RequireEngine();
        ProvisionGapLog.Reset();
        try
        {
            ProvisionGapLog.NoteDependencyGap();
            var snapshot = ProvisionGapLog.Capture();

            ProvisionGapLog.Reset();
            Assert.True(ProvisionGapLog.CanEstablishAbsence, "a Reset starts the next bundle gap-free");

            ProvisionGapLog.Restore(snapshot);
            Assert.False(ProvisionGapLog.CanEstablishAbsence, "a restored bundle keeps the gap it had");
        }
        finally { ProvisionGapLog.Reset(); }
    }

    // ── the resolver is where a gap is found: it must tell the log, or the refusal above never fires ──

    private static DependencyResolver ResolveInto(string dirName, byte[] app, string appId, string name, string publisher)
    {
        var dir = Path.Combine(TestScratch.Dir("al-runner-missing-object-tests"), dirName);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, $"{publisher}_{name}.app"), app);
        var resolver = new DependencyResolver(new[] { dir });
        resolver.Resolve(new[] { new DependencyRef(Guid.Parse(appId), name, publisher, new Version(1, 0, 0, 0)) });
        return resolver;
    }

    [SkippableFact]
    public void AnUnsuppliedPlatformFloor_IsAGap_SoAMissingObjectKeepsTheRefusal()
    {
        RequireEngine();
        WithGap(ProvisionGapLog.Reset, () =>
        {
            // The dependent ships AL source and no payload, and declares a floor (System >= 28.0) that no
            // searched directory supplies: DependencyResolverTests pins the report; this pins what it does.
            const string id = "00000000-0000-0000-0000-0000005339a1";
            var resolver = ResolveInto("floor-gap",
                DependencyResolverTests.MakeMinimalApp(id, "Floored", "Contoso", "1.0.0.0",
                    r2r: false, alSource: true, platform: "28.0.0.0"), id, "Floored", "Contoso");

            Assert.NotEmpty(resolver.ProvisioningGaps);
            Assert.False(ProvisionGapLog.CanEstablishAbsence);
            Assert.IsType<InvalidOperationException>(BcRuntime.MissingTable(MissingId));
        });
    }

    [SkippableFact]
    public void ADependencyNoLoaderTierCanServe_IsAGap_SoAMissingObjectKeepsTheRefusal()
    {
        RequireEngine();
        WithGap(ProvisionGapLog.Reset, () =>
        {
            const string id = "00000000-0000-0000-0000-0000005339a2";
            var resolver = ResolveInto("unservable",
                DependencyResolverTests.MakeMinimalApp(id, "SymbolsOnly", "Contoso", "1.0.0.0",
                    r2r: false, alSource: false, platform: null), id, "SymbolsOnly", "Contoso");

            Assert.NotEmpty(resolver.UnservableDependencies);
            Assert.False(ProvisionGapLog.CanEstablishAbsence);
        });
    }

    [SkippableFact]
    public void ACompleteDependency_IsNotAGap_SoAMissingObjectStaysAbsent()
    {
        RequireEngine();
        WithGap(ProvisionGapLog.Reset, () =>
        {
            const string id = "00000000-0000-0000-0000-0000005339a3";
            var resolver = ResolveInto("complete",
                DependencyResolverTests.MakeMinimalApp(id, "Precompiled", "Contoso", "1.0.0.0",
                    r2r: true, alSource: false, platform: "28.0.0.0"), id, "Precompiled", "Contoso");

            Assert.Empty(resolver.ProvisioningGaps);
            Assert.Empty(resolver.UnservableDependencies);
            Assert.True(ProvisionGapLog.CanEstablishAbsence);
        });
    }

    // ── Codeunit.Run: BC resolves the target before the run, outside the guarded run's try ──

    [SkippableTheory]
    [InlineData(DataError.ThrowError)]
    [InlineData(DataError.TrapError)]
    public void RunCodeunit_Missing_RaisesInBothSpellings_NeverReturnsFalse(DataError errorLevel)
    {
        RequireEngine();
        ProvisionGapLog.Reset();

        var ex = Unwrap(Assert.ThrowsAny<Exception>(
            () => BcRuntime.NavCodeunit_RunCodeunit(errorLevel, MissingId, null)));

        Assert.IsType<NavMetadataNotFoundException>(ex);
    }

    [SkippableFact]
    public void RunCodeunit_GuardedWithAProvisioningGap_RaisesTheRefusal_NotAFalseReturn()
    {
        RequireEngine();
        WithGap(ProvisionGapLog.NoteDependencyGap, () =>
        {
            // Before #5339 the resolution sat inside the guarded run's catch-all, so this returned
            // false: a provisioning gap read as "the codeunit ran and failed".
            var ex = Unwrap(Assert.ThrowsAny<Exception>(
                () => BcRuntime.NavCodeunit_RunCodeunit(DataError.TrapError, MissingId, null)));

            Assert.Equal(MissingId, Assert.IsType<MissingDependencyCodeunitException>(ex).CodeunitId);
        });
    }
}
