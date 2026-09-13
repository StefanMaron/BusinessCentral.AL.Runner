// #4071 / #4076: a folder with no app.json, run directly, compiles with no manifest even when a
// PARENT folder's app.json declares preprocessorSymbols — the compile never climbs to
// ../app.json (#2542). The runner's own source parse and the PARTIAL-EMIT-DROP census must read
// the same nothing, or they pick the other #if branch from the compile.
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

    private static (string output, int exit) RunRunner(string bundle)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" \"").Append(bundle).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
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
        // A declaration line the census counts and the compile never emits, in a branch that is
        // ACTIVE for the compile (no manifest). The guard must still fire: blanking this region
        // under the parent's symbols is exactly the misread that silenced it.
        File.WriteAllText(Path.Combine(folder, "Ghost.al"), """
            #if not PARENT_ONLY_4071
            /*
            codeunit 64073 "Parent Manifest Ghost"
            */
            #endif
            """);

        var (output, _) = RunRunner(folder);

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
              "platform": "1.0.0.0",
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
        // A declaration line the census counts and the compile never emits.
        File.WriteAllText(Path.Combine(app, "Ghost.al"), """
            /*
            codeunit 64076 "Own Manifest Ghost"
            */
            """);

        var (output, _) = RunRunner(app);

        Assert.Contains("PARTIAL-EMIT-DROP", output);
        Assert.Contains("Own Manifest Ghost", output);
        Assert.Contains("Own Manifest Guarded", output);
    }
}
