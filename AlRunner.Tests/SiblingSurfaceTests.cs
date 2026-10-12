// SiblingSurfaceTests — issue #4125, below the process boundary.
//
// An app whose own files are unchanged must still notice that a sibling app it compiles against
// changed its surface. The records here are what EmitSiblingSymbols hands the --watch loop (to skip
// the incremental replay) and the AL-output cache key (to miss). WatchSiblingDependentStaleTests
// proves the same thing end to end through a real --watch session.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class SiblingSurfaceRecordTests
{
    private static readonly Guid A = new("41250000-0000-4000-8000-0000000000a1");
    private static readonly Guid B = new("41250000-0000-4000-8000-0000000000b2");
    private static readonly Guid C = new("41250000-0000-4000-8000-0000000000c3");
    private static readonly Guid External = new("41250000-0000-4000-8000-0000000000e4");

    private static AppGroup Group(string name, Guid id, params Guid[] dependsOn) =>
        new(name, id, "AL Runner", new Version(1, 0, 0, 0), new List<string>(), dependsOn, "/suites/" + name);

    private static DependencyTerm T(string term, string name = "Lib") => new(term, name);

    [Fact]
    public void WhyNotReplayable_AnAppWithNoSuchDependency_IsAlwaysReplayable()
    {
        var memory = new SiblingSurfaceMemory();
        Assert.Null(memory.WhyNotReplayable("/suites/a", Array.Empty<DependencyTerm>()));
    }

    [Fact]
    public void WhyNotReplayable_NeedsASuccessfulCompileAgainstTheCurrentTerms_AndNamesWhatDiffers()
    {
        var memory = new SiblingSurfaceMemory();
        var s1 = new[] { T("sibling:a:1", "Lib"), T("sibling:b:1", "Mid") };

        Assert.Contains("no whole-module compile", memory.WhyNotReplayable("/suites/a", s1));
        memory.Built("/suites/a", s1);
        Assert.Null(memory.WhyNotReplayable("/suites/a", s1));
        Assert.Null(memory.WhyNotReplayable("/suites/a/", s1));   // the same suite spelled with a trailing slash

        // The surface the baseline was built against is not the current one, whatever happened in between.
        var s2 = new[] { T("sibling:a:2", "Lib"), T("sibling:b:1", "Mid") };
        Assert.Contains("'Lib'", memory.WhyNotReplayable("/suites/a", s2));
        Assert.DoesNotContain("'Mid'", memory.WhyNotReplayable("/suites/a", s2)!);
        // ...still true on the next cycle, and an undo to the old terms is replayable again.
        Assert.Contains("'Lib'", memory.WhyNotReplayable("/suites/a", s2));
        Assert.Null(memory.WhyNotReplayable("/suites/a", s1));

        // A dependency gained or lost is a difference too.
        Assert.NotNull(memory.WhyNotReplayable("/suites/a", s1.Take(1).ToArray()));
        Assert.NotNull(memory.WhyNotReplayable("/suites/a", s1.Append(T("sibling:c:1", "Extra")).ToArray()));
    }

    [Fact]
    public void WhyNotReplayable_AWholeModuleCompileThatStartsForgetsTheBaseline_UntilOneSucceeds()
    {
        var memory = new SiblingSurfaceMemory();
        var s1 = new[] { T("sibling:a:1") };
        memory.Built("/suites/a", s1);
        memory.Built("/suites/other", s1);

        memory.Forget("/suites/a");   // a compile started and may not finish
        Assert.NotNull(memory.WhyNotReplayable("/suites/a", s1));
        Assert.Null(memory.WhyNotReplayable("/suites/other", s1));   // each suite keeps its own
        memory.Built("/suites/a", s1);
        Assert.Null(memory.WhyNotReplayable("/suites/a", s1));
    }

    [Fact]
    public void CacheTerms_ListEverySiblingTheAppCompilesAgainst_ThroughTheChain_InAStableOrder()
    {
        var a = Group("Lib", A);
        var b = Group("Mid", B, A);
        var c = Group("Tests", C, B);
        var groups = new Dictionary<Guid, AppGroup> { [A] = a, [B] = b, [C] = c };
        var surfaces = new SiblingSurfaces(
            new Dictionary<Guid, string> { [A] = "fa", [B] = "fb" }, groups);

        Assert.Equal(new[] { $"sibling:{A:N}:fa", $"sibling:{B:N}:fb" }, surfaces.CacheTerms(c));
        Assert.Equal(new[] { $"sibling:{A:N}:fa" }, surfaces.CacheTerms(b));
        Assert.Empty(surfaces.CacheTerms(a));          // an app with no sibling dependency keeps its key
        Assert.Empty(SiblingSurfaces.None.CacheTerms(c));
    }

    [Fact]
    public void CacheTerms_FollowTheSurface_AndOnlyTheSiblingThatMoved()
    {
        var b = Group("Mid", B, A);
        var c = Group("Tests", C, B);
        var groups = new Dictionary<Guid, AppGroup> { [A] = Group("Lib", A), [B] = b, [C] = c };
        IReadOnlyList<string> Terms(string fa, string fb) => new SiblingSurfaces(
            new Dictionary<Guid, string> { [A] = fa, [B] = fb }, groups).CacheTerms(c);

        var before = Terms("fa", "fb");
        var after = Terms("fa2", "fb");
        Assert.NotEqual(before, after);
        Assert.Equal(before[1], after[1]);   // B's line is untouched
    }

    [Fact]
    public void CacheTerms_ASiblingWhoseSymbolsNeverArrived_IsAnExplicitNone_NotAnOmission()
    {
        var groups = new Dictionary<Guid, AppGroup> { [A] = Group("Lib", A) };
        var surfaces = new SiblingSurfaces(new Dictionary<Guid, string> { [C] = "x" }, groups);
        Assert.Equal(new[] { $"sibling:{A:N}:none" }, surfaces.CacheTerms(Group("Tests", C, A)));
    }

    [Fact]
    public void Fingerprint_HashesTheSymbolsAndTheSidecar_WithoutLettingTheBoundaryMove()
    {
        var dir = TestScratch.Dir("sibling-surface-fingerprint");
        Directory.CreateDirectory(dir);
        try
        {
            string Make(string symbols, string? sidecar)
            {
                var s = Path.Combine(dir, "s.symbols.json");
                var d = Path.Combine(dir, "s.symbols.deps.json");
                File.WriteAllText(s, symbols);
                if (sidecar == null) File.Delete(d); else File.WriteAllText(d, sidecar);
                return SiblingSurfaces.Fingerprint(s, d);
            }

            var baseline = Make("ab", "c");
            Assert.Equal(baseline, Make("ab", "c"));
            Assert.NotEqual(baseline, Make("ab", "d"));
            Assert.NotEqual(baseline, Make("ac", "c"));
            Assert.NotEqual(baseline, Make("a", "bc"));   // the same bytes split differently
            Assert.NotEqual(SiblingSurfaces.Unavailable, Make("ab", null));
            Assert.Equal(SiblingSurfaces.Unavailable,
                SiblingSurfaces.Fingerprint(Path.Combine(dir, "missing.json"), Path.Combine(dir, "s.symbols.deps.json")));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Fingerprint_ReadsTheSymbolsAsAValue_NotAsBytes()
    {
        var dir = TestScratch.Dir("sibling-surface-fingerprint-value");
        Directory.CreateDirectory(dir);
        try
        {
            string Of(string symbolsJson)
            {
                var s = Path.Combine(dir, "v.symbols.json");
                File.WriteAllText(s, symbolsJson);
                return SiblingSurfaces.Fingerprint(s, Path.Combine(dir, "none.deps.json"));
            }

            var whole = Of("""{"Name":"L","Codeunits":[{"Id":1,"Methods":[{"Id":7,"Name":"A","Parameters":[{"Name":"X"},{"Name":"Y"}]}]}]}""");
            // The incremental writer's spelling of the same surface: empty lists written out,
            // properties in another order, a UTF-8 byte order mark.
            Assert.Equal(whole, Of("\uFEFF" + """{"Tables":[],"Codeunits":[{"Methods":[{"Name":"A","Parameters":[{"Name":"X"},{"Name":"Y"}],"Id":7,"Events":[]}],"Id":1}],"Pages":null,"Name":"L"}"""));
            // A different surface: the parameter order, a method id, a method name.
            Assert.NotEqual(whole, Of("""{"Name":"L","Codeunits":[{"Id":1,"Methods":[{"Id":7,"Name":"A","Parameters":[{"Name":"Y"},{"Name":"X"}]}]}]}"""));
            Assert.NotEqual(whole, Of("""{"Name":"L","Codeunits":[{"Id":1,"Methods":[{"Id":8,"Name":"A","Parameters":[{"Name":"X"},{"Name":"Y"}]}]}]}"""));
            Assert.NotEqual(whole, Of("""{"Name":"L","Codeunits":[{"Id":1,"Methods":[{"Id":7,"Name":"B","Parameters":[{"Name":"X"},{"Name":"Y"}]}]}]}"""));
            // Not JSON: hashed as bytes, so any difference still counts.
            Assert.NotEqual(Of("not json"), Of("not json!"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [SkippableFact]
    public void AlOutputKey_FollowsTheSiblingTerms_AndNoTermsLeavesTheKeyWhatItWas()
    {
        TestArtifacts.SkipIfMissing();
        var dir = TestScratch.Dir("sibling-surface-cache-key");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Object.Codeunit.al"), "codeunit 64090 \"SS Key\" { }");
            string Key(IReadOnlyList<string>? terms) => ProgramSupport.ComputeAlCacheKey(
                new[] { dir }, "SiblingKey", Array.Empty<string>(), dir, terms);

            var legacy = Key(null);
            Assert.Equal(legacy, Key(Array.Empty<string>()));
            var one = Key(new[] { $"sibling:{A:N}:fa" });
            Assert.NotEqual(legacy, one);
            Assert.NotEqual(one, Key(new[] { $"sibling:{A:N}:fa2" }));
            Assert.Equal(one, Key(new[] { $"sibling:{A:N}:fa" }));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}

/// <summary>
/// A real <c>EmitSiblingSymbols</c> over a library and the app that depends on it, pass after pass
/// in one process the way a --watch session runs it. The library's symbols are written by two
/// different writers (the whole-module compile, and the incremental merge), which spell one
/// unchanged surface differently; a pass only reports a move when the SURFACE moved.
/// </summary>
[Collection(BcEngineCollection.Name)]
public sealed class SiblingSurfaceStabilityTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public SiblingSurfaceStabilityTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-sibling-surface-stability");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        BcCompiler.SetSiblingSymbolsDir(null);
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static readonly Guid LibId = new("41250000-0000-4000-8000-0000000001a1");
    private static readonly Guid TestsId = new("41250000-0000-4000-8000-0000000001c3");

    private string Dir(string name) => Path.Combine(_root, name);

    private void WriteManifest(string dir, Guid id, string name, int from, string deps)
    {
        Directory.CreateDirectory(dir);
        var appJson = Path.Combine(dir, "app.json");
        File.WriteAllText(appJson, $$"""
        { "id": "{{id}}", "name": "{{name}}", "publisher": "AL Runner", "version": "1.0.0.0",
          "dependencies": [ {{deps}} ], "idRanges": [ { "from": {{from}}, "to": {{from + 9}} } ], "runtime": "14.0" }
        """);
        // BuildAppGroups reads every manifest, which is also what records its declared references;
        // the compile narrows to them (NarrowToDeclaredReferences).
        Assert.NotNull(InProcessAppPackager.ReadIdentity(appJson));
    }

    private void WriteLib(int value, string extraProcedure = "", string addSignature = "A: Integer; B: Text", string extraField = "",
        string codeunitProperty = "", string valueAttribute = "")
    {
        File.WriteAllText(Path.Combine(Dir("lib"), "Lib.Codeunit.al"), $$"""
        codeunit 64080 "SS Lib"
        {
            {{codeunitProperty}}
            {{valueAttribute}}
            procedure Value(): Integer
            begin
                exit({{value}});
            end;

            procedure Add({{addSignature}}): Integer
            begin
                exit(1);
            end;

            procedure Add(A: Integer): Integer
            begin
                exit(A);
            end;
        {{extraProcedure}}
        }
        """);
        File.WriteAllText(Path.Combine(Dir("lib"), "Rec.Table.al"), $$"""
        table 64081 "SS Rec"
        {
            fields
            {
                field(1; "Code"; Code[10]) { }
                field(2; Name; Text[30]) { }
        {{extraField}}
            }
            keys { key(PK; "Code") { } }
        }
        """);
        File.WriteAllText(Path.Combine(Dir("lib"), "Kind.Enum.al"), """
        enum 64082 "SS Kind"
        {
            Extensible = true;
            value(0; None) { }
            value(1; Big) { }
        }
        """);
    }

    private List<AppGroup> Groups() => new()
    {
        new("SS Lib", LibId, "AL Runner", new Version(1, 0, 0, 0), new List<string> { Dir("lib") }, Array.Empty<Guid>(), Dir("lib")),
        new("SS Tests", TestsId, "AL Runner", new Version(1, 0, 0, 0), new List<string> { Dir("tests") }, new[] { LibId }, Dir("tests")),
    };

    private SiblingSurfaces Pass() =>
        ProgramSupport.EmitSiblingSymbols(
            Groups(), _root, Array.Empty<(AppManifest Manifest, string AppPath)>(), announcePath: false);

    [SkippableFact]
    public void OnlyASurfaceChangeMovesTheTerms_WhicheverWriterSpelledThePreviousPass()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        WriteManifest(Dir("lib"), LibId, "SS Lib", 64080, "");
        WriteManifest(Dir("tests"), TestsId, "SS Tests", 64090,
            $$"""{ "id": "{{LibId}}", "name": "SS Lib", "publisher": "AL Runner", "version": "1.0.0.0" }""");
        File.WriteAllText(Path.Combine(Dir("tests"), "Tests.Codeunit.al"), "codeunit 64090 \"SS Tests\" { }");
        WriteLib(42);
        var tests = Groups()[1];
        var last = Pass().CacheTerms(tests);
        var first = last;
        Assert.Single(first);

        IReadOnlyList<string> Now() => Pass().CacheTerms(tests);
        void Same(string what) { var now = Now(); Assert.True(last.SequenceEqual(now), $"{what}: the terms moved, {string.Join(",", last)} -> {string.Join(",", now)}"); last = now; }
        void Moved(string what) { var now = Now(); Assert.False(last.SequenceEqual(now), $"{what}: the terms did not move"); last = now; }

        Same("nothing edited");
        WriteLib(43);
        Same("a body edit after the whole-module compile");
        WriteLib(44);
        Same("a body edit after an incremental one");

        const string Extra = "\n    procedure Extra(): Integer\n    begin\n        exit(58);\n    end;\n";
        WriteLib(44, extraProcedure: Extra);
        Moved("a procedure added");
        WriteLib(45, extraProcedure: Extra);
        Same("a body edit after the addition");

        WriteLib(45);
        Moved("the procedure removed again (a whole-module fallback)");
        WriteLib(46);
        Same("a body edit after a whole-module fallback");
        Assert.True(first.SequenceEqual(last), "back on the first surface the terms are the first ones");

        WriteLib(46, addSignature: "B: Text; A: Integer");
        Moved("two parameters swapped: the order is part of the surface");
        WriteLib(46, addSignature: "B: Text; A: Integer", extraField: "        field(3; Level; Integer) { }");
        Moved("a table field added");

        // What the symbols carry as Properties and Attributes is the surface too: the dependent is
        // compiled against it, and the compiler answers differently (access, obsolete).
        WriteLib(46, addSignature: "B: Text; A: Integer", extraField: "        field(3; Level; Integer) { }", codeunitProperty: "Access = Internal;");
        Moved("the codeunit made Access = Internal");
        WriteLib(46, addSignature: "B: Text; A: Integer", extraField: "        field(3; Level; Integer) { }", codeunitProperty: "Access = Internal;", valueAttribute: "[Obsolete('use Add', '1.0')]");
        Moved("a procedure marked [Obsolete]");
    }
}
