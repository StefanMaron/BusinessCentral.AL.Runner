// #4903: while a host TestPage opens, the runner builds every part eagerly (#2677). An AL
// Error() in a part's OnOpenPage fails the host's OpenView, as on BC (corpus codeunit 67010), and
// an out-of-scope surface a part's OnOpenPage touches is reported as out-of-scope rather than
// swallowed. Only a [RunOnClient] DotNet callback refusal (#2772) is absorbed, which needs
// Base App to reach and is pinned by tests/runner-extras/testpage-trigger-inject-timing. An
// unhandled Confirm in a part's OnOpenPage is left to #4915: BC 27.x and 28.x disagree on it.
using Xunit;

namespace AlRunner.Tests;

public sealed class HostOpenPartErrorTests
{
    private static string Part(int id, string name, string onOpen) => $$"""
        page {{id}} "{{name}}"
        {
            PageType = CardPart;
            SourceTable = "Hope Row";
            layout { area(Content) { field(PartCode; Rec."Code") { ApplicationArea = All; } } }
            trigger OnOpenPage()
            var
                Log: Codeunit "Hope Log";
            begin
                Log.Add('{{name}}');
                {{onOpen}}
            end;
        }
        """;

    private static string Host(int id, string name, string partPage) => $$"""
        page {{id}} "{{name}}"
        {
            PageType = Card;
            SourceTable = "Hope Row";
            layout { area(Content) { field(HostCode; Rec."Code") { ApplicationArea = All; } part(ThePart; "{{partPage}}") { ApplicationArea = All; } } }
        }
        """;

    [SkippableFact]
    public async Task PartErrorFailsTheHostsOpen_AndAPartsOutOfScopeCallIsReported()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-host-open-part-error");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), $$"""
        { "id": "{{Guid.NewGuid()}}", "name": "Hope", "publisher": "AL Runner", "version": "1.0.0.0",
          "dependencies": [], "platform": "1.0.0.0", "idRanges": [ { "from": 62810, "to": 62829 } ], "runtime": "14.0" }
        """);
        File.WriteAllText(Path.Combine(root, "Hope.al"),
            """
            table 62810 "Hope Row" { fields { field(1; "Code"; Code[20]) { } } keys { key(PK; "Code") { Clustered = true; } } }
            codeunit 62811 "Hope Log"
            {
                SingleInstance = true;
                var Entries: Text;
                procedure Add(Entry: Text) begin Entries += Entry + ';'; end;
                procedure Take(): Text var T: Text; begin T := Entries; Entries := ''; exit(T); end;
            }
            """
            + Part(62812, "Hope Error Part", "Error('HOPE part refused');")
            + Part(62813, "Hope Refusing Part", "Client.Get('http://hope.invalid/', Response);").Replace(
                "Log: Codeunit \"Hope Log\";", "Log: Codeunit \"Hope Log\";\n        Client: HttpClient;\n        Response: HttpResponseMessage;")
            + Part(62814, "Hope Clean Part", "")
            + Host(62815, "Hope Error Host", "Hope Error Part")
            + Host(62816, "Hope Refusing Host", "Hope Refusing Part")
            + Host(62817, "Hope Clean Host", "Hope Clean Part")
            + """
            codeunit 62818 "Hope Tests"
            {
                Subtype = Test;

                [Test]
                procedure ErroringPart_FailsTheHostsOpenView()
                var
                    Host: TestPage "Hope Error Host";
                    Log: Codeunit "Hope Log";
                begin
                    Log.Take();
                    asserterror Host.OpenView();
                    if GetLastErrorText() <> 'HOPE part refused' then Error('WRONG: OpenView ended with: %1', GetLastErrorText());
                    if StrPos(Log.Take(), 'Hope Error Part;') = 0 then Error('WRONG: the part''s OnOpenPage did not run');
                end;

                [Test]
                procedure RefusingPart_IsReportedOutOfScope()
                var
                    Host: TestPage "Hope Refusing Host";
                    Log: Codeunit "Hope Log";
                begin
                    Log.Take();
                    Host.OpenView();
                    Host.Close();
                    if StrPos(Log.Take(), 'Hope Refusing Part;') = 0 then Error('WRONG: the refusing part''s OnOpenPage did not run');
                end;

                [Test]
                procedure CleanPart_HostOpens_AndThePartOpened()
                var
                    Host: TestPage "Hope Clean Host";
                    Log: Codeunit "Hope Log";
                begin
                    Log.Take();
                    Host.OpenView();
                    Host.Close();
                    if StrPos(Log.Take(), 'Hope Clean Part;') = 0 then Error('WRONG: the clean part''s OnOpenPage did not run');
                end;
            }
            """);

        var result = await SuiteServer.RunViaServer(root);
        // RefusingPart_IsReportedOutOfScope fails by design: the refusal is the assertion.
        result.AssertCounts(passed: 2, failed: 1, errors: 0);
        var refusing = result.Tests.Single(t => t.Name == "Codeunit62818.RefusingPart_IsReportedOutOfScope");
        Assert.Equal("fail", refusing.Status);
        Assert.Contains("Unexpected out-of-scope: HttpClient.Get (reason: external-http)", refusing.Message);
        result.AssertOutputDoesNotContain("WRONG:");
        Assert.Equal(1, result.ExitCode);
    }

    /// <summary>
    /// The one absorbed exception type is also what BC raises for every other client call with
    /// no client (Confirm, RunModal, Hyperlink, …). Only a [RunOnClient] DotNet access is #2772's
    /// case; a refusal no NavDotNet frame raised is not absorbed. The positive half needs Base
    /// App's camera FactBox: tests/runner-extras/testpage-trigger-inject-timing.
    /// </summary>
    [Fact]
    public void ACallbackRefusalNoDotNetMemberRaised_IsNotAbsorbed()
    {
        Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLCallbackNotAllowedException? caught = null;
        try { RaiseCallbackRefusal(); }
        catch (Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLCallbackNotAllowedException ex) { caught = ex; }

        Assert.NotNull(caught);
        Assert.False(LiveNavTestPage.IsRunOnClientDotNetAccess(caught!));
    }

    private static readonly (string?, string?) GetClientCallback = ("Microsoft.Dynamics.Nav.Runtime", "NavSession");
    private static readonly (string?, string?) DotNetFrame = ("Microsoft.Dynamics.Nav.Runtime", "NavDotNet");
    private static readonly (string?, string?) AlFrame = ("Microsoft.Dynamics.Nav.BusinessApplication", "Codeunit1908");

    [Fact]
    public void Frames_DotNetBeforeTheFirstAlFrame_IsAbsorbed()
        => Assert.True(LiveNavTestPage.IsRunOnClientDotNetAccess(new[] { GetClientCallback, DotNetFrame, AlFrame }));

    [Fact]
    public void Frames_AlBeforeTheDotNetFrame_IsNotAbsorbed()
        // The shape of a Confirm raised by AL code that an outer [RunOnClient] DotNet call reached.
        => Assert.False(LiveNavTestPage.IsRunOnClientDotNetAccess(new[] { GetClientCallback, AlFrame, DotNetFrame }));

    [Fact]
    public void Frames_NoDotNetFrame_IsNotAbsorbed()
        => Assert.False(LiveNavTestPage.IsRunOnClientDotNetAccess(new[] { GetClientCallback, AlFrame }));

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RaiseCallbackRefusal() => throw new Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLCallbackNotAllowedException();
}
