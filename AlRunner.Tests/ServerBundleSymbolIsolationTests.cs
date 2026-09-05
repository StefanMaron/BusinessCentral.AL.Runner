// ServerBundleSymbolIsolationTests — #2755, the AL-visible half.
//
// Two DIFFERENT bundles, two requests, ONE server process. Bundle Alpha ships a prebuilt
// `.app` in its bundle root carrying a SymbolReference.json that declares table 60915; the
// runner registers it through RecordPatches.RegisterBundleSymbolApps. Bundle Beta declares no
// dependency on Alpha, ships no `.app`, and has no way to know that table exists.
//
// Before the fix, _bcAppPaths only ever grew — nothing cleared it, while every index derived
// from it was dropped per request so it would rebuild FROM it. So request 2 rebuilt the Table
// Metadata (2000000136) row set from Alpha's `.app` as well as its own, and Beta could read a
// table belonging to a bundle it has never heard of. A fresh single-bundle process running
// Beta alone cannot see it. Exit code unchanged either way: this is a wrong answer, not a
// crash, and it is invisible to any single-bundle run.
//
// Why a bundle-root `.app` and not two source-only bundles: a source-only bundle's tables land
// in _parsedTables, which ResetForReload ALREADY clears. _bcAppPaths is fed only by
// AddBcAppPath, whose three feeders are dependency `.app` packages (Program.cs) and
// RegisterBundleSymbolApps' scan of the bundle root. A source-only fixture therefore cannot
// express this defect at all — it goes green for a reason that has nothing to do with the
// accumulation (recorded on #2755 by an earlier agent that measured exactly that).
//
// The three arms, all in request 2, so a partial fix cannot pass:
//   * Alpha's ghost table must NOT be visible          — the contamination.
//   * Beta's OWN table MUST be visible                 — clearing too much would break this.
//   * Beta's tests must run at all                     — the SystemApp package (Field,
//     RecordLink, Object …) is registered once at hook-install time and never again, so a
//     reset that dropped it would take the system tables down with it from request 2 on.
//
// Request 1 carries its own positive control: Alpha asserts its ghost table IS visible while
// Alpha is the bundle being run. Without that, a green request 2 could mean the bundle-root
// `.app` was never registered in the first place and the experiment proved nothing.

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerBundleSymbolIsolationTests
{
    // 60915-60919, checked free across AlRunner.Tests and tests/runner-extras.
    private const int GhostTableId = 60915;

    private static string Req(string dir)
        => JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { dir },
            packagePaths = Array.Empty<string>(),
        });

    /// <summary>
    /// A minimal but COMPLETE symbol package: AddBcAppPath reads both symbol surfaces to
    /// completion (#2712) and throws if either fails, and RegisterBundleSymbolApps only
    /// registers a bundle-root `.app` that AppLoader.HasSymbolReference accepts.
    /// </summary>
    private static void WriteGhostSymbolApp(string bundleRoot)
    {
        using var fs = new FileStream(Path.Combine(bundleRoot, "IsoGhost_1.0.0.0.app"), FileMode.Create);
        using var za = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write($$"""
            {
              "RuntimeVersion": "15.1",
              "Namespaces": [],
              "Tables": [
                {
                  "Id": {{GhostTableId}},
                  "Name": "Iso Ghost Table",
                  "Properties": [],
                  "Fields": [
                    { "TypeDefinition": { "Name": "Code[20]" }, "Properties": [], "Id": 1, "Name": "Code" }
                  ]
                }
              ],
              "EnumTypes": [],
              "TableExtensions": []
            }
            """);
    }

    private static (string alphaDir, string betaDir) WriteBundles()
    {
        var root = TestScratch.Dir("al-runner-2755-server");
        var alphaDir = Path.Combine(root, "Alpha");
        var betaDir = Path.Combine(root, "Beta");
        Directory.CreateDirectory(alphaDir);
        Directory.CreateDirectory(betaDir);

        // ── Alpha: app.json (RegisterBundleSymbolApps only runs for a bundle that has one)
        //    plus the ghost symbol package in its root. ────────────────────────────────────
        File.WriteAllText(Path.Combine(alphaDir, "app.json"), """
        {
          "id": "b2f5c4a1-7d33-4e58-9b02-6a1e5c7d9f11",
          "name": "Iso Alpha",
          "publisher": "AL Runner Repro",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 60916, "to": 60917 } ],
          "platform": "1.0.0.0",
          "runtime": "14.0"
        }
        """);
        WriteGhostSymbolApp(alphaDir);
        File.WriteAllText(Path.Combine(alphaDir, "AlphaTests.Codeunit.al"), $$"""
        codeunit 60916 "Iso Alpha Tests"
        {
            Subtype = Test;

            // Positive control for the whole experiment: while ALPHA is the bundle under
            // test, its bundle-root .app IS registered and its table IS visible. If this
            // fails, the fixture never exercised the accumulation and request 2 proves
            // nothing.
            [Test]
            procedure GhostTable_IsVisibleWhileItsOwnBundleRuns()
            var
                TableMetadata: Record "Table Metadata";
            begin
                if not TableMetadata.Get({{GhostTableId}}) then
                    Error('Table Metadata.Get(%1) returned false in Alpha''s own request — the bundle-root .app was never registered, so this fixture cannot observe #2755 at all.', {{GhostTableId}});
                if TableMetadata.Name <> 'Iso Ghost Table' then
                    Error('Table Metadata.Name was "%1", expected "Iso Ghost Table".', TableMetadata.Name);
            end;
        }
        """);

        // ── Beta: its own table, no dependency on Alpha, no .app of its own. ─────────────
        File.WriteAllText(Path.Combine(betaDir, "app.json"), """
        {
          "id": "b2f5c4a1-7d33-4e58-9b02-6a1e5c7d9f22",
          "name": "Iso Beta",
          "publisher": "AL Runner Repro",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 60918, "to": 60919 } ],
          "platform": "1.0.0.0",
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(betaDir, "BetaRow.Table.al"), """
        table 60919 "Iso Beta Row"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Code"; Code[20]) { }
            }
            keys { key(PK; "Code") { Clustered = true; } }
        }
        """);
        File.WriteAllText(Path.Combine(betaDir, "BetaTests.Codeunit.al"), $$"""
        codeunit 60918 "Iso Beta Tests"
        {
            Subtype = Test;

            // The contamination arm. Beta has no dependency on Alpha and ships no symbol
            // package, so a fresh single-bundle process running Beta alone answers "no row".
            [Test]
            procedure GhostTableFromEarlierRequest_IsNotVisible()
            var
                TableMetadata: Record "Table Metadata";
            begin
                if TableMetadata.Get({{GhostTableId}}) then
                    Error('Table Metadata.Get(%1) returned a row named "%2" — that table belongs to a DIFFERENT bundle run earlier in this same server process, which this bundle does not depend on and could not see in a fresh process.', {{GhostTableId}}, TableMetadata.Name);
            end;

            // The positive arm. A reset that cleared the registration list and did not let
            // the current bundle repopulate it, or that dropped the SystemApp package with
            // it, would pass the arm above and fail here.
            [Test]
            procedure OwnTable_IsStillVisible()
            var
                TableMetadata: Record "Table Metadata";
            begin
                if not TableMetadata.Get(Database::"Iso Beta Row") then
                    Error('Table Metadata.Get(%1) returned false for this bundle''s OWN table.', Database::"Iso Beta Row");
                if TableMetadata.Name <> 'Iso Beta Row' then
                    Error('Table Metadata.Name was "%1", expected "Iso Beta Row".', TableMetadata.Name);
            end;
        }
        """);

        return (alphaDir, betaDir);
    }

    [SkippableFact]
    public async Task SecondRequestInOneServerProcess_CannotSeeTheFirstBundlesSymbolPackage()
    {
        TestArtifacts.SkipIfMissing();

        var (alphaDir, betaDir) = WriteBundles();
        // A dedicated server, not the shared one: the claim is about what a process has
        // accumulated, so the set of requests this process has served has to be exactly the
        // two below.
        await using var server = await CliServer.StartAsync(
            new[] { "--cache", TestScratch.Dir("al-runner-2755-server-cache") });

        // ── Request 1: Alpha alone. Its bundle-root .app registers; its own test proves so.
        var lines1 = await server.SendRequestStreamingAsync(Req(alphaDir), TimeSpan.FromSeconds(180));
        var (events1, d1) = ProtocolV2Streaming.Split(lines1);
        var alphaEvent = events1.Single(e => e.GetProperty("name").GetString()!.EndsWith("GhostTable_IsVisibleWhileItsOwnBundleRuns"));
        Assert.Equal("pass", alphaEvent.GetProperty("status").GetString());
        Assert.Equal(0, d1.GetProperty("failed").GetInt32());
        Assert.Equal(0, d1.GetProperty("errors").GetInt32());

        // ── Request 2: Beta alone, SAME process. ────────────────────────────────────────
        var lines2 = await server.SendRequestStreamingAsync(Req(betaDir), TimeSpan.FromSeconds(180));
        var (events2, d2) = ProtocolV2Streaming.Split(lines2);

        var isolation = events2.Single(e => e.GetProperty("name").GetString()!.EndsWith("GhostTableFromEarlierRequest_IsNotVisible"));
        Assert.Equal("pass", isolation.GetProperty("status").GetString());

        var ownTable = events2.Single(e => e.GetProperty("name").GetString()!.EndsWith("OwnTable_IsStillVisible"));
        Assert.Equal("pass", ownTable.GetProperty("status").GetString());

        Assert.Equal(0, d2.GetProperty("failed").GetInt32());
        Assert.Equal(0, d2.GetProperty("errors").GetInt32());
    }
}
