// AlOutputCacheCompileInputsTests — #5368: the AL-output cache key hashed `*.al` and app.json, and
// BC's compiler also reads a report's layout file, a ControlAddIn's resources and the Translations
// folder. Change only one of those and the key was identical, so the next run was a HIT: deleting a
// layout the compile refuses (AL1081) kept passing.
//
// Each fact runs the real runner over its own bundle, twice or three times, on ONE cache root shared
// by the class (the engine's own caches are built once there). The bundle carries a per-fact marker
// in a `.al` file so every fact has its own cache key and none can serve, or overwrite, another's
// entry: any order, any subset.
//
// What is read off the output is the runner's own `[cache] HIT` / `[cache] MISS` line, never the
// result alone: a recompile and a replay both print PASSED.
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class AlOutputCacheCompileInputsFixture : IDisposable
{
    public string CacheDir { get; } = TestScratch.FlatDir("al-runner-cache-inputs-");

    public AlOutputCacheCompileInputsFixture() => Directory.CreateDirectory(CacheDir);

    public void Dispose()
    {
        try { Directory.Delete(CacheDir, recursive: true); } catch { /* best-effort cleanup */ }
    }
}

public sealed class AlOutputCacheCompileInputsTests
    : IClassFixture<AlOutputCacheCompileInputsFixture>, IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly AlOutputCacheCompileInputsFixture _cache;
    private readonly string _root = TestScratch.FlatDir("al-runner-cache-inputs-bundle-");

    public AlOutputCacheCompileInputsTests(AlOutputCacheCompileInputsFixture cache) => _cache = cache;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>A bundle whose compile reads a layout, a ControlAddIn resource and a Translations file.</summary>
    private string NewBundle(bool translations = true)
    {
        Write("app.json", """
            { "id": "c5368000-0000-4a11-9111-0000000000a1", "name": "CacheInputs", "publisher": "Test",
              "version": "1.0.0.0", "idRanges": [ { "from": 90500, "to": 90519 } ], "runtime": "14.0",
              "features": [ "TranslationFile" ] }
            """);
        Write("Tab.al", """
            table 90500 "CI Tab"
            {
                fields { field(1; PK; Integer) { } }
                keys { key(PK; PK) { Clustered = true; } }
            }
            """);
        Write("ReportA.al", """
            report 90501 "CI Report A"
            {
                DefaultRenderingLayout = L1;
                dataset { dataitem(T; "CI Tab") { column(PK; PK) { } } }
                rendering { layout(L1) { Type = RDLC; LayoutFile = 'Layouts/A.rdlc'; } }
            }
            """);
        Write("Addin.al", """
            controladdin "CI Addin"
            {
                Scripts = 'js/a.js';
                StartupScript = 'js/a.js';
            }
            """);
        // The marker gives this fact its own cache key.
        Write("Tests.al", $$"""
            // {{Guid.NewGuid():N}}
            codeunit 90503 "CI Tests"
            {
                Subtype = Test;

                [Test]
                procedure Arithmetic()
                begin
                    if 1 + 2 <> 3 then
                        Error('arithmetic must work');
                end;
            }
            """);
        Write("js/a.js", "// v1");
        Write("Layouts/A.rdlc", "<Report>v1</Report>");
        if (translations)
            Write("Translations/CacheInputs.da-DK.xlf", "<xliff version=\"1.2\"/>");
        Write("notes.txt", "never read by the compile");
        return _root;
    }

    private (string Output, int Exit) Run(string bundle)
    {
        var run = CacheCompileLockEndToEndTests.Start(bundle, _cache.CacheDir);
        return CacheCompileLockEndToEndTests.Finish(run);
    }

    private static bool Hit(string output) => output.Contains("[cache] HIT  key=", StringComparison.Ordinal);
    private static bool Miss(string output) => output.Contains("[cache] MISS key=", StringComparison.Ordinal);

    private static string KeyOf(string output)
    {
        var m = Regex.Match(output, @"\[cache\] MISS key=([0-9a-f]{64})");
        Assert.True(m.Success, $"no MISS line to read the cache key from:\n{output}");
        return m.Groups[1].Value;
    }

    /// <summary>Cold run, then a run that must be a HIT: every fact below starts from a real entry.</summary>
    private string Prime(string bundle)
    {
        var (cold, coldExit) = Run(bundle);
        Assert.True(coldExit == 0 && Miss(cold) && !Hit(cold), $"cold run: exit {coldExit}\n{cold}");
        var (warm, warmExit) = Run(bundle);
        Assert.True(warmExit == 0 && Hit(warm) && !Miss(warm), $"an unchanged bundle must be a HIT: exit {warmExit}\n{warm}");
        return KeyOf(cold);
    }

    [SkippableFact]
    public void AFileTheCompileNeverReads_IsStillAHit()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = NewBundle();
        Prime(bundle);

        Write("notes.txt", "edited, and still never read by the compile");

        var (output, exit) = Run(bundle);
        Assert.True(exit == 0 && Hit(output) && !Miss(output), $"exit {exit}\n{output}");
    }

    [SkippableFact]
    public void DeletedLayout_IsNotAHit_AndFailsAsAColdCompileDoes()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = NewBundle();
        Prime(bundle);

        File.Delete(Path.Combine(bundle, "Layouts", "A.rdlc"));

        var (output, exit) = Run(bundle);
        Assert.False(Hit(output), $"a deleted layout was served from the cache:\n{output}");
        Assert.True(Miss(output), output);
        Assert.Equal(3, exit);
        Assert.Contains("AL1081", output, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void EditedLayout_Recompiles_ThenTheNewStateIsAHit()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = NewBundle();
        Prime(bundle);

        Write("Layouts/A.rdlc", "<Report>v2</Report>");

        var (edited, editedExit) = Run(bundle);
        Assert.True(editedExit == 0 && Miss(edited) && !Hit(edited), $"exit {editedExit}\n{edited}");

        // The recompile republished the entry with the new fingerprints.
        var (again, againExit) = Run(bundle);
        Assert.True(againExit == 0 && Hit(again) && !Miss(again), $"exit {againExit}\n{again}");
    }

    [SkippableFact]
    public void EditedControlAddInResource_Recompiles()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = NewBundle();
        Prime(bundle);

        Write("js/a.js", "// v2");

        var (output, exit) = Run(bundle);
        Assert.True(exit == 0 && Miss(output) && !Hit(output), $"exit {exit}\n{output}");
    }

    [SkippableFact]
    public void EditedTranslationFile_Recompiles()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = NewBundle();
        Prime(bundle);

        Write("Translations/CacheInputs.da-DK.xlf", "<xliff version=\"1.2\"><!-- v2 --></xliff>");

        var (output, exit) = Run(bundle);
        Assert.True(exit == 0 && Miss(output) && !Hit(output), $"exit {exit}\n{output}");
    }

    // The count of files is the same; which name is not. (That a listing is hashed by names, apart from
    // the files read, is pinned in CompileFileReadsFingerprintTests.)
    [SkippableFact]
    public void RenamedTranslationFile_KeepingTheCount_Recompiles()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = NewBundle();
        Prime(bundle);

        File.Move(
            Path.Combine(bundle, "Translations", "CacheInputs.da-DK.xlf"),
            Path.Combine(bundle, "Translations", "CacheInputs.de-DE.xlf"));

        var (output, exit) = Run(bundle);
        Assert.True(exit == 0 && Miss(output) && !Hit(output), $"exit {exit}\n{output}");
    }

    // The compile looked, found no Translations folder, and cached that answer.
    [SkippableFact]
    public void ATranslationsFolderThatAppearsLater_Recompiles()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = NewBundle(translations: false);
        Prime(bundle);

        Write("Translations/CacheInputs.da-DK.xlf", "<xliff version=\"1.2\"/>");

        var (output, exit) = Run(bundle);
        Assert.True(exit == 0 && Miss(output) && !Hit(output), $"exit {exit}\n{output}");
    }

    // An entry an earlier runner wrote has no record of what its compile read.
    [SkippableFact]
    public void AnEntryWithNoRecordOfItsInputs_IsAMiss_AndGetsOne()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = NewBundle();
        var key = Prime(bundle);
        var record = Path.Combine(_cache.CacheDir, key + ".inputs.json");
        Assert.True(File.Exists(record), "the compile published no record of its inputs");

        File.Delete(record);

        var (output, exit) = Run(bundle);
        Assert.True(exit == 0 && Miss(output) && !Hit(output), $"exit {exit}\n{output}");
        Assert.True(File.Exists(record), "the recompile did not write the record back");
    }

    // The enum registry belongs to the DLL the record describes: a pair from two compiles is not an entry.
    [SkippableFact]
    public void AnEntryWhoseSidecarIsNotTheOneItWasPublishedWith_IsAMiss()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = NewBundle();
        var key = Prime(bundle);
        var sidecar = Path.Combine(_cache.CacheDir, key + ".enum-registry.json");
        File.WriteAllText(sidecar, File.ReadAllText(sidecar) + "\n");

        var (output, exit) = Run(bundle);
        Assert.True(exit == 0 && Miss(output) && !Hit(output), $"exit {exit}\n{output}");
    }

    // The record names its inputs relative to the app root, so a bundle that moves keeps its entry.
    [SkippableFact]
    public void ABundleMovedToAnotherDirectory_IsStillAHit()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = NewBundle();
        Prime(bundle);

        var moved = TestScratch.FlatDir("al-runner-cache-inputs-moved-");
        Directory.Move(bundle, moved);
        try
        {
            var (output, exit) = Run(moved);
            Assert.True(exit == 0 && Hit(output) && !Miss(output), $"exit {exit}\n{output}");

            File.Delete(Path.Combine(moved, "Layouts", "A.rdlc"));
            var (deleted, deletedExit) = Run(moved);
            Assert.True(Miss(deleted) && !Hit(deleted) && deletedExit == 3, $"exit {deletedExit}\n{deleted}");
        }
        finally
        {
            try { Directory.Delete(moved, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
