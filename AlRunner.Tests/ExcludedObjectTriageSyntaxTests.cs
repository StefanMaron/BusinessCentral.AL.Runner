// ExcludedObjectTriageSyntaxTests — #5557. The triage identified what an excluded object is with
// line-anchored regexes over the file text, so a header or a `Subtype = Test;` inside a comment
// counted. The first refused a droppable test codeunit (and with it the whole recovered module);
// the second, the unsafe direction, let a codeunit that is not a test through as droppable.
//
// Runner-specific: the triage is the runner's own decision. In-process, BC's parser only.
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class ExcludedObjectTriageSyntaxTests : IDisposable
{
    private readonly BcEngineFixture _engine;
    private readonly string _dir = TestScratch.Dir("al-runner-excl-triage-syntax-" + Guid.NewGuid().ToString("N")[..8]);

    public ExcludedObjectTriageSyntaxTests(BcEngineFixture engine)
    {
        _engine = engine;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void RequireEngine() =>
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    private ProgramSupport.ExcludedObjectVerdict TriageOf(string text)
    {
        var path = Path.Combine(_dir, "Broken.Codeunit.al");
        File.WriteAllText(path, text);
        return Assert.Single(ProgramSupport.ExcludedObjectTriage.Triage(
            new[] { new TddExcludedObjectDetail(path, "Broken", new[] { "error AL0185: DotNet 'Missing' is missing" }) },
            new[] { path }));
    }

    private const string TestCodeunit = """
        codeunit 135209 "Azure Key Vault Module Test"
        {
            Subtype = Test;

            [Test]
            procedure TestOne()
            begin
            end;
        }
        """;

    [SkippableFact]
    public void CommentedOutHeader_DoesNotBlockADroppableTestCodeunit()
    {
        RequireEngine();
        var v = TriageOf("/*\ntable 70002 \"Old\"\n*/\n" + TestCodeunit);

        Assert.True(v.Droppable, v.Reason);
    }

    [SkippableFact]
    public void ARealNonCodeunitDeclaration_StillBlocks_NamingIt()
    {
        RequireEngine();
        var v = TriageOf("table 70002 \"Real\"\n{\n}\n" + TestCodeunit);

        Assert.False(v.Droppable);
        Assert.Contains("table 70002 \"Real\"", v.Reason, StringComparison.Ordinal);
    }

    /// <summary>The branch the COMPILE took is the branch read: with the app's own manifest defining the
    /// symbol, the guarded header is a real declaration and blocks.</summary>
    [SkippableFact]
    public void AnActiveBranchHeader_Blocks_WhenTheManifestDefinesTheSymbol()
    {
        RequireEngine();
        var appJson = Path.Combine(_dir, "app.json");
        File.WriteAllText(appJson, "{ \"id\": \"b5557000-0000-4000-8000-000000005557\", \"name\": \"N\", \"publisher\": \"P\", "
            + "\"version\": \"1.0.0.0\", \"dependencies\": [], \"runtime\": \"14.0\", \"preprocessorSymbols\": [ \"TRIAGE_SYM_5557\" ] }");
        var path = Path.Combine(_dir, "Broken.Codeunit.al");
        File.WriteAllText(path, "#if TRIAGE_SYM_5557\ntable 70002 \"Guarded\"\n{\n}\n#endif\n" + TestCodeunit);

        var v = Assert.Single(ProgramSupport.ExcludedObjectTriage.Triage(
            new[] { new TddExcludedObjectDetail(path, "Broken", new[] { "error" }, appJson) }, new[] { path }));

        Assert.False(v.Droppable);
        Assert.Contains("table 70002 \"Guarded\"", v.Reason, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void AnInactiveBranchHeader_DoesNotBlock()
    {
        RequireEngine();
        var v = TriageOf("#if CENSUS_NEVER_DEFINED_5557\ntable 70002 \"Inactive\"\n{\n}\n#endif\n" + TestCodeunit);

        Assert.True(v.Droppable, v.Reason);
    }

    /// <summary>The unsafe direction: a `Subtype = Test;` that only a comment holds must not make a
    /// library codeunit look like a test.</summary>
    [SkippableFact]
    public void ACommentedSubtypeTest_DoesNotMakeANonTestCodeunitDroppable()
    {
        RequireEngine();
        var v = TriageOf("""
            codeunit 135210 "Some Library"
            {
                /*
                Subtype = Test;
                */
                procedure Helper()
                begin
                end;
            }
            """);

        Assert.False(v.Droppable);
        Assert.Contains("without Subtype = Test", v.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file BC's parser cannot read is refused, never droppable: it fails CLOSED. Driven through
    /// the parser parameter, with a file that WOULD be droppable if the null were read as nothing
    /// to block it.
    /// </summary>
    [Fact]
    public void AFileTheParserCannotRead_IsRefused_NotDroppable()
    {
        var path = Path.Combine(_dir, "Unreadable.Codeunit.al");
        File.WriteAllText(path, TestCodeunit);

        var v = Assert.Single(ProgramSupport.ExcludedObjectTriage.Triage(
            new[] { new TddExcludedObjectDetail(path, "Unreadable", new[] { "error" }) },
            new[] { path }, parse: (_, _, _) => null));

        Assert.False(v.Droppable);
        Assert.Contains("parser could not read", v.Reason, StringComparison.Ordinal);
    }

    /// <summary>Control for the test above: the same file, parsed, IS droppable.</summary>
    [Fact]
    public void TheSameFileParsed_IsDroppable()
    {
        var path = Path.Combine(_dir, "Readable.Codeunit.al");
        File.WriteAllText(path, TestCodeunit);

        var v = Assert.Single(ProgramSupport.ExcludedObjectTriage.Triage(
            new[] { new TddExcludedObjectDetail(path, "Readable", new[] { "error" }) },
            new[] { path },
            parse: (_, _, _) => new[] { new AlRunner.Infrastructure.AlDeclaredObject("codeunit", 135209, "T", true) }));

        Assert.True(v.Droppable, v.Reason);
    }
}
