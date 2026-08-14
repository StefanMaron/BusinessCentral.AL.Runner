using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #1892 — the SAME defect #1683 fixed for the CLI bundle loop
/// (<see cref="CrossBundleModuleIdentityDedupTests"/>), reproduced through
/// <c>--server</c>'s <c>runTests</c> multi-<c>sourcePaths</c> path instead of the CLI's
/// multiple positional bundle args.
///
/// A <c>runTests</c> request whose <c>sourcePaths</c> name an app bundle AND a
/// sibling test bundle that depends on it used to compile the app bundle TWICE:
/// once via <see cref="Program"/>'s per-bundle server compile (its own module,
/// <c>V2_&lt;dir&gt;</c>) and once more via <c>DependencyLoader.LoadAll</c>
/// resolving the SAME AppId as the test bundle's dependency (a
/// Tier-3-compiled <c>Dep_...</c> module) — because <c>RunBundleForServer</c>
/// never registered its own freshly-compiled module into
/// <c>DependencyLoader</c>'s cross-bundle cache the way the CLI's per-AppGroup
/// loop does. Two live modules for one AL app identity means the event-subscriber
/// registry pairs a subscriber <c>MethodInfo</c> discovered from one module's
/// <c>Type</c> with a <c>subscriberInstance</c> BC's own dispatcher materialized
/// from the OTHER module's <c>Type</c>, and <c>RuntimeMethodInfo.Invoke</c> throws
/// <c>TargetException: Object does not match target type</c> inside
/// <c>NavEventScope.CallEventSubscriberInternalAsync</c> → <c>ValidateInvokeTarget</c>
/// — exactly the CLI's own #1683 signature, but only over <c>--server</c>.
///
/// RED (pre-fix): the request aborts — the app bundle's OWN compile is never
/// registered, so the test bundle's dependency resolution re-compiles it into a
/// second module and the install trigger's <c>Modify(true)</c> throws
/// <c>TargetException</c> the instant it dispatches to the mismatched subscriber.
/// GREEN (post-fix): <c>RunBundleForServer</c> checks
/// <c>DependencyLoader.TryGetByAppId</c> before compiling and calls
/// <c>DependencyLoader.RegisterLoaded</c> after — one loaded module per AL app
/// identity, same as the CLI — so the install trigger's subscriber fires cleanly
/// and both tests in the dependent bundle pass.
///
/// Spawns the real runner in --server mode; needs the BC artifact cache. Skips
/// (no-op) when absent.
/// </summary>
public class ServerCrossBundleModuleIdentityDedupTests
{
    private static (string appDir, string testDir) MakeAppTestPair()
    {
        var root = Path.Combine(Path.GetTempPath(), "al-runner-server-xbundle-dedup", Guid.NewGuid().ToString("N"));
        var appDir = Path.Combine(root, "app");
        var testDir = Path.Combine(root, "tests");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(testDir);

        const string appId = "c3d4e5f6-1892-4a1b-9c3d-000000000001";

        File.WriteAllText(Path.Combine(appDir, "app.json"), $$"""
        {
          "id": "{{appId}}",
          "name": "SX1892 Repro App",
          "publisher": "Repro1892",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 61990, "to": 61999 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(appDir, "SX1892App.al"), """
        table 61990 "SX1892 Row"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; Description; Text[50]) { }
            }
            keys { key(PK; "No.") { Clustered = true; } }
        }

        table 61991 "SX1892 Marker"
        {
            DataClassification = SystemMetadata;
            fields { field(1; "No."; Code[20]) { } }
            keys { key(PK; "No.") { Clustered = true; } }
        }

        codeunit 61992 "SX1892 Subscriber"
        {
            [EventSubscriber(ObjectType::Table, Database::"SX1892 Row", 'OnAfterModifyEvent', '', false, false)]
            local procedure OnAfterModifyRow(var Rec: Record "SX1892 Row"; var xRec: Record "SX1892 Row"; RunTrigger: Boolean)
            var
                Marker: Record "SX1892 Marker";
            begin
                if Marker.Get('FIRED') then
                    exit;
                Marker."No." := 'FIRED';
                Marker.Insert();
            end;
        }

        codeunit 61993 "SX1892 Install"
        {
            Subtype = Install;

            trigger OnInstallAppPerCompany()
            var
                Row: Record "SX1892 Row";
            begin
                Row."No." := 'SEED';
                Row.Insert();
                Row.Description := 'seeded';
                Row.Modify(true);
            end;
        }
        """);

        File.WriteAllText(Path.Combine(testDir, "app.json"), $$"""
        {
          "id": "d4e5f6a7-1892-4a1b-9c3d-000000000002",
          "name": "SX1892 Repro Tests",
          "publisher": "Repro1892",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{appId}}", "name": "SX1892 Repro App", "publisher": "Repro1892", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 62050, "to": 62059 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(testDir, "SX1892Tests.al"), """
        codeunit 62050 "SX1892 Test"
        {
            Subtype = Test;

            [Test]
            procedure InstallSeedRowExists()
            var
                Row: Record "SX1892 Row";
            begin
                Row.Get('SEED');
                if Row.Description <> 'seeded' then
                    Error('install trigger did not run the Modify: Description=%1', Row.Description);
            end;

            [Test]
            procedure SubscriberFiredDuringInstall()
            var
                Marker: Record "SX1892 Marker";
            begin
                if not Marker.Get('FIRED') then
                    Error('OnAfterModifyEvent subscriber did not fire during OnInstallAppPerCompany');
            end;
        }
        """);

        return (appDir, testDir);
    }

    [SkippableFact]
    public async Task RunTests_AppThenDependentTestBundle_InstallTriggerSubscriberFiresCleanly_NoSecondModule()
    {
        TestArtifacts.SkipIfMissing();

        var (appDir, testDir) = MakeAppTestPair();
        var cacheDir = Path.Combine(Path.GetTempPath(), "al-runner-server-xbundle-dedup-cache", Guid.NewGuid().ToString("N"));
        await using var server = await CliServer.StartAsync(new[] { "--cache", cacheDir });

        // Same order as the issue's Leg 2 (`sourcePaths [app, tests]`) — the exact
        // shape that crashed under --server while the identical pair passed via the
        // CLI (`al-runner app tests`).
        var req = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { appDir, testDir },
            packagePaths = Array.Empty<string>(),
        });
        var lines = await server.SendRequestStreamingAsync(req, TimeSpan.FromSeconds(180));
        var (events, d) = ProtocolV2Streaming.Split(lines);

        // The exact defect: two loaded modules for one AL app identity produced a
        // subscriber MethodInfo/instance mismatch inside the install trigger's
        // Modify(true) dispatch. Never silently pass a run that hit this — this
        // assertion is the whole point of the RED->GREEN cycle, and matches what
        // CrossBundleModuleIdentityDedupTests pins for the CLI path.
        var allOutput = string.Join(" | ", lines) + " " + server.StdErr;
        Assert.DoesNotContain("TargetException", allOutput);
        Assert.DoesNotContain("Object does not match target type", allOutput);

        // Both tests in the dependent bundle must actually have run and passed —
        // not merely "no crash". The app bundle itself declares zero [Test]
        // codeunits, so total == 2 proves the test bundle's dependency on the app
        // resolved to a SINGLE working module (install trigger ran AND its
        // subscriber fired), matching the CLI's 2/2 result for the identical pair.
        Assert.Equal(2, d.GetProperty("total").GetInt32());
        Assert.Equal(2, d.GetProperty("passed").GetInt32());
        Assert.Equal(0, d.GetProperty("failed").GetInt32());
        Assert.Equal(0, d.GetProperty("errors").GetInt32());
        Assert.Equal(0, d.GetProperty("exitCode").GetInt32());
        Assert.Equal(2, events.Count);
    }
}
