// DependencyResolverTests — version-aware resolution contract.
//
// Root cause being tested
// -----------------------
// DependencyResolver previously indexed .app packages with "first-wins" semantics
// and ignored the declared minimum version when selecting among candidates. An ISV
// that vendors a stale Microsoft symbol-only .app (e.g. Tests-TestLibraries v17.0)
// in its .alpackages dir could cause the resolver to bind to v17 even when v28.1
// was available in a package-cache dir, because the ISV .alpackages dir is indexed
// first. BC then compiled against v17 symbols, baking v17 function IDs into emitted
// C#. At runtime, BC 28.1 dispatch only recognises current IDs → NavNCLCompilationException.
//
// Fix: resolver now keeps ALL candidates per AppId / (Name, Publisher) and selects
// the highest-version candidate whose version >= the declared minimum. The minimum-
// version semantics match what a real BC build (alc) does.
//
// Test strategy
// -------------
// Unit tests against DependencyResolver in isolation, using synthetic minimal .app
// fixtures written to a per-test temp directory. Asserts concrete versions and paths.

using System.IO.Compression;
using System.Text;
using Xunit;
using AlRunnerV2;

namespace AlRunnerV2.Tests;

public sealed class DependencyResolverTests : IDisposable
{
    private readonly string _root;

    public DependencyResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-resolver-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Part 1: highest-satisfying-version selection ───────────────────────────

    /// <summary>
    /// Two dirs: A has v17.0.0.0, B has v28.1.49838.50621 for the SAME AppId.
    /// A dep declaring minimum v17 must bind to v28.1, not v17.
    /// This is the exact scenario that triggered the stale-symbol workaround.
    /// FAILS on old (first-wins) code, PASSES after the fix.
    /// </summary>
    [Fact]
    public void TwoVersions_SameAppId_HigherVersionChosen_WhenBothSatisfyMinimum()
    {
        var appId = "aaaaaaaa-0000-0000-0000-000000000001";
        var dirA = MakeDir("A");
        var dirB = MakeDir("B");

        WriteApp(dirA, "TestLib_v17.app",   appId, "Tests-TestLibraries", "Microsoft", "17.0.0.0");
        WriteApp(dirB, "TestLib_v28.app",   appId, "Tests-TestLibraries", "Microsoft", "28.1.49838.50621");

        var resolver = new DependencyResolver(new[] { dirA, dirB });
        var dep = new DependencyRef(Guid.Parse(appId), "Tests-TestLibraries", "Microsoft",
            new Version(17, 0, 0, 0));

        var result = resolver.Resolve(new[] { dep });

        Assert.Single(result);
        Assert.Equal(new Version(28, 1, 49838, 50621), result[0].Manifest.Version);
        Assert.Contains("v28", result[0].AppPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Dirs in reverse order (v28 dir first, v17 dir second) — should still pick v28.1.
    /// </summary>
    [Fact]
    public void TwoVersions_SameAppId_HigherVersionChosen_RegardlessOfDirOrder()
    {
        var appId = "aaaaaaaa-0000-0000-0000-000000000002";
        var dirA = MakeDir("C");
        var dirB = MakeDir("D");

        WriteApp(dirA, "TestLib_v28.app",   appId, "Tests-TestLibraries", "Microsoft", "28.1.49838.50621");
        WriteApp(dirB, "TestLib_v17.app",   appId, "Tests-TestLibraries", "Microsoft", "17.0.0.0");

        var resolver = new DependencyResolver(new[] { dirA, dirB });
        var dep = new DependencyRef(Guid.Parse(appId), "Tests-TestLibraries", "Microsoft",
            new Version(17, 0, 0, 0));

        var result = resolver.Resolve(new[] { dep });

        Assert.Single(result);
        Assert.Equal(new Version(28, 1, 49838, 50621), result[0].Manifest.Version);
    }

    /// <summary>
    /// Only one version available; it satisfies the minimum → resolves to that version.
    /// </summary>
    [Fact]
    public void OnlyVersion_SatisfiesMinimum_ReturnsIt()
    {
        var appId = "aaaaaaaa-0000-0000-0000-000000000003";
        var dir = MakeDir("E");
        WriteApp(dir, "TestLib.app", appId, "MyApp", "Publisher", "5.0.0.0");

        var resolver = new DependencyResolver(new[] { dir });
        var dep = new DependencyRef(Guid.Parse(appId), "MyApp", "Publisher",
            new Version(5, 0, 0, 0));

        var result = resolver.Resolve(new[] { dep });

        Assert.Single(result);
        Assert.Equal(new Version(5, 0, 0, 0), result[0].Manifest.Version);
    }

    // ── Part 2: version-not-satisfied error message ────────────────────────────

    /// <summary>
    /// Dep requires minimum v29.0; only v17 and v28.1 are available.
    /// Must throw InvalidOperationException whose message names the available versions.
    /// (This is a version-mismatch problem, not a provisioning gap.)
    /// </summary>
    [Fact]
    public void MinimumNotSatisfied_ThrowsWithVersionDetail()
    {
        var appId = "aaaaaaaa-0000-0000-0000-000000000004";
        var dirA = MakeDir("F");
        var dirB = MakeDir("G");

        WriteApp(dirA, "Lib_v17.app",  appId, "TestLib", "Microsoft", "17.0.0.0");
        WriteApp(dirB, "Lib_v28.app",  appId, "TestLib", "Microsoft", "28.1.49838.50621");

        var resolver = new DependencyResolver(new[] { dirA, dirB });
        var dep = new DependencyRef(Guid.Parse(appId), "TestLib", "Microsoft",
            new Version(29, 0, 0, 0));

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Resolve(new[] { dep }));
        // Error must mention the too-low versions so the problem is obviously a version issue.
        Assert.Contains("29.0", ex.Message);
        Assert.Contains("17.0", ex.Message);
        Assert.Contains("28.1", ex.Message);
    }

    // ── Part 2b: completely-absent dep → MissingDependencyException ───────────

    /// <summary>
    /// A dep declared in the manifest is completely absent from every cache dir.
    /// Must throw MissingDependencyException (not InvalidOperationException) so Program.cs
    /// can emit a loud provisioning-gap message and abort before a doomed compile.
    /// </summary>
    [Fact]
    public void DepCompletelyAbsent_ThrowsMissingDependencyException()
    {
        var emptyDir = MakeDir("MDE_empty");
        var resolver = new DependencyResolver(new[] { emptyDir });
        var dep = new DependencyRef(
            Guid.Parse("bee8cf2f-494a-42f4-aabd-650e87934d39"),
            "Business Foundation Test Libraries", "Microsoft", new Version(28, 2, 0, 0));

        Assert.Throws<AlRunnerV2.Infrastructure.MissingDependencyException>(
            () => resolver.Resolve(new[] { dep }));
    }

    /// <summary>
    /// MissingDependencyException carries the dep's identity + searched dirs.
    /// The exception message names the publisher, name, and searched dir so the user sees
    /// exactly what is missing and where it was looked for.
    /// </summary>
    [Fact]
    public void DepCompletelyAbsent_ExceptionNamesDepAndSearchedDir()
    {
        var dir = MakeDir("MDE_detail");
        var resolver = new DependencyResolver(new[] { dir });
        var depId = Guid.Parse("bee8cf2f-494a-42f4-aabd-650e87934d39");
        var dep = new DependencyRef(
            depId, "Business Foundation Test Libraries", "Microsoft", new Version(28, 2, 0, 0));

        var ex = Assert.Throws<AlRunnerV2.Infrastructure.MissingDependencyException>(
            () => resolver.Resolve(new[] { dep }));

        Assert.Equal("Microsoft", ex.DepPublisher);
        Assert.Equal("Business Foundation Test Libraries", ex.DepName);
        Assert.Equal("28.2.0.0", ex.DepVersion);
        Assert.Equal(depId, ex.DepAppId);
        Assert.Contains(dir, ex.SearchedDirs);
    }

    /// <summary>
    /// ToDetailedMessage for a Microsoft dep names the al-runner provision command and
    /// the DownloadArtifacts test-apps fix so the user can resolve it in one command.
    /// </summary>
    [Fact]
    public void DepCompletelyAbsent_ToDetailedMessage_NamesProvisionCommandForMicrosoftDep()
    {
        var dir = MakeDir("MDE_msg_ms");
        var ex = new AlRunnerV2.Infrastructure.MissingDependencyException(
            "Microsoft", "Business Foundation Test Libraries", "28.2.0.0",
            Guid.Parse("bee8cf2f-494a-42f4-aabd-650e87934d39"),
            new[] { dir });

        var msg = ex.ToDetailedMessage("28.2.50931.52786");

        // Names the missing dep.
        Assert.Contains("Business Foundation Test Libraries", msg);
        Assert.Contains("28.2.0.0", msg);
        Assert.Contains("Microsoft", msg);
        // Names the provision command.
        Assert.Contains("al-runner provision", msg);
        // Names the DownloadArtifacts test-apps fix.
        Assert.Contains("test-apps", msg);
        Assert.Contains("28.2.50931.52786", msg);
        // Frames it as a provisioning gap, not a user-code error.
        Assert.Contains("PROVISIONING gap", msg);
        Assert.Contains("your code is NOT the problem", msg);
    }

    /// <summary>
    /// ToDetailedMessage for a non-Microsoft dep does NOT mention al-runner provision
    /// (a third-party dep can't be auto-provisioned from the MS CDN).
    /// </summary>
    [Fact]
    public void DepCompletelyAbsent_ToDetailedMessage_NoProvisionForThirdPartyDep()
    {
        var dir = MakeDir("MDE_msg_3p");
        var ex = new AlRunnerV2.Infrastructure.MissingDependencyException(
            "Contoso", "Contoso Core Library", "5.0.0.0",
            Guid.NewGuid(), new[] { dir });

        var msg = ex.ToDetailedMessage("28.2.50931.52786");

        Assert.Contains("Contoso Core Library", msg);
        Assert.Contains("PROVISIONING gap", msg);
        // Should NOT suggest the MS provision path for a third-party dep.
        Assert.DoesNotContain("test-apps", msg);
        Assert.DoesNotContain("platform-apps", msg);
    }

    /// <summary>
    /// Version near-miss (dep found but below minimum) still throws InvalidOperationException,
    /// not MissingDependencyException — backward compatibility for existing callers.
    /// </summary>
    [Fact]
    public void VersionNearMiss_StillThrowsInvalidOperationException_NotMissingDependencyException()
    {
        var appId = "dddddddd-0000-0000-0000-000000000001";
        var dir = MakeDir("MDE_nearmiss");
        WriteApp(dir, "App_v5.app", appId, "SomeLib", "SomePub", "5.0.0.0");

        var resolver = new DependencyResolver(new[] { dir });
        var dep = new DependencyRef(Guid.Parse(appId), "SomeLib", "SomePub",
            new Version(10, 0, 0, 0)); // requires v10 but only v5 exists

        // Must be InvalidOperationException (version near-miss), NOT MissingDependencyException
        // (completely absent).
        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Resolve(new[] { dep }));
        Assert.IsNotType<AlRunnerV2.Infrastructure.MissingDependencyException>(ex);
        Assert.Contains("5.0", ex.Message);
    }

    // ── Part 3: Name+Publisher fallback ───────────────────────────────────────

    /// <summary>
    /// Dep declares AppId=empty (no GUID); resolver must fall back to Name+Publisher lookup
    /// and still pick the highest satisfying version.
    /// </summary>
    [Fact]
    public void NamePublisherFallback_PicksHighestSatisfyingVersion()
    {
        var appId = "bbbbbbbb-0000-0000-0000-000000000001";
        var dirA = MakeDir("H");
        var dirB = MakeDir("I");

        WriteApp(dirA, "App_v10.app", appId, "FooApp", "BarPub", "10.0.0.0");
        WriteApp(dirB, "App_v20.app", appId, "FooApp", "BarPub", "20.0.0.0");

        var resolver = new DependencyResolver(new[] { dirA, dirB });
        // Note: AppId = Guid.Empty → name+publisher lookup path.
        var dep = new DependencyRef(Guid.Empty, "FooApp", "BarPub", new Version(10, 0, 0, 0));

        var result = resolver.Resolve(new[] { dep });

        Assert.Single(result);
        Assert.Equal(new Version(20, 0, 0, 0), result[0].Manifest.Version);
    }

    // ── Part 4: AppId near-miss must NOT fall through to Name+Publisher ────────

    /// <summary>
    /// Dep specifies AppId X. The index has AppId X but only at v5 (too old for min=v10).
    /// A DIFFERENT app with the same (Name, Publisher) but AppId Y is also in the index.
    /// The resolver must NOT silently pick AppId Y — that is a different package.
    /// It must throw/return-false, reporting the version near-miss.
    /// </summary>
    [Fact]
    public void AppIdNearMiss_DoesNotFallThroughToNamePublisher()
    {
        var appIdX = "cccccccc-0000-0000-0000-000000000001";
        var appIdY = "cccccccc-0000-0000-0000-000000000002";
        var dirA = MakeDir("J");
        var dirB = MakeDir("K");

        // AppId X with old version in dirA.
        WriteApp(dirA, "AppX_v5.app",  appIdX, "Shared", "Vendor", "5.0.0.0");
        // AppId Y with same name/publisher but different AppId in dirB (newer version).
        WriteApp(dirB, "AppY_v20.app", appIdY, "Shared", "Vendor", "20.0.0.0");

        var resolver = new DependencyResolver(new[] { dirA, dirB });
        // Ask for AppId X with minimum v10 (which only X is indexed for, but X is too old).
        var dep = new DependencyRef(Guid.Parse(appIdX), "Shared", "Vendor",
            new Version(10, 0, 0, 0));

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Resolve(new[] { dep }));
        // Should report that v5 was found (near-miss) — not silently succeed.
        Assert.Contains("5.0", ex.Message);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private string MakeDir(string name)
    {
        var d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Writes a minimal NAVX .app file (header + ZIP with NavxManifest.xml).</summary>
    private static void WriteApp(string dir, string fileName,
        string appId, string name, string publisher, string version)
    {
        File.WriteAllBytes(Path.Combine(dir, fileName), MakeMinimalApp(appId, name, publisher, version));
    }

    private static byte[] MakeMinimalApp(string appId, string name, string publisher, string version)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{appId}" Name="{name}" Publisher="{publisher}" Version="{version}"/>
            </Package>
            """;

        // Build ZIP containing NavxManifest.xml.
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("NavxManifest.xml");
            using var es = entry.Open();
            es.Write(Encoding.UTF8.GetBytes(xml));
        }
        var zipBytes = ms.ToArray();

        // NAVX wrapper: magic "NAVX" + LE uint32 ZIP offset (8) + ZIP bytes.
        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        return result;
    }
}
