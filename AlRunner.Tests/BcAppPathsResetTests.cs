// BcAppPathsResetTests — issue #2755.
//
// RecordPatches._bcAppPaths is process-global and nothing ever cleared it. The per-bundle reload
// path, ResetForReload, clears _parsedTables and every other source-derived dictionary and then
// calls InvalidateBcAppIndexes() — which drops the DERIVED table/extension indexes precisely so
// the next lookup rebuilds them FROM _bcAppPaths. So in --server and --watch, bundle 2 was
// compiled and run against its own registered .app symbols UNION every earlier bundle's, while a
// fresh single-bundle process running bundle 2 alone saw only its own.
//
// The neighbouring per-bundle state does reset: InstallTriggerRunner.ResetForNewBundle() clears
// _depAssemblies, and the server path's own comment says "New bundle in the server session:
// replace (not inherit) the install-trigger registrations". Two writers of the same per-bundle
// notion, one holding the invariant and one not.
//
// ── WHY THIS TEST IS SHAPED THIS WAY, WHICH IS MOST OF THE WORK ─────────────────────────────
//
// impl-7 measured two things before this could be written, both AFTER a fixture that passed and
// read like a result (see their comment on #2755):
//
//   * A SOURCE-ONLY bundle never touches _bcAppPaths at all. Its tables live in _parsedTables,
//     which ResetForReload does clear. A source-only fixture cannot express this defect and goes
//     green for an unrelated reason — they nearly reported exactly that.
//   * A layered-synthesized .app is not registrable either: manifest plus src/, no
//     SymbolReference.json, so RegisterBundleSymbolApps skips it. Copying one into a bundle root
//     would have produced a second fixture that also could not express the defect.
//
// So the assertion is made DIRECTLY against the registered set, through
// RecordPatches.RegisteredBcAppPathsForTests(). Inferring it from a downstream table lookup is
// what conflates this with _parsedTables — which the reset DOES clear, so such a test would pass
// on the broken build.
//
// The .app comes from SymbolAppFixture (impl-7, #2855): a registrable symbol package emitted
// in-process in milliseconds, platform-only and never an application floor.
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// MUST be serial. This mutates RecordPatches' process-global registration list and calls
// ResetForReload(), which clears roughly twenty static dictionaries — running it beside another
// test that reads any of them would corrupt that test, not this one, which is the hard kind of
// flake to trace back.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class BcAppPathsResetTests : IDisposable
{
    private readonly string _root;

    public BcAppPathsResetTests() => _root = TestScratch.Dir("al-runner-bcapppaths-reset");

    public void Dispose()
    {
        // Leave the process's registration list as it was found: this class runs inside a shared
        // engine, and a stray registration outlives the test that made it.
        try { RecordPatches.ResetForReload(); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string RegisterOneSymbolApp(string name, int tableId)
    {
        var bundleDir = Path.Combine(_root, name);
        var appPath = Path.Combine(bundleDir, $"{name}.app");
        SymbolAppFixture.WriteBundleAndApp(
            bundleDir, appPath, Guid.NewGuid(), name, tableId, $"{name} Table",
            withSymbolReference: true);
        RecordPatches.RegisterBundleSymbolApps(bundleDir);
        return appPath;
    }

    [Fact]
    public void ResetForReload_ClearsTheRegisteredSymbolApps()
    {
        RecordPatches.ResetForReload();
        var appPath = RegisterOneSymbolApp("BcpA", 70600);

        // Precondition, asserted rather than assumed: if registration silently did not happen —
        // which is exactly what a source-only or SymbolReference-less fixture produces — the
        // assertion below would pass for a reason that has nothing to do with the reset.
        Assert.Contains(appPath, RecordPatches.RegisteredBcAppPathsForTests());

        RecordPatches.ResetForReload();

        Assert.DoesNotContain(appPath, RecordPatches.RegisteredBcAppPathsForTests());
    }

    [Fact]
    public void ASecondBundleDoesNotInheritTheFirstBundlesSymbolApps()
    {
        // The defect as --server actually meets it: two bundles, one process, a reload between
        // them. Bundle 2 must see its own registration and NOT bundle 1's.
        RecordPatches.ResetForReload();
        var first = RegisterOneSymbolApp("BcpFirst", 70610);
        Assert.Contains(first, RecordPatches.RegisteredBcAppPathsForTests());

        // What Program.cs does between bundles in a server session: reset, then register this
        // bundle's own closure (Program.cs 2196/2200 then 2354/2357 on the CLI path, 4049/4529
        // then 4533/4534 on the server path — the reset precedes registration in both, which is
        // what makes clearing here safe).
        RecordPatches.ResetForReload();
        var second = RegisterOneSymbolApp("BcpSecond", 70620);

        var registered = RecordPatches.RegisteredBcAppPathsForTests();
        Assert.Contains(second, registered);
        Assert.DoesNotContain(first, registered);
    }

    [Fact]
    public void AfterTheReset_ReRegisteringTheSameAppStillWorks()
    {
        // The other direction, and the one that stops the fix from being "break registration".
        // Every bundle re-registers its FULL resolved closure — platform apps included — right
        // after the reset, so clearing is only safe if a cleared path can be registered again.
        // AddBcAppPath early-returns on a path already present, so a clear that did not really
        // clear would show up here as a missing re-registration.
        RecordPatches.ResetForReload();
        var appPath = RegisterOneSymbolApp("BcpAgain", 70630);
        Assert.Contains(appPath, RecordPatches.RegisteredBcAppPathsForTests());

        RecordPatches.ResetForReload();
        Assert.DoesNotContain(appPath, RecordPatches.RegisteredBcAppPathsForTests());

        RecordPatches.RegisterBundleSymbolApps(Path.GetDirectoryName(appPath)!);
        Assert.Contains(appPath, RecordPatches.RegisteredBcAppPathsForTests());
    }

    [Fact]
    public void TheRegisteredSetIsAnInputToTheInstallBaselineKey_SoClearingItMovesTheKey()
    {
        // #2710/#2753 folded the registered set into the install-baseline cache key precisely
        // because it varies between runs, and named --server accumulation as one of the two ways
        // it varies. This asserts the two facts stay connected: if the key ever stopped reading
        // the registered set, the accumulation would go silent again and this fix would be
        // load-bearing for nothing.
        RecordPatches.ResetForReload();
        var empty = InvokeStateKey();

        RegisterOneSymbolApp("BcpKeyed", 70640);
        var withApp = InvokeStateKey();
        Assert.NotEqual(empty, withApp);

        RecordPatches.ResetForReload();
        Assert.Equal(empty, InvokeStateKey());
    }

    private static string InvokeStateKey()
    {
        var m = typeof(RecordPatches).GetMethod(
            "RegisteredBcAppSymbolStateKey", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "RegisteredBcAppSymbolStateKey is gone — the install-baseline key may no longer "
                + "name the registered symbol set (#2710), which is what makes this accumulation "
                + "observable at all");
        return (string)m.Invoke(null, null)!;
    }
}
