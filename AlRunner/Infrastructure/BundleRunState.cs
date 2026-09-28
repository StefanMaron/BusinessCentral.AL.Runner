using System.Reflection;

namespace AlRunner.Infrastructure;

/// <summary>
/// The single-slot process state a bundle's test run reads, as that bundle's load left it. A run
/// over several bundles loads every bundle before any runs tests (#4850 for --server, #4931 for the
/// CLI), so by the time a bundle runs, a later bundle's load has overwritten each slot; capturing
/// at the end of the load and restoring before the run gives the run exactly what it saw when
/// load and run were one pass.
/// <para>Trap: a new single-slot setter the bundle loop calls during a load and a run reads needs a
/// term here, or the deferred run of every bundle but the last reads the last bundle's value.</para>
/// </summary>
internal sealed class BundleRunState
{
    private readonly IReadOnlyList<Assembly> _installDependencies;
    private readonly BcCompiler.BundleReferenceState _references;
    private readonly (Guid AppId, string Name, string Publisher, string Version) _bundleInfo;
    private readonly string? _resourceDir;
    private readonly ProvisionGapLog.Snapshot? _provisionGaps;

    private BundleRunState(bool includeProvisionGaps)
    {
        _installDependencies = InstallTriggerRunner.DependencyAssemblies;
        // The resolved dependency closure is what --test-data hands the backup reader (TestDataProvisioner.ResolveSymbols).
        _references = BcCompiler.CaptureBundleReferenceState();
        _bundleInfo = BcRuntime.GetCurrentModuleAppInfo();
        _resourceDir = Patches.NavAppResourcePatches.CurrentBundleDir;
        _provisionGaps = includeProvisionGaps ? ProvisionGapLog.Capture() : null;
    }

    /// <param name="includeProvisionGaps">The CLI resets ProvisionGapLog per bundle and reads it after
    /// each bundle's run; --server never resets it per bundle, so restoring it there would drop the
    /// gaps a later bundle's load reported.</param>
    internal static BundleRunState Capture(bool includeProvisionGaps = false) => new(includeProvisionGaps);

    internal void Restore()
    {
        InstallTriggerRunner.ResetForNewBundle();
        InstallTriggerRunner.SetDependencyAssemblies(_installDependencies);
        BcCompiler.RestoreBundleReferenceState(_references);
        BcRuntime.SetCurrentBundleInfo(_bundleInfo.AppId, _bundleInfo.Name, _bundleInfo.Publisher, _bundleInfo.Version);
        Patches.NavAppResourcePatches.SetCurrentBundleDir(_resourceDir);
        if (_provisionGaps != null) ProvisionGapLog.Restore(_provisionGaps);
    }
}
