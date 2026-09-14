using System.Text.Json;
using AlRunner.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace AlRunner.Tests;

public sealed class ServerTableRelationReloadTests(ITestOutputHelper output)
{
    // A dedicated process is essential: #3964 only fails on the first request, even
    // when the compile cache is already warm. Target is deliberately not a sibling.
    [SkippableFact]
    public async Task PackagedSiblingRelations_WorkOnFirstAndRepeatedRequests_InColdAndWarmServers()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-server-relation-reload");
        try
        {
            var target = Path.Combine(root, "external", "target");
            var subject = Path.Combine(root, "source", "subject");
            var tests = Path.Combine(root, "source", "tests");
            var packages = Path.Combine(root, "packages");
            var targetId = Guid.NewGuid();
            var subjectId = Guid.NewGuid();
            WriteApp(target, targetId, "SRR Target", []);
            WriteApp(subject, subjectId, "SRR Subject", [(targetId, "SRR Target")]);
            WriteApp(tests, Guid.NewGuid(), "SRR Tests", [(targetId, "SRR Target"), (subjectId, "SRR Subject")]);
            File.WriteAllText(Path.Combine(target, "Target.al"), """
                table 70780 "SRR Target"
                {
                    fields { field(1; Code; Code[20]) { } field(2; Marker; Integer) { } }
                    keys { key(PK; Code) { Clustered = true; } }
                }
                codeunit 70780 "SRR Install"
                {
                    Subtype = Install;
                    trigger OnInstallAppPerCompany()
                    var Target: Record "SRR Target";
                    begin
                        Target.Code := 'PRESENT'; Target.Marker := 37; Target.Insert();
                    end;
                }
                """);
            File.WriteAllText(Path.Combine(subject, "Subject.al"), """
                table 70781 "SRR Subject"
                {
                    fields
                    {
                        field(1; Code; Code[20]) { }
                        field(2; "Target Code"; Code[20]) { TableRelation = "SRR Target".Code; }
                    }
                    keys { key(PK; Code) { Clustered = true; } }
                }
                codeunit 70781 "SRR Logic"
                {
                    procedure Twice(Value: Integer): Integer
                    begin exit(Value * 2); end;
                }
                """);
            File.WriteAllText(Path.Combine(tests, "Tests.al"), """
                codeunit 70782 "SRR Tests"
                {
                    Subtype = Test;
                    var Assert: Codeunit "SRR Assert";
                    [Test] procedure ValidRelation()
                    var Subject: Record "SRR Subject";
                    begin
                        Subject.Validate("Target Code", 'PRESENT');
                        Assert.AreEqual('PRESENT', Subject."Target Code", 'valid relation');
                    end;
                    [Test] procedure MissingRelation()
                    var Subject: Record "SRR Subject";
                    begin
                        asserterror Subject.Validate("Target Code", 'MISSING');
                        Assert.ExpectedError('cannot be found');
                    end;
                    [Test] procedure DependencyLogic()
                    var Logic: Codeunit "SRR Logic";
                    begin
                        Assert.AreEqual('42', Format(Logic.Twice(21)), 'dependency result');
                    end;
                    [Test] procedure InstallSeed()
                    var Target: Record "SRR Target";
                    begin
                        Target.Get('PRESENT');
                        Assert.AreEqual('37', Format(Target.Marker), 'install seed');
                    end;
                }
                codeunit 70783 "SRR Assert"
                {
                    procedure AreEqual(Expected: Text; Actual: Text; Context: Text)
                    begin
                        if Expected <> Actual then
                            Error('%1: expected %2, actual %3', Context, Expected, Actual);
                    end;
                    procedure ExpectedError(Part: Text)
                    begin
                        if StrPos(GetLastErrorText(), Part) = 0 then
                            Error('Expected error containing %1, actual %2', Part, GetLastErrorText());
                    end;
                }
                """);
            Directory.CreateDirectory(packages);
            Package(target, packages, targetId, "SRR Target", 70780, false);
            Package(subject, packages, subjectId, "SRR Subject", 70781, true);
            var cache = Path.Combine(root, "cache");
            var results = new List<(List<JsonElement> Events, JsonElement Summary, bool Cached, string Diagnostic)>();
            foreach (var coverage in new[] { false, true })
            {
                await using var server = await CliServer.StartAsync(["--cache", cache, "--package-cache", packages]);
                var request = JsonSerializer.Serialize(new
                {
                    command = "runTests", sourcePaths = new[] { tests },
                    packagePaths = new[] { packages }, coverage
                });
                for (var i = 0; i < 3; i++)
                {
                    var lines = await server.SendRequestStreamingAsync(request, TimeSpan.FromSeconds(180));
                    var (events, summary) = ProtocolV2Streaming.Split(lines);
                    var diagnostic = $"coverage={coverage}, request={i + 1}: {string.Join("\n", lines)}\n{server.StdErr}";
                    output.WriteLine($"coverage={coverage}, request={i + 1}: {summary}");
                    output.WriteLine(string.Join(", ", events.Select(e => $"{e.GetProperty("name")}={e.GetProperty("status")}")));
                    results.Add((events, summary, coverage || i > 0, diagnostic));
                }
            }
            // Finish both lifecycles before asserting so a regression exposes first-request
            // failures on BOTH cold and already-populated caches, plus subsequent recovery.
            Assert.All(results, result =>
            {
                var (events, summary, cached, diagnostic) = result;
                Assert.True(summary.GetProperty("exitCode").GetInt32() == 0, diagnostic);
                Assert.Equal(4, events.Count);
                Assert.Equal(new[] { "DependencyLogic", "InstallSeed", "MissingRelation", "ValidRelation" },
                    events.Select(e => e.GetProperty("name").GetString()!.Split('.').Last()).OrderBy(n => n).ToArray());
                Assert.All(events, e => Assert.True(e.GetProperty("status").GetString() == "pass", diagnostic));
                Assert.Equal(4, summary.GetProperty("passed").GetInt32());
                Assert.Equal(0, summary.GetProperty("failed").GetInt32());
                Assert.Equal(0, summary.GetProperty("errors").GetInt32());
                Assert.Equal(4, summary.GetProperty("total").GetInt32());
                Assert.Equal(cached, summary.GetProperty("cached").GetBoolean());
            });
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort scratch cleanup */ }
        }
    }

    private static void WriteApp(string directory, Guid id, string name, (Guid Id, string Name)[] dependencies)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "app.json"), JsonSerializer.Serialize(new
        {
            id, name, publisher = "AL Runner", version = "1.0.0.0", platform = "1.0.0.0", runtime = "14.0",
            idRanges = new[] { new { from = 70780, to = 70789 } },
            dependencies = dependencies.Select(d => new { id = d.Id, name = d.Name, publisher = "AL Runner", version = "1.0.0.0" })
        }));
    }

    // Like SymbolAppFixture, supply actual registrable symbols, rather than the
    // source-only packages synthesized by the sibling pre-pass. Sources remain embedded.
    // The type spelling and Twice(Integer): Integer signature id match BC-emitted
    // SymbolReference.json for this AL declaration; executable code and authoritative
    // table documents are compiled from the embedded source by the tested runner.
    private static void Package(string source, string packages, Guid id, string name, int tableId, bool subject)
    {
        var symbols = JsonSerializer.SerializeToUtf8Bytes(new
        {
            AppId = id, Name = name, Publisher = "AL Runner", Version = "1.0.0.0", RuntimeVersion = "14.0",
            Tables = new[] { new {
                Id = tableId, Name = name,
                Fields = new object[] {
                    new { Id = 1, Name = "Code", TypeDefinition = new { Name = "Code[20]" } },
                    new { Id = 2, Name = subject ? "Target Code" : "Marker", TypeDefinition = new { Name = subject ? "Code[20]" : "Integer" },
                        Properties = subject ? new[] { new { Name = "TableRelation", Value = "\"SRR Target\".Code" } } : [] }
                },
                Keys = new[] { new { Name = "PK", FieldNames = new[] { "Code" }, Properties = new[] { new { Name = "Clustered", Value = "1" } } } }
            } },
            Codeunits = subject
                ? new object[] { new { Id = tableId, Name = "SRR Logic", Methods = new[] { new {
                    Id = 1516892452, Name = "Twice", ReturnTypeDefinition = new { Name = "Integer" },
                    Parameters = new[] { new { Name = "Value", TypeDefinition = new { Name = "Integer" } } }
                } } } }
                : new object[] { new { Id = tableId, Name = "SRR Install", Properties = new[] { new { Name = "Subtype", Value = "Install" } } } }
        });
        var identity = InProcessAppPackager.ReadIdentity(Path.Combine(source, "app.json"))!;
        InProcessAppPackager.EmitAppPackageToFile(source, identity, Path.Combine(packages, name + ".app"), symbols);
    }
}
