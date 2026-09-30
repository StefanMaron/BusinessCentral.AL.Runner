// ServerAffectedSelectionEnteredScopeTests — #5011: under affectedOnly, a test that used an object
// or entered a procedure without executing any statement of it is selected when that object or
// procedure changes. Mechanism: docs/server-mode.md#affectedonly-and-entered-scopes.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerAffectedSelectionEnteredScopeTests
{
    private static string EmptyCodeunit(string body = "")
        => "codeunit 60692 \"EnterSel Cu SX\"\n{\n    procedure P()\n    begin\n" + body + "    end;\n\n"
           + "    procedure Q(): Integer\n    begin\n        exit(1);\n    end;\n}\n";

    private static string RunCodeunit(string body = "")
        => "codeunit 60693 \"EnterSel Run SX\"\n{\n    trigger OnRun()\n    begin\n" + body + "    end;\n}\n";

    // No OnRun at all: Codeunit.Run builds an instance and enters nothing.
    private static string NoTriggerCodeunit(string members = "")
        => "codeunit 60694 \"EnterSel NoTrig SX\"\n{\n" + members + "}\n";

    // Held by a test codeunit's global, so no test builds it: the instance outlives each test.
    private static string GlobalCodeunit(string members = "")
        => "codeunit 60698 \"EnterSel Global SX\"\n{\n" + members + "}\n";

    private const string ProbeGlobal = "    trigger OnRun()\n    begin\n        Error('PROBE-GLOBAL');\n    end;\n";

    private const string GlobalTests = """
        codeunit 60699 "EnterSel Global Tests SX"
        {
            Subtype = Test;

            var
                G: Codeunit "EnterSel Global SX";

            [Test]
            procedure RunsGlobalA()
            begin
                G.Run();
            end;

            [Test]
            procedure RunsGlobalB()
            begin
                G.Run();
            end;
        }
        """;

    private const string Table = """
        table 60695 "EnterSel Tab SX"
        {
            fields { field(1; PK; Integer) { } }
            keys { key(PK; PK) { Clustered = true; } }
        }
        """;

    private static string Page(string triggers = "")
        => "page 60696 \"EnterSel Page SX\"\n{\n    SourceTable = \"EnterSel Tab SX\";\n"
           + "    layout { area(Content) { field(PK; Rec.PK) { } } }\n" + triggers + "}\n";

    private const string ProbeEmpty = "        Error('PROBE-EMPTY');\n";
    private const string ProbeRun = "        Error('PROBE-RUN');\n";
    private const string ProbeNoTrig = "    trigger OnRun()\n    begin\n        Error('PROBE-NOTRIG');\n    end;\n";
    private const string ProbePage = "    trigger OnOpenPage()\n    begin\n        Error('PROBE-PAGE');\n    end;\n";

    private const string Tests = """
        codeunit 60697 "EnterSel Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure CallsEmpty()
            var
                C: Codeunit "EnterSel Cu SX";
            begin
                C.P();
            end;

            [Test]
            procedure CallsQ()
            var
                C: Codeunit "EnterSel Cu SX";
            begin
                if C.Q() <> 1 then
                    Error('CallsQ failed');
            end;

            [Test]
            procedure RunsById()
            begin
                Codeunit.Run(60693);
            end;

            [Test]
            procedure RunsNoTriggerById()
            begin
                Codeunit.Run(60694);
            end;

            [Test]
            procedure OpensPage()
            var
                TP: TestPage "EnterSel Page SX";
            begin
                TP.OpenView();
                TP.Close();
            end;

            [Test]
            procedure Unrelated()
            begin
                if 1 + 1 <> 2 then
                    Error('Unrelated failed');
            end;
        }
        """;

    private static readonly string[] All =
        { "CallsEmpty", "CallsQ", "OpensPage", "RunsById", "RunsGlobalA", "RunsGlobalB", "RunsNoTriggerById", "Unrelated" };

    private static string Bundle(string prefix, string appIdSuffix)
    {
        var dir = TestScratch.Dir(prefix);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "c5011000-0000-4a11-9111-{{appIdSuffix}}",
          "name": "Entered Scope Selection SX {{appIdSuffix}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60690, "to": 60699 } ],
          "runtime": "14.0"
        }
        """);
        Write(dir, "Cu.Codeunit.al", EmptyCodeunit());
        Write(dir, "Run.Codeunit.al", RunCodeunit());
        Write(dir, "NoTrig.Codeunit.al", NoTriggerCodeunit());
        Write(dir, "Tab.Table.al", Table);
        Write(dir, "Page.Page.al", Page());
        Write(dir, "Tests.Codeunit.al", Tests);
        Write(dir, "Global.Codeunit.al", GlobalCodeunit());
        Write(dir, "GlobalTests.Codeunit.al", GlobalTests);
        return dir;
    }

    private static void Write(string bundle, string file, string source)
        => File.WriteAllText(Path.Combine(bundle, file), source);

    private sealed record Observed(string[] Ran, Dictionary<string, string> Status,
        Dictionary<string, string> Line, bool ForcedFull, string? Reason, string Raw);

    private static async Task<Observed> Send(CliServer server, string bundle)
    {
        var request = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["command"] = "runTests",
            ["sourcePaths"] = new[] { bundle },
            ["packagePaths"] = Array.Empty<string>(),
            ["affectedOnly"] = true,
        });
        var lines = await server.SendRequestStreamingAsync(request, TimeSpan.FromSeconds(180));
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        var raw = string.Join(" | ", lines);
        Assert.True(summary.TryGetProperty("selection", out var selection), raw);
        var status = events.ToDictionary(
            e => e.GetProperty("name").GetString()!.Split('.').Last(), e => e.GetProperty("status").GetString()!,
            StringComparer.Ordinal);
        var line = events.ToDictionary(
            e => e.GetProperty("name").GetString()!.Split('.').Last(), e => e.GetRawText(), StringComparer.Ordinal);
        return new Observed(status.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(), status, line,
            selection.GetProperty("forcedFull").GetBoolean(),
            selection.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
            raw);
    }

    private static async Task<Observed> SendFresh(string cache, string bundle)
    {
        await using var server = await CliServer.StartAsync(new[] { "--cache", cache });
        return await Send(server, bundle);
    }

    // Narrowed to exactly `test`, which fails on `probe`.
    private static void AssertSelectedAndFails(Observed o, string test, string probe)
    {
        Assert.False(o.ForcedFull, o.Raw);
        Assert.Equal(new[] { test }, o.Ran);
        Assert.True(o.Status[test] == "fail", o.Raw);
        Assert.Contains(probe, o.Line[test], StringComparison.Ordinal);
    }

    private static void AssertSelectedAndPass(Observed o, params string[] tests)
    {
        Assert.False(o.ForcedFull, o.Raw);
        Assert.Equal(tests, o.Ran);
        Assert.All(tests, t => Assert.True(o.Status[t] == "pass", o.Raw));
    }

    [SkippableFact]
    public async Task EmptyScopeOrObjectGainingCode_SelectsTheTestsThatEnteredIt()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-entersel", "000000000001");
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        var baseline = await Send(server, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.Equal(All, baseline.Ran);
        Assert.All(baseline.Status.Values, s => Assert.Equal("pass", s));

        var unchanged = await Send(server, bundle);
        Assert.False(unchanged.ForcedFull, unchanged.Raw);
        Assert.Empty(unchanged.Ran);

        // The issue's case: an empty procedure gains a statement. CallsQ ran Q of the same
        // codeunit, and the change is narrowed to P, so it stays skipped.
        Write(bundle, "Cu.Codeunit.al", EmptyCodeunit(ProbeEmpty));
        AssertSelectedAndFails(await Send(server, bundle), "CallsEmpty", "PROBE-EMPTY");
        // Removing lines narrows to no procedure, so the whole codeunit's callers run.
        Write(bundle, "Cu.Codeunit.al", EmptyCodeunit());
        AssertSelectedAndPass(await Send(server, bundle), "CallsEmpty", "CallsQ");

        // Reached only by id, through an empty trigger.
        Write(bundle, "Run.Codeunit.al", RunCodeunit(ProbeRun));
        AssertSelectedAndFails(await Send(server, bundle), "RunsById", "PROBE-RUN");
        Write(bundle, "Run.Codeunit.al", RunCodeunit());
        AssertSelectedAndPass(await Send(server, bundle), "RunsById");

        // Reached only by id, with no trigger to enter: only the instance was built.
        Write(bundle, "NoTrig.Codeunit.al", NoTriggerCodeunit(ProbeNoTrig));
        AssertSelectedAndFails(await Send(server, bundle), "RunsNoTriggerById", "PROBE-NOTRIG");
        Write(bundle, "NoTrig.Codeunit.al", NoTriggerCodeunit());
        AssertSelectedAndPass(await Send(server, bundle), "RunsNoTriggerById");

        // A page without triggers gains one: the test that opened it built its instance.
        Write(bundle, "Page.Page.al", Page(ProbePage));
        AssertSelectedAndFails(await Send(server, bundle), "OpensPage", "PROBE-PAGE");

        // An instance no one test built: which tests use it is not recorded, so everything runs.
        Write(bundle, "Global.Codeunit.al", GlobalCodeunit(ProbeGlobal));
        var global = await Send(server, bundle);
        Assert.True(global.ForcedFull, global.Raw);
        Assert.Contains("Codeunit id:60698 was built outside any one test", global.Reason, StringComparison.Ordinal);
        Assert.Equal(All, global.Ran);
        foreach (var t in new[] { "RunsGlobalA", "RunsGlobalB" })
            Assert.Contains("PROBE-GLOBAL", global.Line[t], StringComparison.Ordinal);
    }

    // #5007's path: the change made while no server runs.
    [SkippableFact]
    public async Task NextServer_EmptyProcedureGainingCode_SelectsTheTestThatCalledIt()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-entersel-persist", "000000000002");
        var cache = TestScratch.Dir("al-runner-server-affected-entersel-persist-cache");

        var baseline = await SendFresh(cache, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.Equal(All, baseline.Ran);

        Write(bundle, "Cu.Codeunit.al", EmptyCodeunit(ProbeEmpty));
        var edited = await SendFresh(cache, bundle);
        Assert.False(edited.ForcedFull, edited.Raw);
        // Across processes the change is whole-object (no scope narrowing), so both callers of the codeunit run.
        Assert.Equal(new[] { "CallsEmpty", "CallsQ" }, edited.Ran);
        Assert.Contains("PROBE-EMPTY", edited.Line["CallsEmpty"], StringComparison.Ordinal);
        Assert.Equal("pass", edited.Status["CallsQ"]);

        Write(bundle, "Run.Codeunit.al", RunCodeunit(ProbeRun));
        Write(bundle, "Cu.Codeunit.al", EmptyCodeunit());
        var byId = await SendFresh(cache, bundle);
        Assert.False(byId.ForcedFull, byId.Raw);
        Assert.Equal(new[] { "CallsEmpty", "CallsQ", "RunsById" }, byId.Ran);
        Assert.Contains("PROBE-RUN", byId.Line["RunsById"], StringComparison.Ordinal);
    }
}
