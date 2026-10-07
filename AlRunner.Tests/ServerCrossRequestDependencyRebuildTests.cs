// #5081: one --server process, two directories that are checkouts of one app (same app id, same
// source), both resolving a library from ONE shared packagePaths directory. A request compiles X;
// the library is then rebuilt at the SAME version and the SAME path; a request for Y must answer
// what a fresh server answers, which is a module compiled against the rebuilt library.
//
// The library ships symbols only, so its one observable is what the dependent's compile bakes in:
// the ordinal of an enum value, inlined into the test module. The test reports it through its
// failure message, because X and Y must hold byte-identical source (a different source would
// already differ in BundleSourceFingerprint, #5079). Runner-specific (server module reuse), so it
// lives here, not in the al-language corpus.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerCrossRequestDependencyRebuildTests : IClassFixture<SharedCliServer>
{
    private readonly SharedCliServer _fixture;

    public ServerCrossRequestDependencyRebuildTests(SharedCliServer fixture) => _fixture = fixture;

    // Each fact declares its own library and app ids, shared only by that fact's two directories,
    // so no fact on the shared server can be answered from another fact's module.
    private static string LibraryId(int fact) => $"5081c0de-0000-4a11-9111-00000000{fact:D2}01";
    private static string AppId(int fact) => $"5081c0de-0000-4a11-9111-00000000{fact:D2}02";

    private static byte[] BuildLibrary(int fact, int secondOrdinal)
    {
        var id = LibraryId(fact);
        var manifest = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{id}" Name="rebuild-lib-{fact}" Publisher="repro" Version="1.0.0.0" Runtime="16.0" Target="Cloud" />
              <IdRanges><IdRange MinObjectId="{LibraryEnumId(fact)}" MaxObjectId="{LibraryEnumId(fact)}" /></IdRanges>
              <Dependencies />
            </Package>
            """;
        var symbols = $$"""
            {"RuntimeVersion":"16.0","Codeunits":[],"Tables":[],"Reports":[],"XmlPorts":[],"Queries":[],"ControlAddIns":[],"EnumTypes":[{"Values":[{"Name":"First"},{"Ordinal":{{secondOrdinal}},"Name":"Second"}],"Id":{{LibraryEnumId(fact)}},"Name":"Rebuild Kind {{fact}}"}],"DotNetPackages":[],"Interfaces":[],"PermissionSets":[],"PermissionSetExtensions":[],"ReportExtensions":[],"InternalsVisibleToModules":[],"AppId":"{{id}}","Name":"rebuild-lib-{{fact}}","Publisher":"repro","Version":"1.0.0.0"}
            """;
        return NavxPackageBuilder.Build(id, manifest, symbols, Array.Empty<(string, string)>());
    }

    private static int LibraryEnumId(int fact) => 65400 + fact;
    private static int TestCodeunitId(int fact) => 65420 + fact;

    /// <summary>The library, rebuilt in place: same file name, same version, a different ordinal.</summary>
    private static void PublishLibrary(string packages, int fact, int secondOrdinal)
    {
        Directory.CreateDirectory(packages);
        var path = Path.Combine(packages, $"repro_rebuild-lib-{fact}_1.0.0.0.app");
        var before = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.UtcNow;
        File.WriteAllBytes(path, BuildLibrary(fact, secondOrdinal));
        // Two builds can share a length; move the mtime forward so no (inode, size, mtime) file
        // identity can mistake the rebuild for the file it replaced.
        File.SetLastWriteTimeUtc(path, before.AddSeconds(5));
    }

    /// <summary>The rebuild a user-space tool can make invisible to a stat: the same file (inode), the
    /// same length, the mtime put back (`cp -p`, `rsync -t --inplace`, an archive-normalised extract).
    /// Only ctime, which no tool can set, tells it from the build it replaced (#5409).</summary>
    private static int RewriteLibraryInPlaceKeepingSizeAndMtime(string packages, int fact)
    {
        var path = Path.Combine(packages, $"repro_rebuild-lib-{fact}_1.0.0.0.app");
        var mtime = File.GetLastWriteTimeUtc(path);
        var length = new FileInfo(path).Length;
        // Deflate makes the length depend on the digits, so look for an ordinal that keeps it.
        var ordinal = Enumerable.Range(8, 90).First(o => BuildLibrary(fact, o).Length == length);
        File.WriteAllBytes(path, BuildLibrary(fact, ordinal));
        File.SetLastWriteTimeUtc(path, mtime);
        Assert.Equal(length, new FileInfo(path).Length);
        return ordinal;
    }

    private static void WriteCheckout(string dir, int fact)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{AppId(fact)}}",
          "name": "Rebuild Tests {{fact}}",
          "publisher": "repro",
          "version": "1.0.0.0",
          "dependencies": [ { "id": "{{LibraryId(fact)}}", "name": "rebuild-lib-{{fact}}", "publisher": "repro", "version": "1.0.0.0" } ],
          "idRanges": [ { "from": {{TestCodeunitId(fact)}}, "to": {{TestCodeunitId(fact)}} } ],
          "runtime": "16.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), $$"""
        codeunit {{TestCodeunitId(fact)}} "Rebuild Tests {{fact}}"
        {
            Subtype = Test;

            [Test]
            procedure ReportOrdinal()
            begin
                Error('ordinal=%1', Enum::"Rebuild Kind {{fact}}"::Second.AsInteger());
            end;
        }
        """);
    }

    /// <summary>One runTests request; the ordinal its dependent's compile baked in, and the server's
    /// stderr for that request.</summary>
    private static async Task<(string Ordinal, string StdErr)> Request(CliServer server, string packages, string checkout)
    {
        var mark = server.StdErrMark;
        var lines = await server.SendRequestStreamingAsync(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["command"] = "runTests", ["sourcePaths"] = new[] { checkout }, ["packagePaths"] = new[] { packages },
        }), TimeSpan.FromSeconds(240));
        var stderr = server.StdErrSince(mark);
        var (events, _) = ProtocolV2Streaming.Split(lines);
        var ev = Assert.Single(events);
        var message = ev.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        var at = message.IndexOf("ordinal=", StringComparison.Ordinal);
        Assert.True(at >= 0, $"the test did not report an ordinal: {message}\n{string.Join(" | ", lines)}\n--- stderr ---\n{stderr}");
        return (message[(at + "ordinal=".Length)..].Split(' ', '\n')[0], stderr);
    }

    private const string ReuseNote = "reusing that module instead of recompiling";

    [SkippableFact]
    public async Task LibraryRebuiltInPlace_OtherCheckoutOfTheApp_CompilesAgainstTheRebuiltLibrary()
    {
        TestArtifacts.SkipIfMissing();
        const int fact = 1;
        var root = TestScratch.Dir("al-runner-server-rebuild-5081-rebuilt");
        var packages = Path.Combine(root, "packages");
        var x = Path.Combine(root, "x");
        var y = Path.Combine(root, "y");
        WriteCheckout(x, fact);
        WriteCheckout(y, fact);
        try
        {
            var server = await _fixture.GetAsync(new[] { "--no-cache" });
            PublishLibrary(packages, fact, secondOrdinal: 7);
            var first = await Request(server, packages, x);
            Assert.True(first.Ordinal == "7", $"X compiled against the first build: {first.Ordinal}\n{first.StdErr}");

            PublishLibrary(packages, fact, secondOrdinal: 9);
            var second = await Request(server, packages, y);
            // 7 is X's module reused for Y: its compile saw the library as it was before the rebuild.
            Assert.True(second.Ordinal == "9", $"Y answered ordinal {second.Ordinal}, not the rebuilt library's 9\n{second.StdErr}");
            Assert.DoesNotContain(ReuseNote, second.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>#5409: the rebuild a stat cannot see. Without ctime in the content-hash memo's key the
    /// old hash is served for the new bytes, the fingerprint matches, and Y reuses X's module.</summary>
    [SkippableFact]
    public async Task LibraryRewrittenInPlaceWithTheSameSizeAndMtime_OtherCheckoutCompilesAgainstIt()
    {
        TestArtifacts.SkipIfMissing();
        const int fact = 4;
        var root = TestScratch.Dir("al-runner-server-rebuild-5409-inplace");
        var packages = Path.Combine(root, "packages");
        var x = Path.Combine(root, "x");
        var y = Path.Combine(root, "y");
        WriteCheckout(x, fact);
        WriteCheckout(y, fact);
        try
        {
            var server = await _fixture.GetAsync(new[] { "--no-cache" });
            PublishLibrary(packages, fact, secondOrdinal: 7);
            var first = await Request(server, packages, x);
            Assert.True(first.Ordinal == "7", first.StdErr);

            var rewritten = RewriteLibraryInPlaceKeepingSizeAndMtime(packages, fact);
            var second = await Request(server, packages, y);
            Assert.True(second.Ordinal == rewritten.ToString(),
                $"Y answered ordinal {second.Ordinal}, not the rewritten library's {rewritten}\n{second.StdErr}");
            Assert.DoesNotContain(ReuseNote, second.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>The other half of the same staleness, with no module reuse in it: the SAME directory is
    /// asked again after the library was rebuilt, so it is always compiled afresh, and what it binds to
    /// is whatever symbols the warm compile loader still holds for the package.</summary>
    [SkippableFact]
    public async Task LibraryRebuiltInPlace_SameCheckoutAgain_CompilesAgainstTheRebuiltLibrary()
    {
        TestArtifacts.SkipIfMissing();
        const int fact = 3;
        var root = TestScratch.Dir("al-runner-server-rebuild-5081-same");
        var packages = Path.Combine(root, "packages");
        var x = Path.Combine(root, "x");
        WriteCheckout(x, fact);
        try
        {
            var server = await _fixture.GetAsync(new[] { "--no-cache" });
            PublishLibrary(packages, fact, secondOrdinal: 7);
            var first = await Request(server, packages, x);
            Assert.True(first.Ordinal == "7", first.StdErr);

            PublishLibrary(packages, fact, secondOrdinal: 9);
            var second = await Request(server, packages, x);
            Assert.True(second.Ordinal == "9", $"X answered ordinal {second.Ordinal}, not the rebuilt library's 9\n{second.StdErr}");

            // And back: the symbols of the build that is on disk now, never a remembered one.
            PublishLibrary(packages, fact, secondOrdinal: 7);
            var third = await Request(server, packages, x);
            Assert.True(third.Ordinal == "7", $"X answered ordinal {third.Ordinal}, not the first build's 7\n{third.StdErr}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>The control: the reuse #5077 exists for. Nothing was rebuilt, so the other checkout
    /// still takes the module the first request compiled rather than compiling its own.</summary>
    [SkippableFact]
    public async Task LibraryUnchanged_OtherCheckoutOfTheApp_StillReusesTheModule()
    {
        TestArtifacts.SkipIfMissing();
        const int fact = 2;
        var root = TestScratch.Dir("al-runner-server-rebuild-5081-control");
        var packages = Path.Combine(root, "packages");
        var x = Path.Combine(root, "x");
        var y = Path.Combine(root, "y");
        WriteCheckout(x, fact);
        WriteCheckout(y, fact);
        try
        {
            var server = await _fixture.GetAsync(new[] { "--no-cache" });
            PublishLibrary(packages, fact, secondOrdinal: 7);
            var first = await Request(server, packages, x);
            Assert.True(first.Ordinal == "7", first.StdErr);
            Assert.DoesNotContain(ReuseNote, first.StdErr, StringComparison.Ordinal);

            // Same bytes written again: a content identity must not read a rewrite as a change.
            PublishLibrary(packages, fact, secondOrdinal: 7);
            var second = await Request(server, packages, y);
            Assert.True(second.Ordinal == "7", second.StdErr);
            Assert.True(second.StdErr.Contains(ReuseNote, StringComparison.Ordinal),
                $"Y compiled its own module although nothing was rebuilt:\n{second.StdErr}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
