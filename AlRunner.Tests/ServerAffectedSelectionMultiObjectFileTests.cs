// ServerAffectedSelectionMultiObjectFileTests — #5003: a test whose statements sit in a file that
// declares several objects is attributed to the object that owns each statement, so an unchanged
// request skips it. A CHANGE to such a file still runs everything, because the change model tracks
// one object per file (docs/server-mode.md#affectedonly-and-files-declaring-several-objects).
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

// #5110: facts that need no startup flag of their own share one --server (SharedCliServer).
public class ServerAffectedSelectionMultiObjectFileTests : IClassFixture<SharedCliServer>
{
    private readonly SharedCliServer _fixture;

    public ServerAffectedSelectionMultiObjectFileTests(SharedCliServer fixture) => _fixture = fixture;

    // One file per shape the issue's reading has to hold for; the test codeunit sits in its own file.
    private const string SameKind = """
        codeunit 62401 "MO A One"
        {
            procedure Value(): Integer
            begin
                exit(1);
            end;
        }

        codeunit 62402 "MO A Two"
        {
            procedure Value(): Integer
            begin
                exit(2);
            end;
        }
        """;

    private static string CodeunitAndTable(int helperValue = 7) => $$"""
        codeunit 62403 "MO B Helper"
        {
            procedure Value(): Integer
            begin
                exit({{helperValue}});
            end;
        }

        table 62404 "MO B Rows"
        {
            fields
            {
                field(1; PK; Integer) { }
            }
            keys { key(PK; PK) { Clustered = true; } }

            procedure Doubled(V: Integer): Integer
            begin
                exit(V * 2);
            end;
        }
        """;

    private const string TableAndExtension = """
        table 62405 "MO C Base"
        {
            fields
            {
                field(1; PK; Integer) { }
            }
            keys { key(PK; PK) { Clustered = true; } }

            procedure BaseValue(): Integer
            begin
                exit(3);
            end;
        }

        tableextension 62406 "MO C Ext" extends "MO C Base"
        {
            procedure ExtValue(): Integer
            begin
                exit(30);
            end;
        }
        """;

    private const string PageAndExtension = """
        page 62407 "MO D Page"
        {
            PageType = Card;

            procedure Value(): Integer
            begin
                exit(4);
            end;
        }

        pageextension 62408 "MO D Ext" extends "MO D Page"
        {
            trigger OnOpenPage()
            begin
                Message('MO D');
            end;
        }
        """;

    // Two objects of one kind with near-identical names, told apart only by id, in one file.
    private const string SameNameShape = """
        codeunit 62409 "MO E Twin"
        {
            procedure Value(): Integer
            begin
                exit(5);
            end;
        }

        codeunit 62410 "MO E Twin Other"
        {
            procedure Value(): Integer
            begin
                exit(6);
            end;
        }
        """;

    // MO_SPLIT is declared in app.json, so the guarded branch is the one compiled; the file still
    // declares two objects.
    private const string Guarded = """
        codeunit 62411 "MO F Guarded"
        {
            procedure Pick(): Integer
            begin
        #if MO_SPLIT
                exit(11);
        #else
                exit(12);
        #endif
            end;
        }

        codeunit 62412 "MO F Plain"
        {
            procedure Value(): Integer
            begin
                exit(5);
            end;
        }
        """;

    // The symbol below is never declared, so only one object remains and the file is an ordinary
    // single-object file: the unchanged path next to the multi-object ones.
    private const string GuardedAway = """
        #if MO_NEVER
        codeunit 62413 "MO G Hidden"
        {
        }
        #endif

        codeunit 62414 "MO G Visible"
        {
            procedure Value(): Integer
            begin
                exit(14);
            end;
        }
        """;

    private static string Single(int value = 9) => $$"""
        codeunit 62430 "MO Single"
        {
            procedure Value(): Integer
            begin
                exit({{value}});
            end;
        }
        """;

    // One test codeunit per shape: under TestIsolation = Codeunit selecting one test selects its whole
    // codeunit (#5035), so a shared codeunit would hide which shape a rerun came from.
    private static string TestCodeunit(int id, string procName, string vars, string body) => $$"""
        codeunit {{id}} "MO T {{id}}"
        {
            Subtype = Test;

            [Test]
            procedure {{procName}}()
            {{vars}}
            begin
        {{body}}
            end;
        }
        """;

    private static readonly (string File, string Source)[] TestFiles =
    {
        ("TSameKind.Codeunit.al", TestCodeunit(62420, "SameKindTwoCodeunits",
            "var\n    One: Codeunit \"MO A One\";\n    Two: Codeunit \"MO A Two\";",
            "        if One.Value() + Two.Value() <> 3 then\n            Error('SAME-KIND-%1', One.Value() + Two.Value());")),
        ("TCodeunitAndTable.Codeunit.al", TestCodeunit(62421, "CodeunitAndTableInOneFile",
            "var\n    H: Codeunit \"MO B Helper\";\n    R: Record \"MO B Rows\";",
            "        if H.Value() <> 7 then\n            Error('HELPER-%1', H.Value());\n        if R.Doubled(4) <> 8 then\n            Error('DOUBLED-%1', R.Doubled(4));")),
        ("TTableExt.Codeunit.al", TestCodeunit(62422, "TableAndTableExtension",
            "var\n    R: Record \"MO C Base\";",
            "        if R.BaseValue() + R.ExtValue() <> 33 then\n            Error('TABLE-EXT-%1', R.BaseValue() + R.ExtValue());")),
        ("TPageExt.Codeunit.al", TestCodeunit(62423, "PageAndPageExtension",
            "var\n    P: Page \"MO D Page\";",
            "        if P.Value() <> 4 then\n            Error('PAGE-%1', P.Value());")),
        ("TTwin.Codeunit.al", TestCodeunit(62424, "TwoCodeunitsOfOneKind",
            "var\n    A: Codeunit \"MO E Twin\";\n    B: Codeunit \"MO E Twin Other\";",
            "        if A.Value() + B.Value() <> 11 then\n            Error('TWIN-%1', A.Value() + B.Value());")),
        ("TGuarded.Codeunit.al", TestCodeunit(62425, "PreprocessorGuardedObject",
            "var\n    G: Codeunit \"MO F Guarded\";\n    P: Codeunit \"MO F Plain\";",
            "        if G.Pick() + P.Value() <> 16 then\n            Error('GUARDED-%1', G.Pick() + P.Value());")),
        ("TGuardedAway.Codeunit.al", TestCodeunit(62426, "GuardedAwayObjectLeavesOneObject",
            "var\n    V: Codeunit \"MO G Visible\";",
            "        if V.Value() <> 14 then\n            Error('VISIBLE-%1', V.Value());")),
        ("TSingle.Codeunit.al", TestCodeunit(62427, "SingleObjectFile",
            "var\n    S: Codeunit \"MO Single\";",
            "        if S.Value() <> 9 then\n            Error('SINGLE-%1', S.Value());")),
        ("TOwnFile.Codeunit.al", TestCodeunit(62428, "OwnFileOnly", "",
            "        if 2 + 2 <> 4 then\n            Error('OWN-FILE');")),
    };

    private static readonly string[] AllTests =
    {
        "CodeunitAndTableInOneFile", "GuardedAwayObjectLeavesOneObject", "OwnFileOnly",
        "PageAndPageExtension", "PreprocessorGuardedObject", "SameKindTwoCodeunits", "SingleObjectFile",
        "TableAndTableExtension", "TwoCodeunitsOfOneKind",
    };

    private static string Bundle(string prefix, string appIdSuffix)
    {
        var dir = TestScratch.Dir(prefix);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "c5003000-0000-4a11-9111-{{appIdSuffix}}",
          "name": "Multi Object File {{appIdSuffix}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "preprocessorSymbols": [ "MO_SPLIT" ],
          "idRanges": [ { "from": 62400, "to": 62499 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "SameKind.Codeunit.al"), SameKind);
        File.WriteAllText(Path.Combine(dir, "CodeunitAndTable.al"), CodeunitAndTable());
        File.WriteAllText(Path.Combine(dir, "TableAndExtension.al"), TableAndExtension);
        File.WriteAllText(Path.Combine(dir, "PageAndExtension.al"), PageAndExtension);
        File.WriteAllText(Path.Combine(dir, "SameNameShape.al"), SameNameShape);
        File.WriteAllText(Path.Combine(dir, "Guarded.al"), Guarded);
        File.WriteAllText(Path.Combine(dir, "GuardedAway.al"), GuardedAway);
        File.WriteAllText(Path.Combine(dir, "Single.Codeunit.al"), Single());
        foreach (var (file, source) in TestFiles) File.WriteAllText(Path.Combine(dir, file), source);
        return dir;
    }

    private sealed record Observed(
        Dictionary<string, (string Status, string Line)> Tests, bool ForcedFull, string Reason, string Raw)
    {
        public string[] Ran => Tests.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
    }

    private static async Task<Observed> Send(CliServer server, params string[] bundles)
    {
        var request = new Dictionary<string, object>
        {
            ["command"] = "runTests",
            ["sourcePaths"] = bundles,
            ["packagePaths"] = Array.Empty<string>(),
            ["affectedOnly"] = true,
        };
        var stderrMark = server.StdErrMark;
        var lines = await server.SendRequestStreamingAsync(JsonSerializer.Serialize(request), TimeSpan.FromSeconds(180));
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        var raw = string.Join(" | ", lines) + "\n--- stderr ---\n" + server.StdErrSince(stderrMark);
        var forced = false;
        var reason = "";
        if (summary.TryGetProperty("selection", out var selection))
        {
            forced = selection.GetProperty("forcedFull").GetBoolean();
            if (selection.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
                reason = r.GetString()!;
        }
        var tests = events.ToDictionary(
            e => e.GetProperty("name").GetString()!.Split('.').Last(),
            e => (e.GetProperty("status").GetString()!, e.GetRawText()), StringComparer.Ordinal);
        return new Observed(tests, forced, reason, raw);
    }

    private static void AssertRan(Observed o, string step, params string[] expected)
        => Assert.True(expected.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(o.Ran),
            $"{step}: ran [{string.Join(", ", o.Ran)}], expected [{string.Join(", ", expected)}]:\n{o.Raw}");

    /// <summary>
    /// The issue's own measurement, over every shape of a file declaring several objects: with
    /// nothing changed the second request runs nothing, and an edit to a single-object file still
    /// narrows to the one test that covers it.
    /// </summary>
    [SkippableFact]
    public async Task UnchangedRequest_RunsNothing_ForEveryMultiObjectShape()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-multi-object", "000000000001");
        var server = await _fixture.GetAsync(new[] { "--no-cache" });

        var baseline = await Send(server, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        AssertRan(baseline, "baseline", AllTests);
        Assert.True(baseline.Tests.Values.All(t => t.Status == "pass"), baseline.Raw);

        var unchanged = await Send(server, bundle);
        Assert.False(unchanged.ForcedFull, unchanged.Raw);
        AssertRan(unchanged, "unchanged");

        // A single-object file's edit selects only the test that ran its code, so the multi-object
        // tests are now skipped because they are known, not because they are unknown and rerun.
        File.WriteAllText(Path.Combine(bundle, "Single.Codeunit.al"), Single(10));
        var single = await Send(server, bundle);
        Assert.False(single.ForcedFull, single.Raw);
        AssertRan(single, "single-object edit", "SingleObjectFile");
        Assert.Equal("fail", single.Tests["SingleObjectFile"].Status);
        Assert.Contains("SINGLE-10", single.Tests["SingleObjectFile"].Line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other direction: a test that ran code of a multi-object file is selected when that file
    /// changes, and it runs the new code. The change model tracks one object per file, so such an
    /// edit runs everything (a table-only edit included), and the next request is narrow again.
    /// </summary>
    [SkippableFact]
    public async Task EditingAMultiObjectFile_RunsEverything_ThenNarrowsAgain()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-multi-object-edit", "000000000002");
        var server = await _fixture.GetAsync(new[] { "--no-cache" });
        Assert.True((await Send(server, bundle)).ForcedFull);
        AssertRan(await Send(server, bundle), "unchanged");

        var file = Path.Combine(bundle, "CodeunitAndTable.al");
        File.WriteAllText(file, CodeunitAndTable(8));
        var edited = await Send(server, bundle);
        Assert.True(edited.ForcedFull, edited.Raw);
        Assert.Contains("CodeunitAndTable.al", edited.Reason, StringComparison.Ordinal);
        AssertRan(edited, "helper edit", AllTests);
        Assert.Equal("fail", edited.Tests["CodeunitAndTableInOneFile"].Status);
        Assert.Contains("HELPER-8", edited.Tests["CodeunitAndTableInOneFile"].Line, StringComparison.Ordinal);

        // Put the helper back: the table is untouched, yet the file changed, so everything runs.
        File.WriteAllText(file, CodeunitAndTable());
        var restored = await Send(server, bundle);
        Assert.True(restored.ForcedFull, restored.Raw);
        AssertRan(restored, "restored", AllTests);
        Assert.Equal("pass", restored.Tests["CodeunitAndTableInOneFile"].Status);

        AssertRan(await Send(server, bundle), "unchanged again");
    }
}
