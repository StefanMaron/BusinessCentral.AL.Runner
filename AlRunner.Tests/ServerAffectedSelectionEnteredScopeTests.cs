// ServerAffectedSelectionEnteredScopeTests — #5011: under affectedOnly, a test that used an object
// or entered a procedure without executing any statement of it is selected when that object or
// procedure changes. Mechanism: docs/server-mode.md#affectedonly-and-entered-scopes.
// Runs under --isolation test: these assert per-test narrowing inside one codeunit, which the
// default Codeunit isolation widens to the whole codeunit (#5035, ServerAffectedSelectionSharedSetupTests).
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

    // A field trigger and an action trigger, both empty: their AL names ("PK - OnValidate",
    // "Act - OnAction") differ from their C# method names, which is what selection must key on.
    private static string Page(string triggers = "", string validateBody = "", string actionBody = "")
        => "page 60696 \"EnterSel Page SX\"\n{\n    SourceTable = \"EnterSel Tab SX\";\n"
           + "    layout\n    {\n        area(Content)\n        {\n            field(PK; Rec.PK)\n            {\n"
           + "                trigger OnValidate()\n                begin\n" + validateBody
           + "                end;\n            }\n        }\n    }\n"
           + "    actions\n    {\n        area(Processing)\n        {\n            action(Act)\n            {\n"
           + "                trigger OnAction()\n                begin\n" + actionBody
           + "                end;\n            }\n        }\n    }\n"
           + triggers + "}\n";

    // A processing-only report whose dataitem trigger is empty.
    private static string Report(string body = "")
        => "report 60700 \"EnterSel Report SX\"\n{\n    ProcessingOnly = true;\n\n    dataset\n    {\n"
           + "        dataitem(T; \"EnterSel Tab SX\")\n        {\n"
           + "            trigger OnAfterGetRecord()\n            begin\n" + body
           + "            end;\n        }\n    }\n}\n";

    private const string ProbeValidate = "                    Error('PROBE-VALIDATE');\n";
    private const string ProbeAction = "                    Error('PROBE-ACTION');\n";
    private const string ProbeReport = "                Error('PROBE-REPORT');\n";

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
            procedure ValidatesPageField()
            var
                TP: TestPage "EnterSel Page SX";
            begin
                TP.OpenNew();
                TP.PK.SetValue(5);
                TP.Close();
            end;

            [Test]
            procedure InvokesPageAction()
            var
                TP: TestPage "EnterSel Page SX";
            begin
                TP.OpenView();
                TP.Act.Invoke();
                TP.Close();
            end;

            [Test]
            procedure RunsReport()
            var
                T: Record "EnterSel Tab SX";
            begin
                T.PK := 1;
                T.Insert();
                Report.Run(60700, false);
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
        { "CallsEmpty", "CallsQ", "InvokesPageAction", "OpensPage", "RunsById", "RunsGlobalA", "RunsGlobalB",
          "RunsNoTriggerById", "RunsReport", "Unrelated", "ValidatesPageField" };

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
          "idRanges": [ { "from": 60690, "to": 60709 } ],
          "runtime": "14.0"
        }
        """);
        Write(dir, "Cu.Codeunit.al", EmptyCodeunit());
        Write(dir, "Run.Codeunit.al", RunCodeunit());
        Write(dir, "NoTrig.Codeunit.al", NoTriggerCodeunit());
        Write(dir, "Tab.Table.al", Table);
        Write(dir, "Page.Page.al", Page());
        Write(dir, "Report.Report.al", Report());
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
        await using var server = await CliServer.StartAsync(new[] { "--isolation", "test", "--cache", cache });
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
        await using var server = await CliServer.StartAsync(new[] { "--isolation", "test", "--no-cache" });

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

        // Empty triggers whose AL name is not their C# method name: a page field's OnValidate,
        // a page action's OnAction, a report dataitem's OnAfterGetRecord. Each change is narrowed
        // to that trigger, so only the test that drives it runs, not the other tests opening the page.
        Write(bundle, "Page.Page.al", Page(validateBody: ProbeValidate));
        AssertSelectedAndFails(await Send(server, bundle), "ValidatesPageField", "PROBE-VALIDATE");
        Write(bundle, "Page.Page.al", Page(actionBody: ProbeAction));
        var action = await Send(server, bundle);
        // The revert of OnValidate is a deletion, which widens to the whole page.
        Assert.False(action.ForcedFull, action.Raw);
        Assert.Equal(new[] { "InvokesPageAction", "OpensPage", "ValidatesPageField" }, action.Ran);
        Assert.Contains("PROBE-ACTION", action.Line["InvokesPageAction"], StringComparison.Ordinal);
        Assert.Equal("pass", action.Status["OpensPage"]);
        Assert.Equal("pass", action.Status["ValidatesPageField"]);
        Write(bundle, "Page.Page.al", Page());
        Assert.Equal(new[] { "InvokesPageAction", "OpensPage", "ValidatesPageField" }, (await Send(server, bundle)).Ran);
        Write(bundle, "Page.Page.al", Page(actionBody: ProbeAction));
        AssertSelectedAndFails(await Send(server, bundle), "InvokesPageAction", "PROBE-ACTION");
        Write(bundle, "Page.Page.al", Page());
        await Send(server, bundle);

        Write(bundle, "Report.Report.al", Report(ProbeReport));
        AssertSelectedAndFails(await Send(server, bundle), "RunsReport", "PROBE-REPORT");

        // A page trigger added where none existed: whole-object, so every test that opened the page.
        Write(bundle, "Page.Page.al", Page(ProbePage));
        var page = await Send(server, bundle);
        Assert.False(page.ForcedFull, page.Raw);
        Assert.Equal(new[] { "InvokesPageAction", "OpensPage", "ValidatesPageField" }, page.Ran);
        foreach (var t in page.Ran)
            Assert.Contains("PROBE-PAGE", page.Line[t], StringComparison.Ordinal);

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
