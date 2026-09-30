// ServerAffectedSelectionSharedSetupTests — #5035: under TestIsolation = Codeunit a test's recording
// holds only what it ran, not the setup an earlier test of its codeunit ran for it (the isInitialized
// pattern), so selecting one test selects its whole codeunit. Mechanism:
// docs/server-mode.md#affectedonly-and-test-isolation.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerAffectedSelectionSharedSetupTests
{
    private const string Setup = """
        table 61830 "Shared Setup SX"
        {
            fields
            {
                field(1; PK; Code[10]) { }
                field(2; Value; Integer) { }
            }
            keys { key(PK; PK) { Clustered = true; } }
        }
        """;

    internal static string Helper(string probe = "") => """
        codeunit 61831 "Shared Setup Helper SX"
        {
            procedure CreateSetup()
            var
                S: Record "Shared Setup SX";
            begin
        """ + probe + """

                S.PK := 'A';
                S.Value := 42;
                S.Insert();
            end;
        }
        """;

    internal const string Probe = "        Error('PROBE-INIT');";

    // Only the first test to call Initialize() runs the helper; the others read what it wrote.
    private const string Tests = """
        codeunit 61832 "Shared Setup Tests SX"
        {
            Subtype = Test;

            var
                IsInitialized: Boolean;

            local procedure Initialize()
            var
                H: Codeunit "Shared Setup Helper SX";
            begin
                if IsInitialized then
                    exit;
                H.CreateSetup();
                IsInitialized := true;
            end;

            local procedure ReadSetup(): Integer
            var
                S: Record "Shared Setup SX";
            begin
                S.Get('A');
                exit(S.Value);
            end;

            [Test]
            procedure ReadsSetupOne()
            begin
                Initialize();
                if ReadSetup() <> 42 then
                    Error('ReadsSetupOne failed');
            end;

            [Test]
            procedure ReadsSetupTwo()
            begin
                Initialize();
                if ReadSetup() + 1 <> 43 then
                    Error('ReadsSetupTwo failed');
            end;

            [Test]
            procedure ReadsSetupThree()
            begin
                Initialize();
                if ReadSetup() * 2 <> 84 then
                    Error('ReadsSetupThree failed');
            end;

            [Test]
            procedure NeverInitializes()
            begin
                if 3 + 3 <> 6 then
                    Error('NeverInitializes failed');
            end;
        }
        """;

    // A second codeunit the helper edit cannot reach: stays skipped unless isolation is Disabled.
    private const string Control = """
        codeunit 61833 "Shared Setup Control SX"
        {
            Subtype = Test;

            [Test]
            procedure Independent()
            begin
                if 2 + 2 <> 4 then
                    Error('Independent failed');
            end;
        }
        """;

    private static readonly string[] Readers = { "ReadsSetupOne", "ReadsSetupThree", "ReadsSetupTwo" };
    private static readonly string[] WholeTestCodeunit = { "NeverInitializes", "ReadsSetupOne", "ReadsSetupThree", "ReadsSetupTwo" };

    internal static string Bundle(string prefix, string appIdSuffix)
    {
        var dir = TestScratch.Dir(prefix);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "c5035000-0000-4a11-9111-{{appIdSuffix}}",
          "name": "Shared Setup SX {{appIdSuffix}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 61830, "to": 61849 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Setup.Table.al"), Setup);
        File.WriteAllText(Path.Combine(dir, "Helper.Codeunit.al"), Helper());
        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), Tests);
        File.WriteAllText(Path.Combine(dir, "Control.Codeunit.al"), Control);
        return dir;
    }

    private sealed record Observed(Dictionary<string, (string Status, string Line)> Tests, bool ForcedFull,
        string? Reason, string Raw)
    {
        public string[] Ran => Tests.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
    }

    private static async Task<Observed> Send(CliServer server, string bundle, string? isolation = null)
    {
        var request = new Dictionary<string, object>
        {
            ["command"] = "runTests",
            ["sourcePaths"] = new[] { bundle },
            ["packagePaths"] = Array.Empty<string>(),
            ["affectedOnly"] = true,
        };
        if (isolation != null) request["testIsolation"] = isolation;
        var lines = await server.SendRequestStreamingAsync(JsonSerializer.Serialize(request), TimeSpan.FromSeconds(180));
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        var raw = string.Join(" | ", lines) + "\n--- stderr ---\n" + server.StdErr;
        Assert.True(summary.TryGetProperty("selection", out var selection), raw);
        var tests = events.ToDictionary(
            e => e.GetProperty("name").GetString()!.Split('.').Last(),
            e => (e.GetProperty("status").GetString()!, e.GetRawText()), StringComparer.Ordinal);
        return new Observed(tests, selection.GetProperty("forcedFull").GetBoolean(),
            selection.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null, raw);
    }

    private static void AssertRan(Observed o, string step, params string[] expected)
        => Assert.True(expected.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(o.Ran),
            $"{step}: ran [{string.Join(", ", o.Ran)}], expected [{string.Join(", ", expected)}]:\n{o.Raw}");

    private static void AssertReadersFailWithProbe(Observed o, string step)
    {
        foreach (var t in Readers)
        {
            Assert.True(o.Tests[t].Status == "fail", $"{step}: {t} must fail:\n{o.Raw}");
            Assert.Contains("PROBE-INIT", o.Tests[t].Line, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The issue's acceptance: an edit to setup only the first test ran selects every test of that
    /// codeunit, and all three readers fail with the probe. The untouched codeunit stays skipped.
    /// </summary>
    [SkippableFact]
    public async Task CodeunitIsolation_SetupEditSelectsTheWholeCodeunit_NotOnlyTheTestThatRanIt()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-shared-setup", "000000000001");
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        var baseline = await Send(server, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        AssertRan(baseline, "baseline", "Independent", "NeverInitializes", "ReadsSetupOne", "ReadsSetupThree", "ReadsSetupTwo");
        Assert.True(baseline.Tests.Values.All(t => t.Status == "pass"), baseline.Raw);

        File.WriteAllText(Path.Combine(bundle, "Helper.Codeunit.al"), Helper(Probe));
        var edited = await Send(server, bundle);
        Assert.False(edited.ForcedFull, edited.Raw);
        AssertRan(edited, "edit", WholeTestCodeunit);
        AssertReadersFailWithProbe(edited, "edit");
        Assert.Equal("pass", edited.Tests["NeverInitializes"].Status);
    }

    /// <summary>The same through the persisted baseline: the next server's first request selects the codeunit.</summary>
    [SkippableFact]
    public async Task CodeunitIsolation_AcrossARestart_SelectsTheWholeCodeunit()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-shared-setup-restart", "000000000002");
        var cache = TestScratch.Dir("al-runner-server-affected-shared-setup-restart-cache");

        await using (var recorder = await CliServer.StartAsync(new[] { "--cache", cache }))
            Assert.True((await Send(recorder, bundle)).ForcedFull);

        File.WriteAllText(Path.Combine(bundle, "Helper.Codeunit.al"), Helper(Probe));
        await using var restarted = await CliServer.StartAsync(new[] { "--cache", cache });
        var edited = await Send(restarted, bundle);
        Assert.False(edited.ForcedFull, $"the persisted baseline must let the first request narrow: {edited.Raw}");
        AssertRan(edited, "restarted", WholeTestCodeunit);
        AssertReadersFailWithProbe(edited, "restarted");
    }

    /// <summary>
    /// Under Test isolation the database resets per test but the codeunit instance does not (#4826:
    /// BC's TestIsolation = Function, measured by corpus PR #517, Windows nightly run 36727084058),
    /// so IsInitialized carries across tests exactly as under Codeunit isolation and the selection
    /// widens to the whole codeunit. Switching the isolation between requests cannot reuse that
    /// recording and runs everything.
    /// </summary>
    [SkippableFact]
    public async Task TestIsolation_SetupEditSelectsTheWholeCodeunit_AndAnIsolationChangeForcesAFullRun()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-shared-setup-testiso", "000000000003");
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        Assert.True((await Send(server, bundle, "test")).ForcedFull);

        File.WriteAllText(Path.Combine(bundle, "Helper.Codeunit.al"), Helper(Probe));
        var edited = await Send(server, bundle, "test");
        Assert.False(edited.ForcedFull, edited.Raw);
        AssertRan(edited, "test isolation", WholeTestCodeunit);
        AssertReadersFailWithProbe(edited, "test isolation");
        Assert.Equal("pass", edited.Tests["NeverInitializes"].Status);

        File.WriteAllText(Path.Combine(bundle, "Helper.Codeunit.al"), Helper());
        var switched = await Send(server, bundle, "codeunit");
        Assert.True(switched.ForcedFull, switched.Raw);
        Assert.Contains("test isolation", switched.Reason, StringComparison.Ordinal);
        Assert.Equal(5, switched.Tests.Count);
    }

    /// <summary>Under Disabled isolation nothing is reset between codeunits, so any selection is the whole bundle.</summary>
    [SkippableFact]
    public async Task DisabledIsolation_AnySelectionSelectsTheWholeBundle()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-shared-setup-disabled", "000000000004");
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        Assert.True((await Send(server, bundle, "disabled")).ForcedFull);

        File.WriteAllText(Path.Combine(bundle, "Helper.Codeunit.al"), Helper(Probe));
        var edited = await Send(server, bundle, "disabled");
        Assert.False(edited.ForcedFull, edited.Raw);
        AssertRan(edited, "disabled isolation", "Independent", "NeverInitializes", "ReadsSetupOne", "ReadsSetupThree", "ReadsSetupTwo");
        AssertReadersFailWithProbe(edited, "disabled isolation");
    }
}
