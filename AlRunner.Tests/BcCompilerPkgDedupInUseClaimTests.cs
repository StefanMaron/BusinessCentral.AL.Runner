// BcCompilerPkgDedupInUseClaimTests — BOTH staging paths must claim the stage they hand to a
// compile (issue #2990).
//
// PkgDedupCache's age rule is only safe because a live process's stage carries a claim. That
// makes "did the claim get written" a property of DeduplicateAppPackageDirs, not of the cache
// class, and it has two exits that reach a compile:
//
//   * the CREATE path — stage the picked .app set into `<key>.tmp-<rand>` and publish it;
//   * the REUSE path — `Directory.Exists(<key>)`, hand the existing one straight back.
//
// The reuse path is the one that matters and the easy one to forget. A stage is WRITTEN once
// and READ on every reuse, so a stage a --watch session has reused daily for a month still has
// a month-old mtime; if only the create path claimed, the age rule would eventually delete a
// directory that is in constant use. This test therefore drives the same call twice and
// asserts the claim is recreated by the second call specifically.
//
// It asserts on the claim, never by pruning: this runs against the machine's real
// al-runner-pkgdedup root, which other runners share.
using System.IO.Compression;
using System.Reflection;
using System.Text;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class BcCompilerPkgDedupInUseClaimTests : IDisposable
{
    private readonly string _root;
    private readonly List<string> _stagesToClean = new();

    public BcCompilerPkgDedupInUseClaimTests()
    {
        _root = TestScratch.Dir("al-runner-pkgdedup-claim-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // Only what this test caused to exist: a stage keyed by this fixture's own GUID paths
        // cannot be anyone else's.
        foreach (var stage in _stagesToClean)
        {
            try { File.Delete(PkgDedupCache.InUseMarkerPath(stage)); } catch { }
            try { Directory.Delete(stage, recursive: true); } catch { }
        }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void BothStagingPaths_ClaimTheStageTheyHandToTheCompile()
    {
        var dirA = Path.Combine(_root, "a", ".alpackages");
        var dirB = Path.Combine(_root, "b", ".alpackages");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        var onlyInA = "aaaaaaaa-1111-0000-0000-0000000000a1";
        var shared = "bbbbbbbb-2222-0000-0000-0000000000b2";
        WriteApp(dirA, "OnlyInA.app", onlyInA, "Only In A", "Contoso", "1.0.0.0");
        WriteApp(dirA, "Shared.app", shared, "Shared", "Contoso", "1.0.0.0");
        // The cross-dir (AppId, Version) duplicate is what forces the staging branch.
        WriteApp(dirB, "Shared.app", shared, "Shared", "Contoso", "1.0.0.0");

        var packageDirs = new List<string> { dirA, dirB };

        // ── create path ──────────────────────────────────────────────────────────────────
        var first = InvokeDeduplicate(packageDirs);
        Assert.NotEqual(packageDirs, first);   // the staging branch really engaged
        var stage = Assert.Single(first);
        _stagesToClean.Add(stage);

        var marker = PkgDedupCache.InUseMarkerPath(stage);
        Assert.True(File.Exists(marker),
            $"the create path must claim the stage it returns; no '{marker}'");
        Assert.True(ScratchDirs.TryReadOwner(marker, out var pid, out _, out _));
        Assert.Equal(Environment.ProcessId, pid);

        // ── reuse path ───────────────────────────────────────────────────────────────────
        // Remove the claim and backdate the stage to well past any threshold, so the second
        // call has to re-establish both facts by itself.
        File.Delete(marker);
        var ancient = DateTime.UtcNow - TimeSpan.FromDays(30);
        Directory.SetLastWriteTimeUtc(stage, ancient);

        var second = InvokeDeduplicate(packageDirs);
        Assert.Equal(stage, Assert.Single(second));   // same content key, so the reuse branch ran

        Assert.True(File.Exists(marker),
            "the REUSE path must claim the stage too — a stage reused for months is never " +
            "rewritten, so nothing else would tell the prune it is alive");
        Assert.True(Directory.GetLastWriteTimeUtc(stage) > ancient + TimeSpan.FromDays(1),
            "reuse must stamp last-USE on the stage, or an age rule measures its creation date");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────

    private static List<string> InvokeDeduplicate(List<string> packageDirs)
    {
        var method = typeof(BcCompiler)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .SingleOrDefault(m => m.Name == "DeduplicateAppPackageDirs" && m.GetParameters().Length == 2)
            ?? throw new InvalidOperationException(
                "BcCompiler.DeduplicateAppPackageDirs(dirs, excludeAppId) not found by reflection.");
        return (List<string>)method.Invoke(null, new object?[] { packageDirs, null })!;
    }

    private static void WriteApp(string dir, string fileName,
        string appId, string name, string publisher, string version)
        => File.WriteAllBytes(Path.Combine(dir, fileName), MakeMinimalApp(appId, name, publisher, version));

    private static byte[] MakeMinimalApp(string appId, string name, string publisher, string version)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{appId}" Name="{name}" Publisher="{publisher}" Version="{version}"/>
            </Package>
            """;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var es = zip.CreateEntry("NavxManifest.xml").Open())
                es.Write(Encoding.UTF8.GetBytes(xml));
            // The dedup scan drops any .app without one before staging ever happens.
            using (var ss = zip.CreateEntry("SymbolReference.json").Open())
                ss.Write(Encoding.UTF8.GetBytes("{}"));
        }
        var zipBytes = ms.ToArray();
        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        return result;
    }
}
