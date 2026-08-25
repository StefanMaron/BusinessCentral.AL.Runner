// MetadataProviderSeedDiagnosisTests — proves BcRuntime.IsMetadataProviderSeeded() reports
// the REAL, live state of NavSystemTenant.metadataProvider instead of assuming it.
//
// Why this exists (#2008)
// ------------------------
// AlRunner.Patches.RecordPatches.FieldVirtualTable.cs's BuildManagedFieldDataProvider wraps
// any failure of BC's own FieldDataProvider(NavSession) ctor in a RunnerOutOfScopeException
// whose message used to assert, UNCONDITIONALLY, "the skeleton NavGlobal.MetadataProvider/
// NCLMetadata is not seeded" — even though EnsureMetadataProviderSeeded() (one frame up, in
// PopulateFieldVirtualTable) had already run by the time that catch block could ever fire.
// That was a guess dressed up as a diagnosis: issue #2008's own triage comment confirmed
// nothing in the code path actually verified it. A wrong diagnosis is worse than none — it
// sends the next investigator down the exact dead end #2008's reporter nearly went down.
//
// The fix replaces the assumption with a live field read: BcRuntime.IsMetadataProviderSeeded()
// (see MetadataPatches.cs) inspects NavSystemTenant.metadataProvider directly and the catch
// block now reports whichever is actually true. This test proves that helper is accurate in
// both directions — unseeded (null field) reports false, seeded (non-null field) reports
// true — by manipulating the live field directly (deterministic, independent of whichever
// other test in this process may or may not have called EnsureMetadataProviderSeeded()
// first) and restoring the original value afterwards.
using System.Reflection;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

// Loads Ncl/skeleton state in-process, so it must share the serial bc-engine collection —
// see BcEngineCollection.cs.
[Collection(BcEngineCollection.Name)]
public class MetadataProviderSeedDiagnosisTests
{
    private readonly BcEngineFixture _engine;

    public MetadataProviderSeedDiagnosisTests(BcEngineFixture engine) => _engine = engine;

    private static (Type systemTenantType, object skeletonSystemTenant, FieldInfo metadataProviderField) ResolveSeam()
    {
        var skeletonSystemTenant = typeof(BcRuntime)
            .GetProperty("SkeletonSystemTenant", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null);
        Assert.NotNull(skeletonSystemTenant);

        var systemTenantType = skeletonSystemTenant!.GetType();
        var metadataProviderField = systemTenantType.GetField(
            "metadataProvider", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(metadataProviderField);

        return (systemTenantType, skeletonSystemTenant, metadataProviderField!);
    }

    [SkippableFact]
    public void IsMetadataProviderSeeded_FieldIsNull_ReportsFalse()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (_, skeletonSystemTenant, metadataProviderField) = ResolveSeam();
        var original = metadataProviderField.GetValue(skeletonSystemTenant);
        try
        {
            metadataProviderField.SetValue(skeletonSystemTenant, null);

            Assert.False(BcRuntime.IsMetadataProviderSeeded(),
                "IsMetadataProviderSeeded() must report false when NavSystemTenant.metadataProvider is genuinely null " +
                "— this is the one case where the OLD unconditional 'not seeded' wording was actually true.");
        }
        finally
        {
            metadataProviderField.SetValue(skeletonSystemTenant, original);
        }
    }

    [SkippableFact]
    public void IsMetadataProviderSeeded_FieldIsNonNull_ReportsTrue()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (_, skeletonSystemTenant, metadataProviderField) = ResolveSeam();
        var original = metadataProviderField.GetValue(skeletonSystemTenant);
        try
        {
            // A real MetadataProvider instance (built the same way EnsureMetadataProviderSeeded
            // builds one) proves the check reads the field, not merely "did anyone call the
            // seed method" — the bug #2008 exposed is exactly that distinction: the OLD message
            // could not tell "never attempted" from "attempted and succeeded".
            var metaProvType = metadataProviderField.FieldType;
            var ctor = metaProvType.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            var seeded = ctor != null
                ? ctor.Invoke(null)
                : System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(metaProvType);
            metadataProviderField.SetValue(skeletonSystemTenant, seeded);

            Assert.True(BcRuntime.IsMetadataProviderSeeded(),
                "IsMetadataProviderSeeded() must report true once NavSystemTenant.metadataProvider genuinely holds " +
                "a non-null value — proving #2008's fix: the exception message this feeds no longer blames " +
                "'not seeded' when seeding actually succeeded.");
        }
        finally
        {
            metadataProviderField.SetValue(skeletonSystemTenant, original);
        }
    }
}
