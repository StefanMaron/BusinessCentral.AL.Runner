// #4931: BundleRunState gives a deferred bundle run every single-slot value its own load left,
// after a later bundle's load has overwritten each one. BcCompiler's reference statics are shared,
// hence the serial collection.
using System.Reflection;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcCompilerSharedReferenceCollection.Name)]
public sealed class BundleRunStateTests
{
    private static readonly Assembly AsmA = typeof(BundleRunStateTests).Assembly;
    private static readonly Assembly AsmB = typeof(object).Assembly;

    private static void Load(string tag, Assembly installDep)
    {
        InstallTriggerRunner.ResetForNewBundle();
        InstallTriggerRunner.SetDependencyAssemblies(new[] { installDep });
        BcCompiler.SetResolvedDeps(
            new[] { (new AppManifest("Pub", tag, new Version(1, 0, 0, 0), Guid.NewGuid(), Array.Empty<DependencyRef>()), $"/deps/{tag}.app") },
            new[] { $"/pkg/{tag}" });
        BcCompiler.SetExtraSymbolDirs(new[] { $"/workspace/{tag}" });
        BcCompiler.SetCurrentAppIdentity(IdOf(tag), "Pub " + tag, new Version(2, 0, 0, 0));
        BcRuntime.SetCurrentBundleInfo(IdOf(tag), "App " + tag, "Pub " + tag, "3.0.0.0");
        NavAppResourcePatches.SetCurrentBundleDir($"/src/{tag}");
    }

    private static Guid IdOf(string tag) => tag == "A"
        ? new Guid("3b0e6f52-0000-4c38-9e25-0000000000b1") : new Guid("3b0e6f52-0000-4c38-9e25-0000000000b2");

    [Fact]
    public void Restore_GivesTheRunWhatItsOwnLoadLeft_AfterALaterLoadOverwroteIt()
    {
        var original = BundleRunState.Capture();
        try
        {
            Load("A", AsmA);
            var loadedA = BundleRunState.Capture();
            Load("B", AsmB);

            loadedA.Restore();

            Assert.Equal(new[] { AsmA }, InstallTriggerRunner.DependencyAssemblies);
            // What --test-data hands the backup reader (TestDataProvisioner.ResolveSymbols).
            Assert.Equal(new[] { "/deps/A.app" }, BcCompiler.ResolvedDepAppPaths());
            var references = BcCompiler.CaptureBundleReferenceState();
            Assert.Equal(new[] { "/pkg/A" }, references.PackageCacheDirs);
            Assert.Equal(new[] { "/workspace/A" }, references.ExtraSymbolDirs);
            Assert.Equal((IdOf("A"), "Pub A"), (references.AppId, references.Publisher));
            Assert.Equal((IdOf("A"), "App A"), (BcRuntime.GetCurrentModuleAppInfo().AppId, BcRuntime.GetCurrentModuleAppInfo().Name));
            Assert.Equal("/src/A", NavAppResourcePatches.CurrentBundleDir);
        }
        finally
        {
            original.Restore();
        }
    }

    /// <summary>The control: with nothing overwritten in between, restoring changes nothing, and the
    /// later load really did replace every slot (so the test above is not passing on a no-op load).</summary>
    [Fact]
    public void ALaterLoad_ReplacesEverySlot()
    {
        var original = BundleRunState.Capture();
        try
        {
            Load("A", AsmA);
            Load("B", AsmB);

            Assert.Equal(new[] { AsmB }, InstallTriggerRunner.DependencyAssemblies);
            Assert.Equal(new[] { "/deps/B.app" }, BcCompiler.ResolvedDepAppPaths());
            Assert.Equal(new[] { "/workspace/B" }, BcCompiler.CaptureBundleReferenceState().ExtraSymbolDirs);
            Assert.Equal(IdOf("B"), BcCompiler.CaptureBundleReferenceState().AppId);
            Assert.Equal("App B", BcRuntime.GetCurrentModuleAppInfo().Name);
            Assert.Equal("/src/B", NavAppResourcePatches.CurrentBundleDir);
        }
        finally
        {
            original.Restore();
        }
    }

    /// <summary>The CLI's per-bundle provisioning record: a deferred run names its own bundle's
    /// unservable apps, and its tail reports its own bundle's gaps.</summary>
    [Fact]
    public void ProvisionGapLog_RestoreGivesTheBundleItsOwnGapsAndUnservableApps()
    {
        var original = ProvisionGapLog.Capture();
        try
        {
            ProvisionGapLog.Reset();
            ProvisionGapLog.RegisterUnservableApp("App A", () => new[] { 70001 });
            ProvisionGapLog.Report("gap A");
            var loadedA = ProvisionGapLog.Capture();
            ProvisionGapLog.Reset();
            ProvisionGapLog.RegisterUnservableApp("App B", () => new[] { 70002 });
            ProvisionGapLog.Report("gap B");
            Assert.Equal("App B", ProvisionGapLog.UnservableAppDeclaringCodeunit(70002)); // builds the owner index

            ProvisionGapLog.Restore(loadedA);

            Assert.Equal(new[] { "gap A" }, ProvisionGapLog.Collected);
            Assert.Equal("App A", ProvisionGapLog.UnservableAppDeclaringCodeunit(70001));
            Assert.Null(ProvisionGapLog.UnservableAppDeclaringCodeunit(70002));
        }
        finally
        {
            ProvisionGapLog.Restore(original);
        }
    }
}
