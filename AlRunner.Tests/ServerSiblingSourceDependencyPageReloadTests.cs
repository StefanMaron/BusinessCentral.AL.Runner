// ServerSiblingSourceDependencyPageReloadTests — issue #4099.
//
// Same shape as ServerSiblingSourceDependencyReloadTests (#4025), but the edited dependency object
// is a PAGE, a PAGEEXTENSION (in the test bundle), a TABLE trigger and a TABLEEXTENSION trigger,
// with a codeunit arm alongside, which applied before the fix. Every arm reads a
// value that only the dependency's own trigger/procedure code produces, and the test asserts the
// 'A' variant, so a FAIL names which compile of the dependency executed.

using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerSiblingSourceDependencyPageReloadTests
{
    private const string SubjectAppId = "4099a000-0000-4000-8000-00000000a001";
    private const string TestsAppId = "4099a000-0000-4000-8000-00000000a002";

    private sealed record Fixture(string Root, string SubjectDir, string TestsDir, string CacheDir);

    private static readonly string[] Arms = { "DepPage", "DepPageExtension", "DepTableTrigger", "DepTableExtension", "DepLogic" };

    private static Fixture Create()
    {
        var root = TestScratch.Dir("al-runner-4099");
        var subjectDir = Path.Combine(root, "subject");
        var testsDir = Path.Combine(root, "tests");
        var cacheDir = TestScratch.Dir("al-runner-4099-cache");
        Directory.CreateDirectory(subjectDir);
        Directory.CreateDirectory(testsDir);

        File.WriteAllText(Path.Combine(testsDir, "app.json"), $$"""
        {
          "id": "{{TestsAppId}}",
          "name": "Repro4099 Tests",
          "publisher": "Repro4099",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{SubjectAppId}}", "name": "Repro4099 Subject", "publisher": "Repro4099", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 64095, "to": 64099 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(testsDir, "Tests.Codeunit.al"), """
        codeunit 64095 "Repro4099 Tests"
        {
            Subtype = Test;

            [Test]
            procedure DepPage()
            var
                TP: TestPage "Repro4099 Card";
            begin
                TP.OpenView();
                if TP.PageVarCtl.Value <> 'page-A' then
                    Error('dep page: expected page-A, actual %1', TP.PageVarCtl.Value);
                TP.Close();
            end;

            [Test]
            procedure DepPageExtension()
            var
                TP: TestPage "Repro4099 Card";
                Flag: Codeunit "Repro4099 Flag";
            begin
                // The extension's OnOpenPage raises its variant, because a control over an
                // extension variable is not bound on a TestPage yet (#3228). The flag keeps
                // that raise out of the other page arm.
                Flag.SetRaise(true);
                asserterror TP.OpenView();
                Flag.SetRaise(false);
                if GetLastErrorText() <> 'ext-A' then
                    Error('dep pageextension: expected ext-A, actual %1', GetLastErrorText());
            end;

            [Test]
            procedure DepTableTrigger()
            var
                Rec: Record "Repro4099 Rec";
            begin
                Rec.Init();
                Rec."Code" := 'K';
                Rec.Insert(true);
                if Rec.Tag <> 'table-A' then
                    Error('dep table trigger: expected table-A, actual %1', Rec.Tag);
            end;

            [Test]
            procedure DepTableExtension()
            var
                Rec: Record "Repro4099 Rec";
            begin
                Rec.Init();
                Rec."Code" := 'E';
                Rec.Insert(true);
                if Rec.ExtTag <> 'tabext-A' then
                    Error('dep tableextension: expected tabext-A, actual %1', Rec.ExtTag);
            end;

            [Test]
            procedure DepLogic()
            var
                Logic: Codeunit "Repro4099 Logic";
            begin
                if Logic.Twice(21) <> 42 then
                    Error('dep codeunit: expected 42, actual %1', Logic.Twice(21));
            end;
        }
        """);
        return new Fixture(root, subjectDir, testsDir, cacheDir);
    }

    /// <summary>Write the dependency with every arm's variant letter and the codeunit multiplier.</summary>
    private static void WriteSubject(Fixture f, string letter, int multiplier)
    {
        File.WriteAllText(Path.Combine(f.SubjectDir, "app.json"), $$"""
        {
          "id": "{{SubjectAppId}}",
          "name": "Repro4099 Subject",
          "publisher": "Repro4099",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 64090, "to": 64094 } ],
          "runtime": "14.0"
        }
        """);
        // The pageextension lives in the TEST bundle, so this arm edits the requested bundle's
        // generation, over the dependency's page.
        File.WriteAllText(Path.Combine(f.TestsDir, "CardExt.PageExt.al"), $$"""
        pageextension 64096 "Repro4099 Card Ext" extends "Repro4099 Card"
        {
            trigger OnOpenPage()
            var
                Flag: Codeunit "Repro4099 Flag";
            begin
                if Flag.Raise() then
                    Error('ext-{{letter}}');
            end;
        }

        codeunit 64097 "Repro4099 Flag"
        {
            SingleInstance = true;
            var
                RaiseOnOpen: Boolean;

            procedure SetRaise(Value: Boolean)
            begin
                RaiseOnOpen := Value;
            end;

            procedure Raise(): Boolean
            begin
                exit(RaiseOnOpen);
            end;
        }
        """);
        File.WriteAllText(Path.Combine(f.SubjectDir, "Subject.al"), $$"""
        table 64090 "Repro4099 Rec"
        {
            fields
            {
                field(1; "Code"; Code[10]) { }
                field(2; Tag; Text[30]) { }
            }
            keys { key(PK; "Code") { } }

            trigger OnInsert()
            begin
                Tag := 'table-{{letter}}';
            end;
        }

        page 64090 "Repro4099 Card"
        {
            PageType = Card;
            SourceTable = "Repro4099 Rec";
            layout { area(Content) { field(PageVarCtl; PageText) { ApplicationArea = All; } } }
            var
                PageText: Text[30];

            trigger OnOpenPage()
            begin
                PageText := 'page-{{letter}}';
            end;
        }

        tableextension 64093 "Repro4099 Rec Ext" extends "Repro4099 Rec"
        {
            fields { field(64093; ExtTag; Text[30]) { } }

            trigger OnBeforeInsert()
            begin
                ExtTag := 'tabext-{{letter}}';
            end;
        }

        codeunit 64091 "Repro4099 Logic"
        {
            procedure Twice(Value: Integer): Integer
            begin
                exit(Value * {{multiplier}});
            end;
        }
        """);
    }

    private static string Req(Fixture f) => JsonSerializer.Serialize(new
    {
        command = "runTests",
        sourcePaths = new[] { f.TestsDir },
        coverage = false,
    });

    /// <summary>
    /// Run one request. Variant 'A' (multiplier 2) must PASS every arm; any other variant must FAIL
    /// every arm naming the new value, which is what proves the edited compile executed.
    /// </summary>
    private static async Task AssertRequest(CliServer server, Fixture f, string label, string letter, int multiplier)
    {
        var lines = await server.SendRequestStreamingAsync(Req(f), TimeSpan.FromSeconds(240));
        var (events, _) = ProtocolV2Streaming.Split(lines);
        var joined = string.Join("\n", lines);
        var problems = new List<string>();
        foreach (var arm in Arms)
        {
            var ev = events.SingleOrDefault(e => e.GetProperty("name").GetString()!.EndsWith(arm));
            if (ev.ValueKind != JsonValueKind.Object) { problems.Add($"{arm}: did not run"); continue; }
            var status = ev.GetProperty("status").GetString();
            var message = ev.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            if (letter == "A")
            {
                if (status != "pass") problems.Add($"{arm}: expected PASS, got {status}: {message}");
                continue;
            }
            var expected = arm switch
            {
                "DepPage" => $"actual page-{letter}",
                "DepPageExtension" => $"actual ext-{letter}",
                "DepTableTrigger" => $"actual table-{letter}",
                "DepTableExtension" => $"actual tabext-{letter}",
                _ => $"actual {21 * multiplier}",
            };
            if (status != "fail" || !message.Contains(expected))
                problems.Add($"{arm}: expected FAIL naming '{expected}' (the edited dependency's code), got {status}: {message}");
        }
        Assert.True(problems.Count == 0,
            $"{label}:\n  " + string.Join("\n  ", problems) + $"\n{joined}\n--- stderr ---\n{server.StdErr}");
    }

    [SkippableFact]
    public async Task SiblingSourcePageEditedAtTheSameVersion_ExecutesTheNewPageCode_ColdAndWarm()
    {
        TestArtifacts.SkipIfMissing();
        var f = Create();
        var args = new[] { "--cache", f.CacheDir, "--verbose" };
        try
        {
            await using (var server = await CliServer.StartAsync(args))
            {
                WriteSubject(f, "A", 2);
                await AssertRequest(server, f, "cold 1 (A)", "A", 2);
                WriteSubject(f, "B", 3);
                await AssertRequest(server, f, "cold 2 (B)", "B", 3);
                WriteSubject(f, "A", 2);
                await AssertRequest(server, f, "cold 3 (back to A)", "A", 2);
            }

            // Fresh process, same --cache root: the first page generation it loads is B's.
            await using (var server = await CliServer.StartAsync(args))
            {
                WriteSubject(f, "B", 3);
                await AssertRequest(server, f, "warm 1 (B)", "B", 3);
                WriteSubject(f, "C", 5);
                await AssertRequest(server, f, "warm 2 (C)", "C", 5);
                Assert.True(server.StdErr.Contains("[deps] source-cache HIT: Repro4099 Subject"),
                    $"the fresh server never hit the compiled-deps cache:\n{server.StdErr}");
            }
        }
        finally
        {
            try { Directory.Delete(f.Root, recursive: true); } catch { }
            try { Directory.Delete(f.CacheDir, recursive: true); } catch { }
        }
    }
}
