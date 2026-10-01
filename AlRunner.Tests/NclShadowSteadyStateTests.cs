// NclShadowSteadyStateTests — #5019 and #5018 end to end, through the real runner.
//
// A warm start of the shadow child must not write its Ncl.dll again: that write is what leaves
// a ~RF*.TMP backup on Windows when another process has the file loaded (#5019) and what opens
// the ReplaceFile window a starting sibling can fall into (#5018). Linux cannot show either
// symptom, so the observable here is the cause: the file's modification time survives a warm
// start, and a planted backup is reaped. NclFilePublisherTests pins the pieces.
//
// A private --cache root keeps the shadow dir to this test alone. Spawns the real runner;
// skips when the BC artifact cache is absent.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class NclShadowSteadyStateTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(string bundleDir, string packageCache, string cacheRoot)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{bundleDir}\" --package-cache \"{packageCache}\" --cache \"{cacheRoot}\" --verbose");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        // This test edits the shadow dir's files, so it must own them (#5109).
        SharedEngineCaches.Isolate(psi);
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    [SkippableFact]
    public void WarmStart_LeavesTheShadowNclUnwritten_AndReapsReplaceFileBackups()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = TestScratch.Dir("ncl-shadow-steady-state");
        var bundleDir = Path.Combine(scratch, "tests-app");
        var cacheRoot = Path.Combine(scratch, "cache");
        var packageCache = Path.Combine(scratch, "no-such-package-cache");
        Directory.CreateDirectory(bundleDir);
        File.WriteAllText(Path.Combine(bundleDir, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "Repro5019 Tests",
          "publisher": "Repro5019",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 61970, "to": 61979 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(bundleDir, "Repro5019Tests.al"), """
        codeunit 61970 "Repro5019 Steady Test"
        {
            Subtype = Test;

            [Test]
            procedure TrivialPass()
            begin
                if 1 + 1 <> 2 then
                    Error('arithmetic is broken');
            end;
        }
        """);

        // Cold: builds the shadow dir and the ncl-cecil entry.
        var (cold, coldExit) = RunRunner(bundleDir, packageCache, cacheRoot);
        Assert.True(coldExit == 0 && cold.Contains("1P/0F/0E"), $"cold run must pass:\n{cold}");

        var shadowRoot = Path.Combine(cacheRoot, "ncl-shadow");
        var shadowDirs = Directory.Exists(shadowRoot)
            ? Directory.GetDirectories(shadowRoot).Where(d => !Path.GetFileName(d).Contains(".building.")).ToArray()
            : Array.Empty<string>();
        Assert.True(shadowDirs.Length == 1,
            $"expected exactly one published shadow dir under {shadowRoot}, found {shadowDirs.Length}:\n{cold}");
        var shadowNcl = Path.Combine(shadowDirs[0], "Microsoft.Dynamics.Nav.Ncl.dll");
        Assert.True(File.Exists(shadowNcl), $"the shadow dir holds no Ncl.dll: {shadowNcl}");
        var bytesBefore = File.ReadAllBytes(shadowNcl);

        // Two warm starts against the same cache root (local-test-scope.md: a cache sits between
        // the change and its observable). Each gets a fresh marker time and a fresh backup.
        for (var warm = 1; warm <= 2; warm++)
        {
            var marker = new DateTime(2001, 1, warm, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(shadowNcl, marker);
            var backup = Path.Combine(shadowDirs[0], $"Microsoft.Dynamics.Nav.Ncl.dll~RF{warm}A2B.TMP");
            File.WriteAllBytes(backup, bytesBefore);

            var (output, exit) = RunRunner(bundleDir, packageCache, cacheRoot);
            Assert.True(exit == 0 && output.Contains("1P/0F/0E"), $"warm run {warm} must pass:\n{output}");
            Assert.Contains("[Cecil] Cecil cache HIT", output);

            Assert.True(File.GetLastWriteTimeUtc(shadowNcl) == marker,
                $"warm run {warm} rewrote {shadowNcl} although it already held the cached bytes "
                + $"(mtime {File.GetLastWriteTimeUtc(shadowNcl):O}, expected {marker:O}):\n{output}");
            Assert.Equal(bytesBefore, File.ReadAllBytes(shadowNcl));
            Assert.False(File.Exists(backup), $"warm run {warm} left the ReplaceFile backup {backup}:\n{output}");
        }
    }
}
