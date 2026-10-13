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
}
