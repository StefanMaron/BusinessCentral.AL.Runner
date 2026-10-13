// #5552 (found while re-measuring #3521): the PARTIAL-EMIT-DROP census counted object
// declarations with a line-anchored regex over the file text, so a commented-out object whose
// header starts a line was counted as declared, "dropped" by the compile, and the whole bundle
// refused with exit 3.
//
// The census is now BC's syntax tree, so the guard cannot be provoked with a ghost declaration
// any more. AL_RUNNER_TEST_DROP_EMITTED_OBJECT removes one emitted source by name before the
// guard runs, which is the state the guard exists to catch; the firing tests use it.
//
// Runner-specific: real BC has one parse. No "application" dependency
// (.claude/rules/no-base-app-in-csharp-tests.md); each test raises its own Error().
using System.Diagnostics;
using System.Text;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class PartialEmitDropCensusTests : IDisposable
{
    private readonly BcEngineFixture _engine;

    public PartialEmitDropCensusTests(BcEngineFixture engine) => _engine = engine;

    private void RequireEngine() =>
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root = TestScratch.Dir("al-runner-partial-emit-drop-census");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static (string output, int exit) RunRunner(string bundle, string? dropEmittedObject = null)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        // The guard runs on an emit; a warm al-out cache HIT skips it, and the seam with it.
        args.Append(" --no-cache");
        args.Append(" \"").Append(bundle).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        if (dropEmittedObject != null) psi.Environment["AL_RUNNER_TEST_DROP_EMITTED_OBJECT"] = dropEmittedObject;
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private string Bundle(string name, string probeName)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
            {
              "id": "b5552000-0000-4000-8000-000000005552",
              "name": "EmitDropCensus5552",
              "publisher": "Repro5552",
              "version": "1.0.0.0",
              "dependencies": [],
              "idRanges": [ { "from": 65520, "to": 65529 } ],
              "runtime": "14.0"
            }
            """);
        File.WriteAllText(Path.Combine(dir, "Probe.Codeunit.al"), $$"""
            codeunit 65520 "{{probeName}}"
            {
                Subtype = Test;

                [Test]
                procedure Runs()
                begin
                end;
            }
            """);
        return dir;
    }

    [SkippableFact]
    public void CommentedOutObject_AtLineStart_DoesNotFailTheBundle()
    {
        TestArtifacts.SkipIfMissing();
        var dir = Bundle("commented", "Census Commented Probe");
        // The header starts the line, inside a block comment: not a declaration.
        File.WriteAllText(Path.Combine(dir, "Old.al"), """
            /*
            codeunit 65521 "Old Experiment"
            {
            }
            */
            """);

        var (output, exit) = RunRunner(dir);

        Assert.DoesNotContain("PARTIAL-EMIT-DROP", output);
        Assert.Contains("PASS  Codeunit65520.Runs", output);
        Assert.Equal(0, exit);
    }

    /// <summary>The seam itself, and the control for every test below: the same bundle is clean
    /// without it and refused with it, so a refusal is the dropped object's and nothing else's.</summary>
    [SkippableFact]
    public void ForcedDrop_OfADeclaredObject_StillFiresTheGuard()
    {
        TestArtifacts.SkipIfMissing();
        var dir = Bundle("forced", "Census Forced Probe");
        File.WriteAllText(Path.Combine(dir, "Victim.al"), """
            codeunit 65521 "Census Victim"
            {
            }
            """);

        var (clean, cleanExit) = RunRunner(dir);
        var (output, exit) = RunRunner(dir, dropEmittedObject: "Census Victim");

        Assert.Equal(0, cleanExit);
        Assert.DoesNotContain("PARTIAL-EMIT-DROP", clean);
        Assert.Contains("PARTIAL-EMIT-DROP", output);
        Assert.Contains("2 object(s) declared, only 1 emitted", output);
        Assert.Contains("Declared: [Census Forced Probe, Census Victim]", output.Replace("Victim.al", ""));
        Assert.Equal(3, exit);
        // #5563: nothing was dropped by the compile, so no diagnostic was recorded and the message still says so.
        Assert.Contains("0 AL diagnostics explain the gap", output);
        Assert.DoesNotContain("were recorded for the dropped file(s)", output);
    }

    /// <summary>
    /// What the regex could not see and the tree can: a capitalised keyword (AL keywords are
    /// case-insensitive) and a header with its body on the same line. Each is an object the
    /// compile emitted and the seam then hides; a count that missed either would stay under the
    /// accounted-for total and the guard would not fire.
    /// </summary>
    [SkippableTheory]
    [InlineData("Codeunit 65521 \"Census Victim\"\n{\n}\n")]
    [InlineData("codeunit 65521 \"Census Victim\" { }\n")]
    [InlineData("codeunit 65521 \"Census Victim\" // trailing comment\n{\n}\n")]
    [InlineData("codeunit /* c */ 65521 \"Census Victim\"\n{\n}\n")]
    public void ForcedDrop_OfAnObjectTheRegexCouldNotSee_FiresTheGuard(string victim)
    {
        TestArtifacts.SkipIfMissing();
        var dir = Bundle("unseen", "Census Unseen Probe");
        File.WriteAllText(Path.Combine(dir, "Victim.al"), victim);

        var (output, _) = RunRunner(dir, dropEmittedObject: "Census Victim");

        Assert.Contains("PARTIAL-EMIT-DROP", output);
        Assert.Contains("Census Victim", output);
    }

    /// <summary>
    /// A drop fires, and the objects the guard LISTS as declared are the real ones: a header in a
    /// block comment and one in an inactive branch are in the file and not in the list.
    /// </summary>
    [SkippableFact]
    public void ForcedDrop_ListsOnlyRealDeclarations()
    {
        TestArtifacts.SkipIfMissing();
        var dir = Bundle("listed", "Census Listed Probe");
        File.WriteAllText(Path.Combine(dir, "Victim.al"), """
            codeunit 65521 "Census Victim"
            {
            }
            """);
        File.WriteAllText(Path.Combine(dir, "Ghosts.al"), """
            /*
            codeunit 65522 "Census Commented Ghost"
            */
            #if CENSUS_NEVER_DEFINED_5552
            codeunit 65523 "Census Inactive Ghost"
            {
            }
            #endif
            """);

        var (output, _) = RunRunner(dir, dropEmittedObject: "Census Victim");

        var declared = output.Split('\n').First(l => l.Contains("Declared: ["));
        Assert.Contains("Census Victim", declared);
        Assert.DoesNotContain("Census Commented Ghost", declared);
        Assert.DoesNotContain("Census Inactive Ghost", declared);
    }

    // ── DeclaredObjectNames, in-process ──────────────────────────────────────────────────

    private string WriteApp(string name, string? symbol = null)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var symbols = symbol == null ? "" : $", \"preprocessorSymbols\": [ \"{symbol}\" ]";
        File.WriteAllText(Path.Combine(dir, "app.json"),
            "{ \"id\": \"b5552000-0000-4000-8000-000000005553\", \"name\": \"N\", \"publisher\": \"P\", "
            + "\"version\": \"1.0.0.0\", \"dependencies\": [], \"runtime\": \"14.0\"" + symbols + " }");
        return Path.Combine(dir, "app.json");
    }

    [SkippableFact]
    public void DeclaredObjectNames_CommentsStringsAndInactiveBranches_AreNotDeclarations()
    {
        RequireEngine();
        var appJson = WriteApp("names");
        const string text = """
            // codeunit 65530 "Line Comment Ghost"
            /*
            codeunit 65531 "Block Comment Ghost"
            */
            #if CENSUS_NEVER_DEFINED_5552
            codeunit 65532 "Inactive Ghost"
            {
            }
            #endif
            codeunit 65533 "Real One"
            {
                procedure P()
                begin
                    Message('codeunit 65534 "String Ghost"');
                end;
            }
            """;

        var names = AlMemberSyntaxIndex.DeclaredObjectNames(text, "Names.al", appJson);

        Assert.Equal(new[] { "Real One" }, names);
    }

    [SkippableFact]
    public void DeclaredObjectNames_ManifestSymbolSelectsTheBranch_BothWays()
    {
        RequireEngine();
        const string text = """
            #if CENSUS_SYMBOL_5552
            codeunit 65540 "Symbol On"
            {
            }
            #else
            codeunit 65541 "Symbol Off"
            {
            }
            #endif
            """;

        Assert.Equal(new[] { "Symbol On" },
            AlMemberSyntaxIndex.DeclaredObjectNames(text, "S.al", WriteApp("on", "CENSUS_SYMBOL_5552")));
        Assert.Equal(new[] { "Symbol Off" },
            AlMemberSyntaxIndex.DeclaredObjectNames(text, "S.al", WriteApp("off")));
    }

    [SkippableFact]
    public void DeclaredObjectNames_KindSet_IsTheOneTheRegexCounted()
    {
        RequireEngine();
        var appJson = WriteApp("kinds");
        const string text = """
            interface "Kind Iface"
            {
                procedure Ping(): Integer;
            }
            table 65550 "Kind Table"
            {
                fields { field(1; "Code"; Code[20]) { } }
            }
            tableextension 65551 "Kind TExt" extends "Kind Table"
            {
            }
            enumextension 65552 "Kind EExt" extends "Kind Enum"
            {
            }
            enum 65553 "Kind Enum"
            {
            }
            """;

        var names = AlMemberSyntaxIndex.DeclaredObjectNames(text, "K.al", appJson);

        // Interface and enumextension carry no emitted source; the extension on one line counts.
        Assert.Equal(new[] { "Kind Table", "Kind TExt", "Kind Enum" }, names);
    }

    // ── fail closed when a file cannot be measured ───────────────────────────────────────

    private string TextFile(string name, string text)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>
    /// A file the parser cannot read must make the guard fire rather than hide a drop: its
    /// possible declarations count as declared. Driven in-process through the parser parameter.
    /// </summary>
    [Fact]
    public void DeclaredBeyond_AFileTheParserCannotRead_CountsConservatively_AndIsNamed()
    {
        var victim = TextFile("Unreadable5552.al", "codeunit 65521 \"Victim\"\n{\n}\n");
        var log = new StringWriter();

        var names = ProgramSupport.EmitDropCensus.DeclaredBeyond(
            new[] { victim }, null, accountedFor: 0, log, parse: (_, _, _) => null);

        Assert.Single(names);
        Assert.Contains("Unreadable5552.al", log.ToString());
        Assert.Contains("[emit-census]", log.ToString());
    }

    [Fact]
    public void DeclaredBeyond_TheSameFileParsed_IsCountedByTheTree_NotTheBound()
    {
        var file = TextFile("Parsed5552.al", "codeunit 65521 \"Victim\"\n{\n}\n// codeunit table page\n");
        var log = new StringWriter();

        var names = ProgramSupport.EmitDropCensus.DeclaredBeyond(
            new[] { file }, null, accountedFor: 0, log, parse: (_, _, _) => new[] { "Victim" });

        Assert.Equal(new[] { "Victim" }, names);
        Assert.Equal("", log.ToString());
    }

    [Fact]
    public void DeclaredBeyond_BoundWithinAccountedFor_ParsesNothing()
    {
        var file = TextFile("Quiet5552.al", "codeunit 65521 \"Victim\"\n{\n}\n");
        var parsed = 0;

        var names = ProgramSupport.EmitDropCensus.DeclaredBeyond(
            new[] { file }, null, accountedFor: 1, new StringWriter(), parse: (_, _, _) => { parsed++; return new[] { "x" }; });

        Assert.Empty(names);
        Assert.Equal(0, parsed);
    }

    // ── the one rule the CLI and --server both ask (#5559) ───────────────────────────────

    [Fact]
    public void FindPartialDrop_MoreDeclaredThanEmittedPlusAccountedFor_NamesBothSides()
    {
        var file = TextFile("TwoDeclared5559.al", "codeunit 65521 \"A\"\n{\n}\ncodeunit 65522 \"B\"\n{\n}\n");

        var drop = ProgramSupport.EmitDropCensus.FindPartialDrop(
            new[] { file }, null, new[] { "A" }, accountedFor: 0, new StringWriter(),
            parse: (_, _, _) => new[] { "A", "B" });

        Assert.NotNull(drop);
        Assert.Equal(new[] { "A", "B" }, drop!.Declared);
        Assert.Equal(new[] { "A" }, drop.Emitted);
        Assert.Equal(
            "<bundled>: PARTIAL-EMIT-DROP for M: 2 object(s) declared, only 1 emitted, 0 AL diagnostics explain the gap. "
            + "Declared: [A, B]. Emitted: [A].", drop.Error("<bundled>: ", "M"));
        Assert.Equal(
            "  [server] PARTIAL-EMIT-DROP \u2014 M: 2 declared vs 1 emitted, no diagnostics. Declared: [A, B]. Emitted: [A].",
            drop.ConsoleLine("  [server] ", "M"));
    }

    private static readonly string[] RecordedDiagnostics =
    {
        "SourceFile(Both.al@13:18): error AL0185: Codeunit 'Does Not Exist' is missing",
        "SourceFile(Both.al@4:18): error AL0185: Page 'Gone' is missing",
    };

    /// <summary>
    /// #5563: the diagnostics the retry loop recorded for the dropped file are in every rendering of the report,
    /// and the count it states is the number it holds, so it can never say "0 AL diagnostics" beside a list.
    /// </summary>
    [Fact]
    public void FindPartialDrop_CarriesTheDroppedFilesDiagnostics_IntoEveryRendering()
    {
        var file = TextFile("TwoDeclared5563.al", "codeunit 65521 \"A\"\n{\n}\ncodeunit 65522 \"B\"\n{\n}\n");

        var drop = ProgramSupport.EmitDropCensus.FindPartialDrop(
            new[] { file }, null, new[] { "A" }, accountedFor: 0, new StringWriter(), RecordedDiagnostics,
            parse: (_, _, _) => new[] { "A", "B" });

        Assert.NotNull(drop);
        // CLI suite error: the count and the first diagnostic, on one line like AL-DIAGNOSTIC-FAIL's entry.
        Assert.Equal(
            "<bundled>: PARTIAL-EMIT-DROP for M: 2 object(s) declared, only 1 emitted, 2 AL diagnostic(s) were recorded "
            + "for the dropped file(s) (first: SourceFile(Both.al@13:18): error AL0185: Codeunit 'Does Not Exist' is missing). "
            + "Declared: [A, B]. Emitted: [A].", drop!.Error("<bundled>: ", "M"));
        // --server: every diagnostic inside the one entry, the first is not the only one.
        Assert.Equal(
            "PARTIAL-EMIT-DROP for M: 2 object(s) declared, only 1 emitted, 2 AL diagnostic(s) were recorded "
            + "for the dropped file(s). Declared: [A, B]. Emitted: [A]."
            + "\n" + RecordedDiagnostics[0] + "\n" + RecordedDiagnostics[1],
            drop.Error("", "M", listEveryDiagnostic: true));
        Assert.Equal(
            "  [server] PARTIAL-EMIT-DROP \u2014 M: 2 declared vs 1 emitted, 2 AL diagnostic(s) were recorded for the dropped file(s). "
            + "Declared: [A, B]. Emitted: [A].\n  " + RecordedDiagnostics[0] + "\n  " + RecordedDiagnostics[1],
            drop.ConsoleLine("  [server] ", "M"));
    }

    /// <summary>Diagnostics alone are not a drop: a gap the compile accounted for stays no drop, with or without them.</summary>
    [Fact]
    public void FindPartialDrop_DiagnosticsDoNotCreateADrop_AndNoneKeepsTheZeroWording()
    {
        var file = TextFile("Accounted5563.al", "codeunit 65521 \"A\"\n{\n}\ncodeunit 65522 \"B\"\n{\n}\n");
        string[] parse(string _, string __, string? ___) => new[] { "A", "B" };

        Assert.Null(ProgramSupport.EmitDropCensus.FindPartialDrop(
            new[] { file }, null, new[] { "A" }, accountedFor: 1, new StringWriter(), RecordedDiagnostics, parse: parse));

        var none = ProgramSupport.EmitDropCensus.FindPartialDrop(
            new[] { file }, null, new[] { "A" }, accountedFor: 0, new StringWriter(), Array.Empty<string>(), parse: parse)!;
        var absent = ProgramSupport.EmitDropCensus.FindPartialDrop(
            new[] { file }, null, new[] { "A" }, accountedFor: 0, new StringWriter(), null, parse: parse)!;
        Assert.Equal(none.Error("p: ", "M"), absent.Error("p: ", "M"));
        Assert.Contains("0 AL diagnostics explain the gap", none.Error("p: ", "M", listEveryDiagnostic: true));
        Assert.DoesNotContain("were recorded", none.Error("", "M"));
        Assert.DoesNotContain("\n", none.Error("", "M", listEveryDiagnostic: true));
    }

    /// <summary>The accounted-for objects are the ones the compile reported dropping; the guard fires only beyond them.</summary>
    [Fact]
    public void FindPartialDrop_TheGapIsExactlyWhatWasReportedDropped_IsNotADrop()
    {
        var file = TextFile("Accounted5559.al", "codeunit 65521 \"A\"\n{\n}\ncodeunit 65522 \"B\"\n{\n}\n");

        Assert.Null(ProgramSupport.EmitDropCensus.FindPartialDrop(
            new[] { file }, null, new[] { "A" }, accountedFor: 1, new StringWriter(),
            parse: (_, _, _) => new[] { "A", "B" }));
        Assert.NotNull(ProgramSupport.EmitDropCensus.FindPartialDrop(
            new[] { file }, null, new[] { "A" }, accountedFor: 0, new StringWriter(),
            parse: (_, _, _) => new[] { "A", "B" }));
    }

    /// <summary>A directory in the paths is read for its .al files, and a file the parser cannot read still fires.</summary>
    [Fact]
    public void FindPartialDrop_ADirectoryOfPaths_IsScanned_AndAnUnreadableFileFailsClosed()
    {
        var dir = Path.Combine(_root, "Dir5559");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "X.al"), "codeunit 65521 \"X\"\n{\n}\n");
        File.WriteAllText(Path.Combine(dir, "ignored.txt"), "codeunit 65522 \"Y\"\n");

        var drop = ProgramSupport.EmitDropCensus.FindPartialDrop(
            new[] { dir }, null, Array.Empty<string>(), accountedFor: 0, new StringWriter(), parse: (_, _, _) => null);

        Assert.NotNull(drop);
        Assert.Single(drop!.Declared);
    }

    /// <summary>
    /// Each of the ten counted kinds, capitalised and on one line, is a declaration to the tree.
    /// Removing a kind from the filter drops exactly its row.
    /// </summary>
    [SkippableTheory]
    [InlineData("Table 65550 \"K Table\" { fields { field(1; \"Code\"; Code[20]) { } } }", "K Table")]
    [InlineData("Codeunit 65551 \"K Codeunit\" { }", "K Codeunit")]
    [InlineData("Page 65552 \"K Page\" { }", "K Page")]
    [InlineData("Report 65553 \"K Report\" { }", "K Report")]
    [InlineData("Query 65554 \"K Query\" { }", "K Query")]
    [InlineData("Enum 65555 \"K Enum\" { }", "K Enum")]
    [InlineData("XmlPort 65556 \"K Xml\" { }", "K Xml")]
    [InlineData("TableExtension 65557 \"K TExt\" extends \"K Table\" { }", "K TExt")]
    [InlineData("PageExtension 65558 \"K PExt\" extends \"K Page\" { }", "K PExt")]
    [InlineData("PermissionSet 65559 \"K Perm\" { Assignable = true; }", "K Perm")]
    public void DeclaredObjectNames_EachCountedKind_OnOneLine_IsDeclared(string text, string name)
    {
        RequireEngine();

        Assert.Equal(new[] { name }, AlMemberSyntaxIndex.DeclaredObjectNames(text, "K.al", WriteApp("kind-" + name.Replace(' ', '-'))));
    }
}
