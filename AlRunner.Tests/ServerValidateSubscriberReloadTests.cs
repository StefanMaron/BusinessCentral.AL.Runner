using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2369, end to end: the per-record-construction subscriber injectors read a per-table
/// index that is built on first use. Request 1 builds it for a table with no subscribers; request
/// 2, in the same --server process and for the same bundle, adds a field-validate subscriber and a
/// table-trigger subscriber on that table. Both must fire, and the validate subscriber must stay
/// scoped to its own field. A stale index answers request 2 with request 1's "nobody subscribes".
/// </summary>
public class ServerValidateSubscriberReloadTests
{
    private const string TableAl = """
        table 79931 "SVSR Probe"
        {
            fields
            {
                field(1; "Code"; Code[20]) { }
                field(2; Qty; Integer) { }
                field(3; Other; Integer) { }
                field(4; Note; Text[30]) { }
                field(5; InsertNote; Text[30]) { }
            }
            keys { key(PK; "Code") { Clustered = true; } }
        }
        """;

    private const string SubscriberAl = """
        codeunit 79932 "SVSR Subscribers"
        {
            [EventSubscriber(ObjectType::Table, Database::"SVSR Probe", 'OnAfterValidateEvent', 'Qty', false, false)]
            local procedure QtyValidated(var Rec: Record "SVSR Probe")
            begin
                Rec.Note := 'QTY SUBSCRIBER';
            end;

            [EventSubscriber(ObjectType::Table, Database::"SVSR Probe", 'OnBeforeInsertEvent', '', false, false)]
            local procedure BeforeInsert(var Rec: Record "SVSR Probe")
            begin
                Rec.InsertNote := 'INSERT SUBSCRIBER';
            end;
        }
        """;

    private static void WriteBundle(string dir, bool withSubscribers)
    {
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "0b4c2369-7d1e-4f55-9a3c-2369a1b2c3d4",
          "name": "Runner Extras - Server Validate Subscriber Reload",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 79931, "to": 79939 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Probe.Table.al"), TableAl);
        var subscriberFile = Path.Combine(dir, "Subscribers.Codeunit.al");
        if (withSubscribers) File.WriteAllText(subscriberFile, SubscriberAl);
        else if (File.Exists(subscriberFile)) File.Delete(subscriberFile);

        var expectedNote = withSubscribers ? "QTY SUBSCRIBER" : "";
        var expectedInsertNote = withSubscribers ? "INSERT SUBSCRIBER" : "";
        File.WriteAllText(Path.Combine(dir, "Probe.Codeunit.al"), $$"""
        codeunit 79933 "SVSR Tests"
        {
            Subtype = Test;

            [Test]
            procedure ValidateQtyAndInsert()
            var
                Rec: Record "SVSR Probe";
                Stored: Record "SVSR Probe";
            begin
                Rec.Init();
                Rec."Code" := 'A';
                Rec.Validate(Qty, 5);
                if Rec.Note <> '{{expectedNote}}' then
                    Error('Qty validate: expected Note ''{{expectedNote}}'', got ''%1''', Rec.Note);
                Rec.Insert();
                Stored.Get('A');
                if Stored.InsertNote <> '{{expectedInsertNote}}' then
                    Error('insert: expected InsertNote ''{{expectedInsertNote}}'', got ''%1''', Stored.InsertNote);
            end;

            [Test]
            procedure ValidateOtherFieldDoesNotFireQtySubscriber()
            var
                Rec: Record "SVSR Probe";
            begin
                Rec.Init();
                Rec.Validate(Other, 7);
                if Rec.Note <> '' then
                    Error('Other validate fired a Qty-scoped subscriber: Note = ''%1''', Rec.Note);
            end;
        }
        """);
    }

    private static string Req(string bundleDir)
        => JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { bundleDir },
            packagePaths = Array.Empty<string>(),
        });

    private static void AssertAllPassed(List<string> lines, string request)
    {
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        var failures = string.Join(" | ", events
            .Where(e => e.TryGetProperty("status", out var s) && s.GetString() != "pass")
            .Select(e => e.GetRawText()));
        Assert.True(summary.GetProperty("failed").GetInt32() == 0, $"{request}: {failures} | {summary.GetRawText()}");
        Assert.Equal(2, summary.GetProperty("total").GetInt32());
        Assert.Equal(2, summary.GetProperty("passed").GetInt32());
    }

    [SkippableFact]
    public async Task SubscribersAddedByAReload_FireOnATableTheIndexAlreadyKnew()
    {
        TestArtifacts.SkipIfMissing();

        var dir = TestScratch.Dir("al-runner-server-validate-subscriber-reload");
        Directory.CreateDirectory(dir);
        await using var server = await CliServer.StartAsync();

        WriteBundle(dir, withSubscribers: false);
        AssertAllPassed(await server.SendRequestStreamingAsync(Req(dir)), "request 1 (no subscribers)");

        WriteBundle(dir, withSubscribers: true);
        AssertAllPassed(await server.SendRequestStreamingAsync(Req(dir)), "request 2 (subscribers added)");
    }
}
