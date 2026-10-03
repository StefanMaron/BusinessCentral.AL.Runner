// CodeCoverageLogSiblingSourceServedTextTests — #5250, in-process: the text
// CodeCoveragePatches.SourceCodeLinesFor serves for an object (what the Code Coverage 2000000049
// rows are built from) when a same-app-id sibling source folder is registered beside the compiled
// one. The CLI layout is in CodeCoverageLogSiblingSourceTests; this class pins the pieces that
// layout cannot isolate: which registration order, a dir marked as compiled AFTER it was already
// registered (a source impl registers its own dir first), the memo key, and the reload reset.
//
// BcEngineCollection, because AddSourceDirs parses with BC's parser in-process; the class calls
// ResetForReload, which ParserStaticsIsolationGuardTests admits for this collection.

using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class CodeCoverageLogSiblingSourceServedTextTests : IDisposable
{
    private readonly BcEngineFixture _engine;
    private readonly string _root = TestScratch.Dir("al-runner-codecoverage-log-sibling-served");

    private string Bundle => Path.Combine(_root, "bundle");
    private string Source => Path.Combine(_root, "src");

    public CodeCoverageLogSiblingSourceServedTextTests(BcEngineFixture engine) => _engine = engine;

    public void Dispose()
    {
        RecordPatches.ResetForReload();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void RequireEngine() =>
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    // Cov Sib A is one line longer in the compiled text, so Cov Sib B sits one line further down
    // there, and its body differs (exit(1) against exit(2)).
    private static readonly string[] CompiledLines =
    {
        "codeunit 63700 \"Cov Sib A\"", "{", "    procedure A()", "    begin", "        exit;", "        exit;", "    end;", "}",
        "",
        "codeunit 63701 \"Cov Sib B\"", "{", "    procedure B(): Integer", "    begin", "        exit(1);", "    end;", "}",
    };

    private static readonly string[] SiblingLines =
    {
        "codeunit 63700 \"Cov Sib A\"", "{", "    procedure A()", "    begin", "        exit;", "    end;", "}",
        "",
        "codeunit 63701 \"Cov Sib B\"", "{", "    procedure B(): Integer", "    begin", "        exit(2);", "    end;", "}",
    };

    private static readonly string[] SiblingOnlyLines =
    {
        "codeunit 63702 \"Cov Sib Only\"", "{", "    procedure P()", "    begin", "    end;", "}",
    };

    private static readonly string[] CompiledObjectB = CompiledLines.Skip(8).ToArray();
    private static readonly string[] SiblingObjectB = SiblingLines.Skip(7).ToArray();

    private void WriteFolders()
    {
        Directory.CreateDirectory(Bundle);
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Bundle, "Pair.Codeunit.al"), string.Join("\n", CompiledLines) + "\n");
        File.WriteAllText(Path.Combine(Source, "Pair.Codeunit.al"), string.Join("\n", SiblingLines) + "\n");
        File.WriteAllText(Path.Combine(Source, "Only.Codeunit.al"), string.Join("\n", SiblingOnlyLines) + "\n");
    }

    private static IReadOnlyList<string> Served(int id)
        => CodeCoveragePatches.SourceCodeLinesFor(new { ObjectType = "Codeunit", ObjectNumber = id }).SourceCodeLines;

    // The registration order of the CLI layout: the source impl registers bundle/ first, then the
    // sibling, then the suite loop marks bundle/ as compiled (a no-op registration, a real mark).
    private void RegisterAsTheCliDoes()
    {
        RecordPatches.AddSourceDirs(new[] { Bundle });
        RecordPatches.AddSourceDirs(new[] { Source });
        RecordPatches.AddExecutionSourceDirs(new[] { (Bundle, (string?)null) });
    }

    [SkippableFact]
    public void ASiblingFolder_DoesNotReplace_TheCompiledText_OfAnObjectBothDeclare()
    {
        RequireEngine();
        RecordPatches.ResetForReload();
        WriteFolders();
        RegisterAsTheCliDoes();

        Assert.Equal(CompiledObjectB, Served(63701));
        // The compiled file's text, not the sibling's.
        Assert.DoesNotContain("        exit(2);", Served(63701));
    }

    // The other direction: the sibling is not blanket-ignored, or a source dependency compiled
    // from a sibling folder (#3965) would serve nothing for its own objects.
    [SkippableFact]
    public void ASiblingFolder_StillSupplies_AnObjectNoCompiledFolderDeclares()
    {
        RequireEngine();
        RecordPatches.ResetForReload();
        WriteFolders();
        RegisterAsTheCliDoes();

        Assert.Equal(SiblingOnlyLines, Served(63702));
    }

    // Nothing marked as compiled: the registered dirs are a plain list and the last one wins, as
    // before. Pins that the mark is what decides, and that it can arrive after the registration
    // with no reload in between (the memo key carries it).
    [SkippableFact]
    public void MarkingTheCompiledFolder_AfterTheMapWasServed_ChangesTheServedText()
    {
        RequireEngine();
        RecordPatches.ResetForReload();
        WriteFolders();
        RecordPatches.AddSourceDirs(new[] { Bundle });
        RecordPatches.AddSourceDirs(new[] { Source });
        Assert.Equal(SiblingObjectB, Served(63701));   // last registered wins

        RecordPatches.AddExecutionSourceDirs(new[] { (Bundle, (string?)null) });

        Assert.Equal(CompiledObjectB, Served(63701));
    }

    [SkippableFact]
    public void ResetForReload_ForgetsWhichFoldersWereCompiled()
    {
        RequireEngine();
        RecordPatches.ResetForReload();
        WriteFolders();
        RegisterAsTheCliDoes();
        Assert.Equal(new[] { Bundle }, RecordPatches.RegisteredExecutionSourceDirs());

        RecordPatches.ResetForReload();

        Assert.Empty(RecordPatches.RegisteredExecutionSourceDirs());
        // The next cycle registers the same dirs again WITHOUT marking one: a carried-over mark
        // would keep serving the compiled text for a folder this cycle did not compile.
        RecordPatches.AddSourceDirs(new[] { Bundle });
        RecordPatches.AddSourceDirs(new[] { Source });
        Assert.Equal(SiblingObjectB, Served(63701));
    }
}
