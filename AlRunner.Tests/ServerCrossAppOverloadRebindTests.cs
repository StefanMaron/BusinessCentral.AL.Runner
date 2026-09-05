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
    // One id set per ordering case, so the two cases cannot share on-disk or in-process state.
    private static string AppId(int c) => $"a1b2c3d4-605{c}-4a11-9111-111111111111";
    private static string TestAppId(int c) => $"a1b2c3d4-606{c}-4a11-9222-222222222222";
    private static int LibId(int c) => 60510 + c * 20;
    private static int TestsId(int c) => 60520 + c * 20;

    /// <summary>The dependency app, with one Decimal overload of `Which`.</summary>
    private static string LibBefore(int c) => $$"""
        codeunit {{LibId(c)}} "XApp Ovl Lib {{c}}"
        {
            procedure Which(Seed: Decimal): Integer
            begin
                exit(1);
            end;
        }
        """;

    /// <summary>The edit: a second overload of the SAME name taking Integer. Nothing else moves,
    /// and no existing member's id moves either.</summary>
    private static string LibAfter(int c) => $$"""
        codeunit {{LibId(c)}} "XApp Ovl Lib {{c}}"
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

    private static string MakeAppBundle(string root, int c, string lib)
    {
        var dir = Path.Combine(root, "app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{AppId(c)}}",
          "name": "XApp Ovl App {{c}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{LibId(c)}}, "to": {{LibId(c) + 9}} } ],
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
    private static string MakeTestAppBundle(string root, int c)
    {
        var dir = Path.Combine(root, "test-app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{TestAppId(c)}}",
          "name": "XApp Ovl Test App {{c}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{AppId(c)}}", "name": "XApp Ovl App {{c}}",
              "publisher": "AL Runner", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{TestsId(c)}}, "to": {{TestsId(c) + 9}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.al"), $$"""
        codeunit {{TestsId(c)}} "XApp Ovl Tests {{c}}"
        {
            Subtype = Test;

            [Test]
            procedure BindsTheIntegerOverload()
            var
                Lib: Codeunit "XApp Ovl Lib {{c}}";
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

    private static string RunTestsRequest(string[] sourcePaths)
        => JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths,
            packagePaths = Array.Empty<string>(),
            // Without these the server takes the ordinary full Emit() on every request
            // (RunBundleForServer only calls TryEmitIncremental when useIncrementalChangeModel is
            // set, which is what affectedOnly turns on), and the incremental path this test is
            // about is never entered at all.
            affectedOnly = true,
            perTestCoverage = true,
        });

    /// <summary>
    /// A warm <c>--server</c> request whose DEPENDENCY app gained an overload must never answer
    /// with the overload the consuming app used to bind to.
    ///
    /// <para><b>The two orders have different bars, and the difference is measured, not assumed.</b></para>
    ///
    /// <para><c>dependencyFirst: true</c> — the order the README documents. Must PASS: the request
    /// answers exactly as a from-scratch run of the same sources does.</para>
    ///
    /// <para><c>dependencyFirst: false</c> — an order nothing documents but the runner accepts.
    /// Before this change it returned <c>BOUND-TO=1</c>: a green-looking, silently wrong answer,
    /// because the consuming bundle is processed before the fallback signal the forward
    /// propagation reads even exists. <c>ChangedLaterDependencyBundles</c> now forces that bundle
    /// to compile in full, and what remains is a LOUD failure —
    /// <c>NavNCLCompilationException: Function ID … was called. The object with ID … does not have
    /// a member with that ID</c> — because in this order the dependency's runtime assembly is not
    /// reloaded until after the consuming bundle's tests have already run. That residual is #2614; the bar asserted here is the one
    /// <c>.claude/rules/loud-failures.md</c> sets: pass, or fail in a way somebody can see.
    /// <b>Never <c>BOUND-TO=1</c>.</b></para>
    /// </summary>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WarmRequest_AfterADependencyGainsAnOverload_NeverAnswersWithTheOldOverload(bool dependencyFirst)
    {
        TestArtifacts.SkipIfMissing();

        var c = dependencyFirst ? 1 : 2;
        var root = TestScratch.Dir("al-runner-xapp-overload");
        Directory.CreateDirectory(root);
        var appDir = MakeAppBundle(root, c, LibBefore(c));
        var testAppDir = MakeTestAppBundle(root, c);
        var sourcePaths = dependencyFirst
            ? new[] { appDir, testAppDir }
            : new[] { testAppDir, appDir };

        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        // Request 1 — pre-edit. The Integer argument widens to Which(Decimal), so the test reports
        // BOUND-TO=1 and fails. Asserted, not tolerated: it is the measurement that the fixture
        // really does bind the Decimal overload to begin with, so request 2 cannot pass by the
        // answer having been 2 all along.
        var lines1 = await server.SendRequestStreamingAsync(RunTestsRequest(sourcePaths));
        var (events1, _) = ProtocolV2Streaming.Split(lines1);
        Assert.Single(events1);
        Assert.Equal("fail", events1[0].GetProperty("status").GetString());
        Assert.Contains("BOUND-TO=1", string.Join(" | ", lines1), StringComparison.Ordinal);

        // Edit ONLY the dependency app. The test app's files are not touched — which is the whole
        // point, since a modified caller would get a fresh call-site id anyway.
        File.WriteAllText(Path.Combine(appDir, "Lib.al"), LibAfter(c));

        // Request 2 — same warm process. A from-scratch run of these sources binds
        // Which(Integer) and returns 2.
        var lines2 = await server.SendRequestStreamingAsync(RunTestsRequest(sourcePaths));
        var (events2, _) = ProtocolV2Streaming.Split(lines2);
        Assert.Single(events2);
        var joined2 = string.Join(" | ", lines2);
        var status2 = events2[0].GetProperty("status").GetString();

        // The floor, asserted for BOTH orders: the previous overload's answer must never come back
        // reported as a result. This is the silent failure the whole change exists to remove.
        Assert.False(joined2.Contains("BOUND-TO=1", StringComparison.Ordinal),
            "the consuming app returned the PREVIOUS overload's answer after its dependency gained "
            + $"an overload (dependencyFirst: {dependencyFirst}). Its own files did not change, so "
            + "its module took the \"replay the last cycle's result verbatim\" short-circuit and "
            + "never entered the delta path — leaving its cached C# dispatching the member id that "
            + "Which(Decimal) resolved to. That member still exists in the re-emitted dependency, so "
            + "the call succeeded with no exception and no diagnostic. Got: " + joined2);

        if (!dependencyFirst)
        {
            // The undocumented order: loud is the bar. Passing is fine too — assert only that the
            // answer is not silently wrong, which the check above has already established.
            return;
        }

        Assert.True(status2 == "pass",
            "the documented dependency-first order must answer exactly as a cold run of these "
            + $"sources does (BOUND-TO=2). Got: {joined2}");
    }
}
