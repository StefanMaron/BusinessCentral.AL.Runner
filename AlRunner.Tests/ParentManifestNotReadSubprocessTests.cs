// #4071 / #4076: a folder with no app.json, run directly, compiles with no manifest even when a
// PARENT folder's app.json declares preprocessorSymbols — the compile never climbs to
// ../app.json (#2542). The runner's own source parse and the PARTIAL-EMIT-DROP census must read
// the same nothing, or they pick the other #if branch from the compile.
//
// The census is BC's syntax tree (#5552), so a ghost declaration in a comment no longer makes the
// guard fire. The two census cases force a drop instead: AL_RUNNER_TEST_DROP_EMITTED_OBJECT removes
// one emitted source by name, so the object stays declared and the guard sees it unaccounted for.
//
// The last case is the other direction: the app's OWN manifest defines the symbol, so the census
// must count a codeunit guarded by it — reading no manifest would blank that codeunit and hide a
// drop elsewhere.
//
// Runner-specific: real BC has one parse. No "application" dependency
// (.claude/rules/no-base-app-in-csharp-tests.md); each test raises its own Error().
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class ParentManifestNotReadSubprocessTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root = TestScratch.Dir("al-runner-parent-manifest-not-read");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static (string output, int exit) RunRunner(string bundle, string? dropEmittedObject = null)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        // The guard runs on an emit; a warm al-out cache HIT skips it, and the seam with it.
        if (dropEmittedObject != null) args.Append(" --no-cache");
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

    private string FolderUnderParentDefiningSymbol(string name)
    {
        var parent = Path.Combine(_root, name);
        Directory.CreateDirectory(parent);
        File.WriteAllText(Path.Combine(parent, "app.json"), """
            {
              "id": "b4071000-0000-4000-8000-000000004071",
              "name": "ParentManifest4071",
              "publisher": "Repro4071",
              "version": "1.0.0.0",
              "dependencies": [],
              "platform": "1.0.0.0",
              "idRanges": [ { "from": 64071, "to": 64079 } ],
              "runtime": "14.0",
              "preprocessorSymbols": [ "PARENT_ONLY_4071" ]
            }
            """);
        var folder = Path.Combine(parent, "folder");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Probe.Codeunit.al"), """
            codeunit 64071 "Parent Manifest Probe"
            {
                Subtype = Test;

                [Test]
                procedure CompiledCodeunitIsListed()
                var
                    AllObj: Record AllObj;
                begin
                    AllObj.SetRange("Object Type", AllObj."Object Type"::Codeunit);
                    AllObj.SetRange("Object ID", 64072);
                    if AllObj.Count() <> 1 then
                        Error('AllObj rows for codeunit 64072: expected 1, got %1', AllObj.Count());
                end;
            }
            """);
        return folder;
    }

    [SkippableFact]
    public void SourceParse_FolderWithoutManifest_ListsTheCodeunitTheCompileCompiled()
    {
        TestArtifacts.SkipIfMissing();
        var folder = FolderUnderParentDefiningSymbol("allobj");
        // Compiled: the compile reads no manifest, so PARENT_ONLY_4071 is undefined.
        File.WriteAllText(Path.Combine(folder, "Compiled.Codeunit.al"), """
            #if not PARENT_ONLY_4071
            codeunit 64072 "Parent Manifest Compiled"
            {
                procedure Touch(): Integer
                begin
                    exit(64072);
                end;
            }
            #endif
            """);

        var (output, _) = RunRunner(folder);

        Assert.Contains("PASS  Codeunit64071.CompiledCodeunitIsListed", output);
        Assert.DoesNotContain("AllObj rows for codeunit 64072", output);
    }

    [SkippableFact]
    public void Census_FolderWithoutManifest_StillCountsWhatTheCompileSaw()
    {
        TestArtifacts.SkipIfMissing();
        var folder = FolderUnderParentDefiningSymbol("census");
        File.WriteAllText(Path.Combine(folder, "Compiled.Codeunit.al"), """
            codeunit 64072 "Parent Manifest Compiled"
            {
            }
            """);
        // An object the compile emits (a branch ACTIVE for it, since it reads no manifest) and
        // that the seam then hides. The census must still count it: under the parent's symbols
        // that branch is inactive, the object would not be declared, and the drop would pass.
        File.WriteAllText(Path.Combine(folder, "Ghost.al"), """
            #if not PARENT_ONLY_4071
            codeunit 64073 "Parent Manifest Ghost"
            {
            }
            #endif
            """);

        var (output, _) = RunRunner(folder, dropEmittedObject: "Parent Manifest Ghost");

        Assert.Contains("PARTIAL-EMIT-DROP", output);
        Assert.Contains("Parent Manifest Ghost", output);
    }

    [SkippableFact]
    public void Census_OwnManifestDefinesSymbol_CountsTheGuardedCodeunit_SoADropStillFires()
    {
        TestArtifacts.SkipIfMissing();
        var app = Path.Combine(_root, "own-manifest");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "app.json"), """
            {
              "id": "b4071000-0000-4000-8000-000000004072",
              "name": "OwnManifest4071",
              "publisher": "Repro4071",
              "version": "1.0.0.0",
              "dependencies": [],
              "idRanges": [ { "from": 64074, "to": 64079 } ],
              "runtime": "14.0",
              "preprocessorSymbols": [ "OWN_ONLY_4071" ]
            }
            """);
        File.WriteAllText(Path.Combine(app, "Probe.Codeunit.al"), """
            codeunit 64074 "Own Manifest Probe"
            {
                Subtype = Test;

                [Test]
                procedure Runs()
                begin
                end;
            }
            """);
        // Compiled and emitted: the app's own manifest defines the symbol. No #else.
        File.WriteAllText(Path.Combine(app, "Guarded.Codeunit.al"), """
            #if OWN_ONLY_4071
            codeunit 64075 "Own Manifest Guarded"
            {
            }
            #endif
            """);
        // An emitted object the seam then hides, so the guard has a drop to see.
        File.WriteAllText(Path.Combine(app, "Ghost.al"), """
            codeunit 64076 "Own Manifest Ghost"
            {
            }
            """);

        var (output, _) = RunRunner(app, dropEmittedObject: "Own Manifest Ghost");

        Assert.Contains("PARTIAL-EMIT-DROP", output);
        Assert.Contains("Own Manifest Ghost", output);
        Assert.Contains("Own Manifest Guarded", output);
    }
}
