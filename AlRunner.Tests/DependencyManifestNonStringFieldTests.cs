// DependencyManifestNonStringFieldTests — issue #2560, defect 3.
//
// Program.cs's ReadDependencies read each `dependencies[]` entry's id/name/publisher/
// version via an unguarded JsonElement.GetString(), so a hand-edited/malformed app.json
// with a non-string field (e.g. `"name": 123`) raised InvalidOperationException straight
// out of the resolution path. TryReadManifestDependencyRoots's own catch-all (a pre-scan,
// ProvisioningCheck.cs) protects ONE caller; ReadDependencies is also called directly
// (the per-suite dependency-resolve path, and EnsurePlatformAppsProvisioned's manifest
// scan) with no such wrapper, so the exception was unhandled there.
//
// The fix must do neither of the two easy-but-wrong things: crash (the original bug), or
// silently `continue` past the whole malformed entry (which drops a real dependency edge
// just as silently, the same class of problem the other way — see the issue's own "Test"
// section). It must print a diagnostic naming the file and the specific entry.
//
// Spawns the real runner; needs the BC artifact cache to get far enough to reach app.json
// dependency resolution at all (BC version selection happens first). Skips when absent.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public class DependencyManifestNonStringFieldTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(string bundleDir, string absentPackageCache)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{bundleDir}\"");
        args.Append($" --package-cache \"{absentPackageCache}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(120_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    [SkippableFact]
    public void NumericDependencyName_DoesNotThrow_NamesTheFileAndEntry_StillResolvesOtherDeps()
    {
        TestArtifacts.SkipIfMissing();

        var scratchRoot = Path.Combine(Path.GetTempPath(), "al-runner-nonstring-dep", Guid.NewGuid().ToString("N"));
        var depDir = Path.Combine(scratchRoot, "dep-app");
        var testsDir = Path.Combine(scratchRoot, "tests-app");
        var absentPackageCache = Path.Combine(scratchRoot, "no-such-package-cache");
        Directory.CreateDirectory(depDir);
        Directory.CreateDirectory(testsDir);

        var depId = Guid.NewGuid();
        var testsId = Guid.NewGuid();

        // A real, resolvable dependency alongside the malformed entry — proves the
        // malformed entry degrades on its OWN field rather than dropping the whole
        // dependencies array (a bug that would also silently break every OTHER entry).
        File.WriteAllText(Path.Combine(depDir, "app.json"), $$"""
        {
          "id": "{{depId}}",
          "name": "Repro2560 Dep App",
          "publisher": "Repro2560",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 61960, "to": 61969 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(depDir, "Repro2560Dep.al"), """
        codeunit 61960 "Repro2560 Greeter"
        {
            procedure Greet(): Text
            begin
                exit('hello');
            end;
        }
        """);

        // The `dependencies` array has TWO entries: a real, resolvable one (the dep app
        // above) and one with a numeric `name` field — the exact malformed shape #2560
        // reports. Also a numeric `id`, to prove both string-typed fields degrade
        // independently rather than one bad field taking down parsing of the others.
        File.WriteAllText(Path.Combine(testsDir, "app.json"), $$"""
        {
          "id": "{{testsId}}",
          "name": "Repro2560 Tests",
          "publisher": "Repro2560",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{depId}}", "name": "Repro2560 Dep App", "publisher": "Repro2560", "version": "1.0.0.0" },
            { "id": 999, "name": 123, "publisher": "Repro2560", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 61970, "to": 61979 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(testsDir, "Repro2560Tests.al"), """
        codeunit 61970 "Repro2560 Poison Test"
        {
            Subtype = Test;

            [Test]
            procedure GreeterWorks()
            var
                Greeter: Codeunit "Repro2560 Greeter";
                Result: Text;
            begin
                Result := Greeter.Greet();
                if Result <> 'hello' then
                    Error('Expected hello, got ''%1''', Result);
            end;
        }
        """);

        var (output, exit) = RunRunner(testsDir, absentPackageCache);

        // The decisive negative claim: no UNHANDLED exception took the process down — the
        // .NET runtime's own marker for that ("Unhandled exception. <type>: ...", followed
        // by a raw CLR stack trace) must not appear. Not asserting the exception TYPE/
        // MESSAGE never appears anywhere in the log: a separate, already-caught reader
        // (InProcessAppPackager, a redundant app.json read elsewhere in the pipeline) hits
        // the identical JsonElement type mismatch and logs it as "failed to read ...",
        // which is a legitimate, non-crashing occurrence of the same .NET exception text —
        // this test's claim is about ReadDependencies specifically, proven by the
        // named-diagnostic and still-resolves assertions below, not by banning a substring
        // that a DIFFERENT, already-safe code path also produces.
        Assert.DoesNotContain("Unhandled exception", output);

        // Named diagnostic: which file, which entry (index 1, the second dependencies[]
        // element), which property.
        var appJsonPath = Path.Combine(testsDir, "app.json");
        Assert.Contains(appJsonPath, output);
        Assert.Contains("dependencies[1]", output);
        Assert.Contains("name", output);

        // Not a silent drop of the WHOLE dependencies array over one bad entry: the real,
        // resolvable dependency (entry 0) still resolves and the test that calls into it
        // still passes.
        Assert.True(exit == 0 && output.Contains("1P/0F/0E"), $"the real dependency must still resolve and the test must still pass:\n{output}");
    }
}
