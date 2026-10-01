// ServerAffectedSelectionObjectKindTests — #5083: under affectedOnly, a changed object of a kind no
// recorded key reaches (an enum, an enumextension, a reportextension, an interface, ...) forces a
// full run with a reason, instead of selecting nothing while a full run fails.
// Rules: docs/server-mode.md#affectedonly-and-object-kinds-no-test-records.
// Runs under --isolation test, like ServerAffectedSelectionPageExtensionTests: the no-change step
// asserts a narrowed run, which the default Codeunit isolation would widen.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerAffectedSelectionObjectKindTests
{
    private const string Interface = """
        interface "Kind Greeter SX"
        {
            procedure Name(): Text;
        }
        """;

    private const string InterfaceCommented = """
        interface "Kind Greeter SX"
        {
            // A comment changes the file, not what the interface does.
            procedure Name(): Text;
        }
        """;

    // The reviewer's probe on #5086: every implementer of the greeter now also is an "Other",
    // while no implementer or caller is edited.
    private const string InterfaceExtendsOther = """
        interface "Kind Greeter SX" extends "Kind Other SX"
        {
            procedure Name(): Text;
        }
        """;

    private const string OtherInterface = """
        interface "Kind Other SX"
        {
            procedure Hello(): Text;
        }
        """;

    private static string Impl(int id, string name, string answer)
        => $"codeunit {id} \"{name}\" implements \"Kind Greeter SX\"\n{{\n"
           + $"    procedure Name(): Text\n    begin\n        exit('{answer}');\n    end;\n\n"
           + "    procedure Hello(): Text\n    begin\n        exit('Hello');\n    end;\n}\n";

    private static string Enum(string captionA = "Alpha", string implA = "Kind Impl A SX")
        => "enum 60761 \"Kind Enum SX\" implements \"Kind Greeter SX\"\n{\n    Extensible = true;\n"
           + $"    value(0; A) {{ Caption = '{captionA}'; Implementation = \"Kind Greeter SX\" = \"{implA}\"; }}\n"
           + "    value(9; B) { Caption = 'Beta'; Implementation = \"Kind Greeter SX\" = \"Kind Impl B SX\"; }\n}\n";

    private static string EnumExtension(string captionC = "Gamma")
        => "enumextension 60764 \"Kind Enum Ext SX\" extends \"Kind Enum SX\"\n{\n"
           + $"    value(60764; C) {{ Caption = '{captionC}'; Implementation = \"Kind Greeter SX\" = \"Kind Impl B SX\"; }}\n}}\n";

    private const string Report = """
        report 60765 "Kind Report SX"
        {
            ProcessingOnly = true;
            dataset
            {
                dataitem(Int; Integer)
                {
                    DataItemTableView = where(Number = const(1));
                    column(Num; Number) { }
                }
            }
        }
        """;

    private static string ReportExtension(string extra = "")
        => "reportextension 60766 \"Kind Report Ext SX\" extends \"Kind Report SX\"\n{\n"
           + "    dataset\n    {\n        add(Int)\n        {\n            column(Num2; Number) { }\n" + extra
           + "        }\n    }\n}\n";

    private static string PermissionSet(string extra = "")
        => "permissionset 60767 \"Kind Perm SX\"\n{\n    Assignable = true;\n"
           + "    Permissions = codeunit \"Kind Impl A SX\" = X" + extra + ";\n}\n";

    private const string Tests = """
        codeunit 60768 "Kind Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure CaptionIsAlpha()
            var
                E: Enum "Kind Enum SX";
            begin
                E := E::A;
                if Format(E) <> 'Alpha' then
                    Error('caption was %1', Format(E));
            end;

            [Test]
            procedure ImplementationOfAIsA()
            var
                G: Interface "Kind Greeter SX";
            begin
                G := Enum::"Kind Enum SX"::A;
                if G.Name() <> 'A' then
                    Error('implementation answered %1', G.Name());
            end;

            [Test]
            procedure ExtensionCaptionIsGamma()
            var
                E: Enum "Kind Enum SX";
            begin
                E := E::C;
                if Format(E) <> 'Gamma' then
                    Error('extension caption was %1', Format(E));
            end;

            [Test]
            procedure ImplIsNotOther()
            var
                Impl: Codeunit "Kind Impl A SX";
                G: Interface "Kind Greeter SX";
            begin
                G := Impl;
                if G is "Kind Other SX" then
                    Error('G is Other');
            end;

            [Test]
            procedure Unrelated()
            begin
                if 1 + 1 <> 2 then
                    Error('Unrelated failed');
            end;
        }
        """;

    private static readonly string[] All = { "CaptionIsAlpha", "ExtensionCaptionIsGamma", "ImplIsNotOther", "ImplementationOfAIsA", "Unrelated" };

    private static string Bundle(string prefix, string appIdSuffix)
    {
        var dir = TestScratch.Dir(prefix);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "c5083000-0000-4a11-9111-{{appIdSuffix}}",
          "name": "Object Kind Selection SX {{appIdSuffix}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60761, "to": 60769 } ],
          "runtime": "14.0"
        }
        """);
        Write(dir, "Greeter.Interface.al", Interface);
        Write(dir, "Other.Interface.al", OtherInterface);
        Write(dir, "ImplA.Codeunit.al", Impl(60762, "Kind Impl A SX", "A"));
        Write(dir, "ImplB.Codeunit.al", Impl(60763, "Kind Impl B SX", "B"));
        Write(dir, "Kind.Enum.al", Enum());
        Write(dir, "KindExt.EnumExt.al", EnumExtension());
        Write(dir, "Kind.Report.al", Report);
        Write(dir, "KindExt.ReportExt.al", ReportExtension());
        Write(dir, "Kind.PermissionSet.al", PermissionSet());
        Write(dir, "Tests.Codeunit.al", Tests);
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

    // Forced full with the reason naming the changed object; the failing test's line carries its message.
    private static void AssertForcedFull(Observed o, string reason, string? failing = null, string? message = null)
    {
        Assert.True(o.ForcedFull, o.Raw);
        Assert.Contains(reason, o.Reason, StringComparison.Ordinal);
        Assert.Equal(All, o.Ran);
        foreach (var t in All)
        {
            if (t == failing)
            {
                Assert.NotEqual("pass", o.Status[t]);
                Assert.Contains(message!, o.Line[t], StringComparison.Ordinal);
            }
            else Assert.True(o.Status[t] == "pass", o.Raw);
        }
    }

    [SkippableFact]
    public async Task ChangedEnumOrExtensionKind_ForcesFull_AndRunsTheTestAFullRunFails()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-kind", "000000000001");
        await using var server = await CliServer.StartAsync(new[] { "--isolation", "test", "--no-cache" });

        var baseline = await Send(server, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.Equal(All, baseline.Ran);
        Assert.All(baseline.Status.Values, s => Assert.Equal("pass", s));

        // Nothing changed: narrowed to nothing, so the guard is not a blanket full run.
        var none = await Send(server, bundle);
        Assert.False(none.ForcedFull, none.Raw);
        Assert.Empty(none.Ran);

        // The reviewer's probe: a caption change keeps every ordinal.
        Write(bundle, "Kind.Enum.al", Enum(captionA: "Changed"));
        AssertForcedFull(await Send(server, bundle), "Enum 60761 changed", "CaptionIsAlpha", "caption was Changed");
        Write(bundle, "Kind.Enum.al", Enum());
        AssertForcedFull(await Send(server, bundle), "Enum 60761 changed");

        // An Implementation change dispatches the interface to another codeunit, neither of which changed.
        Write(bundle, "Kind.Enum.al", Enum(implA: "Kind Impl B SX"));
        AssertForcedFull(await Send(server, bundle), "Enum 60761 changed", "ImplementationOfAIsA", "implementation answered B");
        Write(bundle, "Kind.Enum.al", Enum());
        await Send(server, bundle);

        Write(bundle, "KindExt.EnumExt.al", EnumExtension(captionC: "Changed"));
        AssertForcedFull(await Send(server, bundle), "EnumExtension 60764 changed", "ExtensionCaptionIsGamma", "extension caption was Changed");
        Write(bundle, "KindExt.EnumExt.al", EnumExtension());
        await Send(server, bundle);

        // An edited reportextension: the incremental compile falls back to a full one today, which
        // forces a full run before this guard is consulted; NextServer_* proves the guard's own reason.
        Write(bundle, "KindExt.ReportExt.al", ReportExtension("            column(Num3; Number) { }\n"));
        var reportExtension = await Send(server, bundle);
        Assert.True(reportExtension.ForcedFull, reportExtension.Raw);
        Assert.Equal(All, reportExtension.Ran);

        Write(bundle, "Kind.PermissionSet.al", PermissionSet(",\n        codeunit \"Kind Impl B SX\" = X"));
        AssertForcedFull(await Send(server, bundle), "PermissionSet 60767 changed, and no test recording holds the use of this kind of object (PermissionSet)");

        // An interface is unkeyed too: even a comment-only edit runs everything.
        Write(bundle, "Greeter.Interface.al", InterfaceCommented);
        AssertForcedFull(await Send(server, bundle), "Interface Kind Greeter SX changed");

        // An extends clause changes what `is` answers for an implementer nobody edited. Only the
        // selection is asserted here: this server's incremental compile keeps the implementer's old
        // interface list (5089), so NextServer_* asserts the outcome.
        Write(bundle, "Greeter.Interface.al", InterfaceExtendsOther);
        var extends = await Send(server, bundle);
        Assert.True(extends.ForcedFull, extends.Raw);
        Assert.Contains("Interface Kind Greeter SX changed", extends.Reason, StringComparison.Ordinal);
        Assert.Equal(All, extends.Ran);
    }

    // #5007's path: changed while no server runs, so the persisted baseline's diff names the object,
    // and each request compiles cold.
    [SkippableFact]
    public async Task NextServer_ChangedEnumCaptionOrReportExtension_ForcesFull()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-kind-persist", "000000000002");
        var cache = TestScratch.Dir("al-runner-server-affected-kind-persist-cache");

        var baseline = await SendFresh(cache, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.All(baseline.Status.Values, s => Assert.Equal("pass", s));

        Write(bundle, "Kind.Enum.al", Enum(captionA: "Changed"));
        AssertForcedFull(await SendFresh(cache, bundle), "Enum 60761 changed", "CaptionIsAlpha", "caption was Changed");
        Write(bundle, "Kind.Enum.al", Enum());
        await SendFresh(cache, bundle);

        // No test here runs the report (#4918: a reportextension's report-level code is not bound),
        // so this pins the selection, not an outcome.
        Write(bundle, "KindExt.ReportExt.al", ReportExtension("            column(Num3; Number) { }\n"));
        AssertForcedFull(await SendFresh(cache, bundle),
            "ReportExtension 60766 changed, and no test recording holds the use of this kind of object (ReportExtension)");

        Write(bundle, "Greeter.Interface.al", InterfaceExtendsOther);
        AssertForcedFull(await SendFresh(cache, bundle),
            "Interface Kind Greeter SX changed, and no test recording holds the use of this kind of object (Interface)",
            "ImplIsNotOther", "G is Other");
    }
}
