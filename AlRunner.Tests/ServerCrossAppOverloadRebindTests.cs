// ServerCrossAppOverloadRebindTests — issue #2603, the CROSS-APP half of the silent overload hazard #2548
// fixed within one app.
//
// #2548 / #2561 stopped the incremental fast path shipping a caller bound to the old overload
// when caller and callee live in the SAME module: the module's own delta detects that a changed
// object gained a member under a name it already declared, and falls back to a full compile.
//
// That fix cannot reach the cross-app case, and the reason is structural. Each app group gets its
// OWN RadBaseline and its own TryEmitIncremental call. When app A's callee is edited and app B's
// caller is not, B's file hashes are all identical, so B takes the "every file hashes identical to
// the last cycle — genuinely zero work: replay the last cycle's result verbatim" short-circuit and
// never enters the delta path at all. B's cached C# therefore still bakes the member id A's
// PREVIOUS surface resolved to.
//
// As with the same-app case, the failure is silent exactly when the old id survives:
//   * `MethodSymbol.CalculateMethodIdForNewVersions` is method-local, so adding `Which(Integer)`
//     beside `Which(Decimal)` leaves the Decimal overload's id and its `case` label untouched.
//   * What moves is the id the CALLER bakes — an Integer argument used to widen to the Decimal
//     overload and now binds to the Integer one.
//   * B dispatches a member that still exists and gets the previous overload's answer. No
//     NavNCLMissingMethodException, no diagnostic, no log line — the run is green and wrong.
//
// This is the shape the whole cross-app reference graph (#2571) exists to make fixable. The test
// is written to state the requirement, not the mechanism: a warm --server request must answer the
// same as a from-scratch run of the same sources. Any fix that achieves that satisfies it.
//
// Request 1's expectation is deliberately the POST-edit one, so the test app's own source can stay
// byte-identical across both requests — that is the whole point, since a modified caller would get
// a fresh call-site id anyway and there would be nothing to measure.
//
// Credit: the hazard and the compiler contract underneath it were found and pinned by Mikkel Mansa
// Vilhelmsen (vhn) in his AL Runner fork (RadDeltaWatchTests
// .Watch_AddingAnOverloadInOneApp_RebindsItsCrossAppCaller).
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerCrossAppOverloadRebindTests
{
    private const string AppId = "a1b2c3d4-6051-4a11-9111-111111111111";
    private const string TestAppId = "a1b2c3d4-6052-4a11-9222-222222222222";

    /// <summary>The dependency app, with one Decimal overload of `Which`.</summary>
    private const string LibBefore = """
        codeunit 60510 "XApp Ovl Lib"
        {
            procedure Which(Seed: Decimal): Integer
            begin
                exit(1);
            end;
        }
        """;

    /// <summary>The edit: a second overload of the SAME name taking Integer. Nothing else moves,
    /// and no existing member's id moves either.</summary>
    private const string LibAfter = """
        codeunit 60510 "XApp Ovl Lib"
        {
            procedure Which(Seed: Decimal): Integer
            begin
                exit(1);
            end;

            procedure Which(Seed: Integer): Integer
            begin
                exit(2);
            end;
        }
        """;

    private static string MakeAppBundle(string root, string lib)
    {
        var dir = Path.Combine(root, "app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{AppId}}",
          "name": "XApp Ovl App",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60510, "to": 60519 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Lib.al"), lib);
        return dir;
    }

    /// <summary>
    /// The consuming app. Byte-identical across both requests, and it passes an INTEGER — so what
    /// it binds to is decided entirely by which overloads the dependency declares.
    /// </summary>
    private static string MakeTestAppBundle(string root)
    {
        var dir = Path.Combine(root, "test-app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{TestAppId}}",
          "name": "XApp Ovl Test App",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{AppId}}", "name": "XApp Ovl App",
              "publisher": "AL Runner", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60520, "to": 60529 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.al"), """
        codeunit 60520 "XApp Ovl Tests"
        {
            Subtype = Test;

            [Test]
            procedure BindsTheIntegerOverload()
            var
                Lib: Codeunit "XApp Ovl Lib";
                Seed: Integer;
                Bound: Integer;
            begin
                Seed := 7;
                Bound := Lib.Which(Seed);
                // Deliberately the POST-edit expectation. Before the dependency gains an Integer
                // overload the argument widens to the Decimal one and this reports 1; after, a
                // correctly rebound caller reports 2. The value is in the message so a wrong
                // answer names itself instead of only failing.
                if Bound <> 2 then
                    Error('BOUND-TO=' + Format(Bound));
            end;
        }
        """);
        return dir;
    }

    private static string RunTestsRequest(string appDir, string testAppDir)
        => JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { appDir, testAppDir },
            packagePaths = Array.Empty<string>(),
            // Without these the server takes the ordinary full Emit() on every request
            // (RunBundleForServer only calls TryEmitIncremental when useIncrementalChangeModel is
            // set, which is what affectedOnly turns on), and the incremental path this test is
            // about is never entered at all.
            affectedOnly = true,
            perTestCoverage = true,
        });

    /// <summary>
    /// A warm <c>--server</c> request whose DEPENDENCY app gained an overload must answer the same
    /// as a from-scratch run of the same sources.
    /// </summary>
    [SkippableFact]
    public async Task WarmRequest_AfterADependencyGainsAnOverload_RebindsTheConsumingApp()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(Path.GetTempPath(), "al-runner-xapp-overload", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var appDir = MakeAppBundle(root, LibBefore);
        var testAppDir = MakeTestAppBundle(root);

        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        // Request 1 — pre-edit. The Integer argument widens to Which(Decimal), so the test reports
        // BOUND-TO=1 and fails. Asserted, not tolerated: it is the measurement that the fixture
        // really does bind the Decimal overload to begin with, so request 2's pass cannot be
        // "it was always 2".
        var lines1 = await server.SendRequestStreamingAsync(RunTestsRequest(appDir, testAppDir));
        var (events1, _) = ProtocolV2Streaming.Split(lines1);
        Assert.Single(events1);
        Assert.Equal("fail", events1[0].GetProperty("status").GetString());
        Assert.Contains("BOUND-TO=1", string.Join(" | ", lines1), StringComparison.Ordinal);

        // Edit ONLY the dependency app. The test app's files are not touched.
        File.WriteAllText(Path.Combine(appDir, "Lib.al"), LibAfter);

        // Request 2 — same warm process. A from-scratch run of these sources binds
        // Which(Integer) and returns 2.
        var lines2 = await server.SendRequestStreamingAsync(RunTestsRequest(appDir, testAppDir));
        var (events2, _) = ProtocolV2Streaming.Split(lines2);
        Assert.Single(events2);

        var status2 = events2[0].GetProperty("status").GetString();
        Assert.True(status2 == "pass",
            "the consuming app was not rebound after its dependency gained an overload. Its own "
            + "files did not change, so its module took the \"replay the last cycle's result "
            + "verbatim\" short-circuit and never entered the delta path — leaving its cached C# "
            + "dispatching the member id that Which(Decimal) resolved to. That member still exists "
            + "in the re-emitted dependency, so the call succeeded and returned the PREVIOUS "
            + "overload's answer with no exception and no diagnostic. Expected BOUND-TO=2 "
            + $"(what a cold run of these exact sources gives); got: {string.Join(" | ", lines2)}");
    }
}
