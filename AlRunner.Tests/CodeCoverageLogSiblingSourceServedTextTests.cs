// CodeCoverageLogSiblingSourceServedTextTests — #5250, in-process: the text
// CodeCoveragePatches.SourceCodeLinesFor serves for an object (what the Code Coverage 2000000049
// rows are built from) when a same-app-id sibling source folder is registered beside the compiled
// one. The CLI layout is in CodeCoverageLogSiblingSourceTests; this class pins the pieces that
// layout cannot isolate: which registration order, a dir marked as compiled AFTER it was already
// registered (a source impl registers its own dir first), the memo key, and the reload reset.
//
// BcEngineCollection, because AddSourceDirs parses with BC's parser in-process; the class calls
// ResetForReload, which ParserStaticsIsolationGuardTests admits for this collection.

using AlRunner.Infrastructure;
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
        RecordPatches.ResetForReload();   // also forgets the package a fact registered
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

    // AddSourceDirs de-dups on the dir ignoring case and skips one that does not exist; the mark
    // follows it: it names the spelling in the registry (the one Build compares roots by), and
    // never a dir that was not registered. (#5257 review: the path canonicalization this replaces
    // was unpinned and could not change a served text.)
    [SkippableFact]
    public void TheMark_IsTheRegisteredSpelling_AndNeverADirThatWasNotRegistered()
    {
        RequireEngine();
        RecordPatches.ResetForReload();
        WriteFolders();
        RecordPatches.AddSourceDirs(new[] { Bundle });

        RecordPatches.AddExecutionSourceDirs(new[]
        {
            (Bundle.ToUpperInvariant(), (string?)null),                 // the registered dir, spelled differently
            (Path.Combine(_root, "never-written"), (string?)null),      // not a directory: never registered
        });

        Assert.Equal(new[] { Bundle }, RecordPatches.RegisteredExecutionSourceDirs());
        Assert.Equal(new[] { Bundle }, RecordPatches.RegisteredSourceDirs());
    }

    // ---- #5259: two UNMARKED folders (the source-dependency pre-pass registers both and marks
    // neither); the package the loader ran is what says which one's text is the compiled one. ----

    private static readonly Guid SharedAppId = Guid.Parse("c9a37e51-6d24-4b83-a15f-8e2760d4bb31");

    private void WriteSharedAppFolders(string firstText, string secondText, out string first, out string second)
    {
        first = Path.Combine(_root, "first");
        second = Path.Combine(_root, "second");
        foreach (var (dir, text) in new[] { (first, firstText), (second, secondText) })
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "app.json"), $"{{\"id\":\"{SharedAppId}\",\"name\":\"Shared\",\"publisher\":\"AL Runner\",\"version\":\"1.0.0.0\"}}");
            File.WriteAllText(Path.Combine(dir, "Pair.Codeunit.al"), text);
        }
    }

    private string PackageRootHolding(string text)
    {
        var root = Path.Combine(_root, "package.src");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), "{}");
        File.WriteAllText(Path.Combine(root, "Pair.Codeunit.al"), text);
        return root;
    }

    private IReadOnlyList<string>? MapServed(IReadOnlyList<string> registered, string packageRoot, Guid appId)
        => AlCoverageSourceMap.Build(AlCoverageSourceMap.RootsForRegisteredDirs(
                registered, Array.Empty<string>(), new[] { (appId, packageRoot) }))
            .ObjectSourceLines("CodeUnit", 63701);

    // Either registration order, either package text: the object is the package's, which is what
    // ran. Both orders, because "the last registered wins" is right for exactly one of them.
    [SkippableFact]
    public void TwoUnmarkedFolders_TakeTheTextOfThePackageThatLoaded_InEitherOrder()
    {
        RequireEngine();
        RecordPatches.ResetForReload();
        WriteSharedAppFolders(string.Join("\n", CompiledLines) + "\n", string.Join("\n", SiblingLines) + "\n",
            out var compiledFolder, out var siblingFolder);
        var compiledPackage = PackageRootHolding(string.Join("\n", CompiledLines) + "\n");

        Assert.Equal(CompiledObjectB, MapServed(new[] { compiledFolder, siblingFolder }, compiledPackage, SharedAppId));
        Assert.Equal(CompiledObjectB, MapServed(new[] { siblingFolder, compiledFolder }, compiledPackage, SharedAppId));

        var siblingPackage = PackageRootHolding(string.Join("\n", SiblingLines) + "\n");
        Assert.Equal(SiblingObjectB, MapServed(new[] { compiledFolder, siblingFolder }, siblingPackage, SharedAppId));
        Assert.Equal(SiblingObjectB, MapServed(new[] { siblingFolder, compiledFolder }, siblingPackage, SharedAppId));
    }

    // The package of another app is not a root of this map: a package no registered folder carries
    // the id of is the #4984 case, and what the map serves for it did not change.
    [SkippableFact]
    public void APackageNoRegisteredFolderCarriesTheIdOf_IsNotAddedToTheRoots()
    {
        RequireEngine();
        RecordPatches.ResetForReload();
        WriteSharedAppFolders(string.Join("\n", CompiledLines) + "\n", string.Join("\n", SiblingLines) + "\n",
            out var first, out var second);
        var otherPackage = PackageRootHolding(string.Join("\n", CompiledLines) + "\n");

        var roots = AlCoverageSourceMap.RootsForRegisteredDirs(
            new[] { first, second }, Array.Empty<string>(), new[] { (Guid.NewGuid(), otherPackage) });

        Assert.Equal(new[] { first, second }, roots);
        Assert.Empty(roots.PackagedRootOfSibling);
        // Last registered wins, as before the package existed.
        Assert.Equal(SiblingObjectB, MapServed(new[] { first, second }, otherPackage, Guid.NewGuid()));
    }

    // The production route, with a real package: the served text follows the package that is
    // REGISTERED, and a package registered after the map was served changes it (the memo key).
    [SkippableFact]
    public void TheServedText_FollowsThePackageTheLoaderRegistered_EvenAfterTheMapWasServed()
    {
        RequireEngine();
        RecordPatches.ResetForReload();
        var fixtureText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            "Fixtures", "CoverageDependencySource", "dep", "CdsSubject.Codeunit.al"));
        var edited = "\n" + fixtureText;
        WriteSharedAppFolders(fixtureText, edited, out var compiledFolder, out var editedFolder);
        // Same app id as the package BuildSubjectApp writes.
        foreach (var dir in new[] { compiledFolder, editedFolder })
            File.WriteAllText(Path.Combine(dir, "app.json"),
                $"{{\"id\":\"{CoveragePackagedDependencyTests.DepAppId}\",\"name\":\"Shared\",\"publisher\":\"AL Runner\",\"version\":\"1.0.0.0\"}}");
        foreach (var f in new[] { compiledFolder, editedFolder })
            File.Move(Path.Combine(f, "Pair.Codeunit.al"), Path.Combine(f, "CdsSubject.Codeunit.al"));
        RecordPatches.AddSourceDirs(new[] { compiledFolder });
        RecordPatches.AddSourceDirs(new[] { editedFolder });
        // What each folder alone serves: the compiled one's text, and the edited one's.
        var compiledAlone = AlCoverageSourceMap.Build(new[] { compiledFolder }).ObjectSourceLines("CodeUnit", 70860)!;
        var editedAlone = AlCoverageSourceMap.Build(new[] { editedFolder }).ObjectSourceLines("CodeUnit", 70860)!;
        Assert.NotEqual(compiledAlone, editedAlone);

        // Nothing loaded a package yet: the last registered folder wins, as before.
        Assert.Equal(editedAlone, CodeCoveragePatches.SourceCodeLinesFor(new { ObjectType = "Codeunit", ObjectNumber = 70860 }).SourceCodeLines);

        var app = Path.Combine(_root, "dep.app");
        File.WriteAllBytes(app, CoveragePackagedDependencyTests.BuildSubjectApp());
        PackagedDependencySources.Register(Guid.Parse(CoveragePackagedDependencyTests.DepAppId), "AL Runner", app,
            "cc5259-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(app)))[..16]);

        Assert.Equal(compiledAlone, CodeCoveragePatches.SourceCodeLinesFor(new { ObjectType = "Codeunit", ObjectNumber = 70860 }).SourceCodeLines);
    }
}
